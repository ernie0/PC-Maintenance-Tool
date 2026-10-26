using System.Runtime.InteropServices;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Performance;

/// <summary>
/// A performance-counter query. Counters are added by their English names so they work on Chinese Windows too;
/// rates need two collections, so the first value appears on the second <see cref="Collect"/>.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _handle;

    public PdhQuery()
    {
        if (NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _handle) != 0) _handle = IntPtr.Zero;
    }

    /// <summary>Null when the counter does not exist on this computer (for example GPU counters on old drivers).</summary>
    public PdhCounter? Add(string englishPath) =>
        _handle != IntPtr.Zero && NativeMethods.PdhAddEnglishCounterW(_handle, englishPath, IntPtr.Zero, out var counter) == 0 ? new PdhCounter(counter) : null;

    public bool Collect() => _handle != IntPtr.Zero && NativeMethods.PdhCollectQueryData(_handle) == 0;

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.PdhCloseQuery(_handle);
        _handle = IntPtr.Zero;
    }
}

internal sealed class PdhCounter(IntPtr handle)
{
    private const uint Format = NativeMethods.PDH_FMT_DOUBLE | NativeMethods.PDH_FMT_NOCAP100;

    public double? Read() =>
        NativeMethods.PdhGetFormattedCounterValue(handle, Format, IntPtr.Zero, out var value) == 0 && value.CStatus is 0 or 1 ? value.doubleValue : null;

    /// <summary>Every instance of a wildcard counter such as <c>\PhysicalDisk(*)\% Idle Time</c>, by instance name.</summary>
    public Dictionary<string, double> ReadAll()
    {
        var values = new Dictionary<string, double>();
        uint size = 0;
        if (NativeMethods.PdhGetFormattedCounterArrayW(handle, Format, ref size, out _, IntPtr.Zero) != NativeMethods.PDH_MORE_DATA) return values;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.PdhGetFormattedCounterArrayW(handle, Format, ref size, out var count, buffer) != 0) return values;
            const int itemSize = 24;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * itemSize;
                if (Marshal.ReadInt32(item, 8) is not (0 or 1) || Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) is not { } name) continue;
                // Instances can repeat (several processes with one name); their values add up.
                values[name] = values.GetValueOrDefault(name) + BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
