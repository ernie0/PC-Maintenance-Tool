using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace PcMaintenance.Core.Network;

/// <summary>Bytes a process sent and received since monitoring started.</summary>
public readonly record struct ProcessTraffic(long Sent, long Received);

/// <summary>
/// Counts each process's network traffic from Windows' own Microsoft-Windows-Kernel-Network events (what Resource
/// Monitor uses), in a real-time ETW session of this program. Needs elevation; nothing is captured or stored, only
/// byte counts per process id. Traffic inside the PC (loopback) is left out. The session is stopped on
/// <see cref="Dispose"/> and when the process exits, and one left behind by a crash is replaced on the next start.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    private const string SessionName = "PcMaintenance-NetworkTraffic";
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const ulong KeywordIPv4 = 0x10;
    private const ulong KeywordIPv6 = 0x20;
    private const byte LevelInformation = 4;

    private readonly ConcurrentDictionary<int, Counter> _counters = new();
    private readonly EtwNative.EventRecordCallback _callback;
    private readonly Thread _reader;
    private ulong _session;
    private ulong _trace = EtwNative.InvalidTraceHandle;
    private bool _disposed;

    private TrafficMonitor()
    {
        _callback = OnEvent;
        _session = StartSession();
        try
        {
            var provider = KernelNetwork;
            Check(EtwNative.EnableTraceEx2(_session, ref provider, EtwNative.EVENT_CONTROL_CODE_ENABLE_PROVIDER, LevelInformation,
                KeywordIPv4 | KeywordIPv6, 0, 0, IntPtr.Zero));
            var logFile = new EtwNative.EVENT_TRACE_LOGFILEW
            {
                LoggerName = SessionName,
                ProcessTraceMode = EtwNative.PROCESS_TRACE_MODE_REAL_TIME | EtwNative.PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = _callback,
            };
            _trace = EtwNative.OpenTraceW(ref logFile);
            if (_trace == EtwNative.InvalidTraceHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            StopSession();
            throw;
        }
        _reader = new Thread(Read) { IsBackground = true, Name = "網路流量事件" };
        _reader.Start();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>False once Windows stopped delivering events, e.g. because another window took the session over.</summary>
    public bool IsRunning { get; private set; } = true;

    /// <summary>Starts monitoring; throws <see cref="UnauthorizedAccessException"/> without elevation.</summary>
    public static TrafficMonitor Start() => new();

    public IReadOnlyDictionary<int, ProcessTraffic> Snapshot() =>
        _counters.ToDictionary(kv => kv.Key, kv => new ProcessTraffic(Interlocked.Read(ref kv.Value.Sent), Interlocked.Read(ref kv.Value.Received)));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        StopSession();
        _reader.Join(TimeSpan.FromSeconds(5));
        if (_trace != EtwNative.InvalidTraceHandle) EtwNative.CloseTrace(_trace);
        _trace = EtwNative.InvalidTraceHandle;
    }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    private void Read()
    {
        var handles = new[] { _trace };
        EtwNative.ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        IsRunning = false;
    }

    private void OnEvent(IntPtr record)
    {
        try
        {
            if (Marshal.PtrToStructure<Guid>(record + EtwNative.ProviderIdOffset) != KernelNetwork) return;
            if (Parse((ushort)Marshal.ReadInt16(record, EtwNative.EventIdOffset), Marshal.ReadIntPtr(record, EtwNative.UserDataOffset),
                    (ushort)Marshal.ReadInt16(record, EtwNative.UserDataLengthOffset)) is not { } packet) return;
            var counter = _counters.GetOrAdd(packet.ProcessId, _ => new Counter());
            if (packet.IsSend) Interlocked.Add(ref counter.Sent, packet.Bytes);
            else Interlocked.Add(ref counter.Received, packet.Bytes);
        }
        catch
        {
            // A malformed event must not stop the reader thread; it is simply not counted.
        }
    }

    /// <summary>
    /// The payload starts with PID, size, destination and source address, destination and source port (from the
    /// provider's manifest: "%2 bytes transmitted from %4:%6 to %3:%5"); IPv6 events carry 16-byte addresses.
    /// </summary>
    internal static TrafficPacket? Parse(ushort eventId, IntPtr data, int length)
    {
        var (isSend, addressSize) = eventId switch
        {
            10 or 42 => (true, 4),
            11 or 43 => (false, 4),
            26 or 58 => (true, 16),
            27 or 59 => (false, 16),
            _ => (false, 0),
        };
        if (addressSize == 0 || length < 8 + 2 * addressSize) return null;
        var destination = new byte[addressSize];
        var source = new byte[addressSize];
        Marshal.Copy(data + 8, destination, 0, addressSize);
        Marshal.Copy(data + 8 + addressSize, source, 0, addressSize);
        if (IPAddress.IsLoopback(new IPAddress(destination)) || IPAddress.IsLoopback(new IPAddress(source))) return null;
        return new TrafficPacket(Marshal.ReadInt32(data), (uint)Marshal.ReadInt32(data, 4), isSend);
    }

    private static ulong StartSession()
    {
        using var properties = EtwNative.SessionProperties.Create(SessionName);
        var result = EtwNative.StartTraceW(out var session, SessionName, properties.Pointer);
        if (result == EtwNative.ERROR_ALREADY_EXISTS)
        {
            // Left behind by a run that crashed (or used by another window): take it over.
            using var stop = EtwNative.SessionProperties.Create(SessionName);
            EtwNative.ControlTraceW(0, SessionName, stop.Pointer, EtwNative.EVENT_TRACE_CONTROL_STOP);
            using var retry = EtwNative.SessionProperties.Create(SessionName);
            result = EtwNative.StartTraceW(out session, SessionName, retry.Pointer);
        }
        Check(result);
        return session;
    }

    private void StopSession()
    {
        if (_session == 0) return;
        using var properties = EtwNative.SessionProperties.Create(SessionName);
        EtwNative.ControlTraceW(_session, null, properties.Pointer, EtwNative.EVENT_TRACE_CONTROL_STOP);
        _session = 0;
    }

    private static void Check(int result)
    {
        if (result == EtwNative.ERROR_ACCESS_DENIED) throw new UnauthorizedAccessException("查看各程式的網路流量需要系統管理員權限");
        if (result != 0) throw new Win32Exception(result);
    }

    private sealed class Counter
    {
        public long Sent;
        public long Received;
    }
}

