using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace PcMaintenance.Core.Network;

/// <summary>How far this PC's clock is from a time server, by one SNTP request (UDP port 123).</summary>
public static class TimeCheck
{
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <returns>Positive when the server is ahead (this clock is slow); null when the server did not answer in time.</returns>
    public static async Task<TimeSpan?> QueryOffsetAsync(string server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(server, AddressFamily.InterNetwork, timeout.Token);
            if (addresses.Length == 0) return null;
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var request = new byte[48];
            request[0] = 0x23; // no leap warning, version 4, client
            var sent = DateTime.UtcNow;
            await udp.SendAsync(request, new IPEndPoint(addresses[0], 123), timeout.Token);
            var reply = await udp.ReceiveAsync(timeout.Token);
            return Offset(reply.Buffer, sent, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The usual NTP estimate ((T2 − T1) + (T3 − T4)) / 2 from a server reply.</summary>
    internal static TimeSpan? Offset(byte[] reply, DateTime sentUtc, DateTime receivedUtc)
    {
        // A server reply (mode 4) is at least 48 bytes; a zero transmit time means "no time to give" (a kiss-of-death).
        if (reply.Length < 48 || (reply[0] & 0x07) != 4) return null;
        var serverReceived = Timestamp(reply, 32);
        var serverSent = Timestamp(reply, 40);
        if (serverSent == NtpEpoch) return null;
        return ((serverReceived - sentUtc) + (serverSent - receivedUtc)) / 2;
    }

    internal static DateTime Timestamp(byte[] data, int offset)
    {
        ulong seconds = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        ulong fraction = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4));
        if (seconds == 0 && fraction == 0) return NtpEpoch;
        // Era 1 starts in February 2036, when the 32-bit seconds wrap; until then the top bit is always set.
        if ((seconds & 0x8000_0000) == 0) seconds += 0x1_0000_0000;
        return NtpEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond + (fraction * TimeSpan.TicksPerSecond >> 32)));
    }
}

/// <summary>The signal quality (0–100) Windows reports for a connected Wi-Fi card, from the WLAN API.</summary>
public static class WifiSignal
{
    private const int CurrentConnection = 7;   // wlan_intf_opcode_current_connection
    private const int InterfaceInfoSize = 532; // GUID, WCHAR[256] description, state
    private const int SignalQualityOffset = 576;

    /// <param name="interfaceId">NetworkInterface.Id, the card's GUID in braces.</param>
    public static int? Read(string interfaceId)
    {
        if (!Guid.TryParse(interfaceId, out var wanted)) return null;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return null; // e.g. WLAN AutoConfig not running
            try
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return null;
                try
                {
                    var count = Marshal.ReadInt32(list);
                    for (var i = 0; i < count; i++)
                    {
                        var guid = Marshal.PtrToStructure<Guid>(list + 8 + i * InterfaceInfoSize);
                        if (guid != wanted) continue;
                        if (WlanQueryInterface(handle, ref guid, CurrentConnection, IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0) return null;
                        try
                        {
                            return size >= SignalQualityOffset + 4 ? Marshal.ReadInt32(data, SignalQualityOffset) : null;
                        }
                        finally
                        {
                            WlanFreeMemory(data);
                        }
                    }
                    return null;
                }
                finally
                {
                    WlanFreeMemory(list);
                }
            }
            finally
            {
                WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // Windows Server without the wireless feature
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opCodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}

public enum ProxyKind
{
    None,
    Server,
    Script,
}

/// <summary>The current user's proxy as set in Windows' internet settings, which browsers and this program follow.</summary>
public sealed record ProxySetting(ProxyKind Kind, string Text)
{
    public static ProxySetting Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("AutoConfigURL") is string { Length: > 0 } script) return new(ProxyKind.Script, $"自動設定指令碼 {script}");
            if (key?.GetValue("ProxyEnable") is 1 && key.GetValue("ProxyServer") is string { Length: > 0 } server) return new(ProxyKind.Server, $"Proxy {server}");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return new(ProxyKind.None, "");
    }
}

public sealed record HostsEntry(string Address, string Name);

/// <param name="Blocked">Names of update and security sites the file redirects, the usual sign of malware.</param>
public sealed record HostsCheck(bool Readable, IReadOnlyList<HostsEntry> Entries, IReadOnlyList<string> Blocked);

/// <summary>Reads the hosts file, which sends names to fixed addresses before DNS is asked.</summary>
public static class HostsFile
{
    public static readonly string Path = System.IO.Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    // Sites malware cuts off so the PC can neither update nor be cleaned.
    private static readonly string[] Watched =
    [
        "microsoft.com", "windowsupdate", "msftconnecttest", "live.com", "bing.com",
        "symantec", "norton", "mcafee", "kaspersky", "avast", "avg.com", "eset", "trendmicro", "bitdefender", "sophos",
        "malwarebytes", "virustotal",
    ];

    public static HostsCheck Read()
    {
        try
        {
            return Parse(File.ReadAllLines(Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HostsCheck(false, [], []);
        }
    }

    public static HostsCheck Parse(IEnumerable<string> lines)
    {
        var entries = new List<HostsEntry>();
        foreach (var raw in lines)
        {
            var line = raw.Split('#', 2)[0];
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _)) continue;
            foreach (var name in parts.Skip(1))
            {
                // Windows' own default: the PC's names for itself.
                if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new HostsEntry(parts[0], name));
            }
        }
        var blocked = entries.Select(e => e.Name)
            .Where(n => Watched.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new HostsCheck(true, entries, blocked);
    }
}