internal readonly record struct TrafficPacket(int ProcessId, long Bytes, bool IsSend);

/// <summary>ETW declarations for a real-time session (x64 layouts).</summary>
internal static class EtwNative
{
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_ALREADY_EXISTS = 183;
    internal const uint EVENT_TRACE_CONTROL_STOP = 1;
    internal const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    internal const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    internal const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    internal const ulong InvalidTraceHandle = ulong.MaxValue;

    // EVENT_RECORD: EVENT_HEADER (80 bytes: ProviderId at 24, EventDescriptor.Id at 40), buffer context, extended
    // data count, user data length at 86, extended data at 88, user data at 96.
    internal const int ProviderIdOffset = 24;
    internal const int EventIdOffset = 40;
    internal const int UserDataLengthOffset = 86;
    internal const int UserDataOffset = 96;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EventRecordCallback(IntPtr eventRecord);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint BufferCallback(IntPtr logFile);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    internal static extern int EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logFile);

    [DllImport("advapi32.dll")]
    internal static extern int ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    internal static extern int CloseTrace(ulong traceHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct EVENT_TRACE_LOGFILEW
    {
        public string? LogFileName;
        public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public BufferCallback? BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public EventRecordCallback? EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Class;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public uint KernelTime;
        public uint UserTime;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public uint StartBuffers;
        public uint PointerSize;
        public uint EventsLost;
        public uint CpuSpeedInMHz;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    /// <summary>
    /// EVENT_TRACE_PROPERTIES (120 bytes) followed by room for the session name, for a real-time session without a
    /// log file.
    /// </summary>
    internal sealed class SessionProperties : IDisposable
    {
        private const int Size = 120;
        private const int NameBytes = 1024 * sizeof(char);
        private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;

        private SessionProperties(IntPtr pointer) => Pointer = pointer;

        public IntPtr Pointer { get; }

        public static SessionProperties Create(string name)
        {
            var total = Size + NameBytes;
            var pointer = Marshal.AllocHGlobal(total);
            Marshal.Copy(new byte[total], 0, pointer, total);
            Marshal.WriteInt32(pointer, 0, total);                  // Wnode.BufferSize
            Marshal.WriteInt32(pointer, 40, 1);                     // Wnode.ClientContext: QueryPerformanceCounter clock
            Marshal.WriteInt32(pointer, 44, (int)WNODE_FLAG_TRACED_GUID); // Wnode.Flags
            Marshal.WriteInt32(pointer, 48, 64);                    // BufferSize in KB
            Marshal.WriteInt32(pointer, 64, (int)EVENT_TRACE_REAL_TIME_MODE); // LogFileMode
            Marshal.WriteInt32(pointer, 68, 1);                     // FlushTimer: deliver at least every second
            Marshal.WriteInt32(pointer, 116, Size);                 // LoggerNameOffset
            var chars = name.ToCharArray();
            Marshal.Copy(chars, 0, pointer + Size, chars.Length);
            return new SessionProperties(pointer);
        }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
