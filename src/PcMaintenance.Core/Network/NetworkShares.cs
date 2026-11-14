using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace PcMaintenance.Core.Network;

/// <param name="Name">"Z:" for a drive, the printer's name for a printer.</param>
/// <param name="Target">What it points at: "\\nas\共用" or "192.168.1.30:9100".</param>
/// <param name="Folder">The folder to open for a drive; null for a printer.</param>
public sealed record NetworkShare(bool IsPrinter, string Name, string Target, string Host, int Port, string? Folder, bool Offline = false);

public enum ShareReach
{
    Reachable,
    HostUnreachable,
    /// <summary>The server answered but the folder would not open: usually the account or its permissions.</summary>
    FolderRefused,
}

/// <summary>
/// The mapped network drives of this user and the printers that sit on the network, and whether each can be reached:
/// a TCP connection to the file server (port 445) or the printer's port, then for a drive the folder itself. Used by the
/// network check, which runs only when asked.
/// </summary>
public static class NetworkShares
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FolderTimeout = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<NetworkShare> Read() => [.. MappedDrives(), .. NetworkPrinters()];

    public static async Task<ShareReach> ProbeAsync(NetworkShare share, CancellationToken ct)
    {
        if (!await ConnectAsync(share.Host, share.Port, ct)) return ShareReach.HostUnreachable;
        if (share.Folder is not { } folder) return ShareReach.Reachable;
        // A folder on a slow or half-dead server can block for a long time; give up after a few seconds.
        var open = Task.Run(() => Directory.Exists(folder), ct);
        return await Task.WhenAny(open, Task.Delay(FolderTimeout, ct)) == open && open.Result ? ShareReach.Reachable : ShareReach.FolderRefused;
    }

    private static async Task<bool> ConnectAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>"\\server\share\folder" → "server".</summary>
    public static string? HostOf(string unc) =>
        unc.StartsWith(@"\\", StringComparison.Ordinal) && unc[2..].Split('\\')[0] is { Length: > 0 } host ? host : null;

    /// <summary>Drives remembered in HKCU\Network plus any connected right now.</summary>
    private static IEnumerable<NetworkShare> MappedDrives()
    {
        var drives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var network = Registry.CurrentUser.OpenSubKey("Network");
            foreach (var letter in network?.GetSubKeyNames() ?? [])
            {
                using var key = network!.OpenSubKey(letter);
                if (key?.GetValue("RemotePath") is string { Length: > 2 } path) drives[$"{letter.ToUpperInvariant()}:"] = path;
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network))
        {
            var letter = drive.Name[..2];
            if (!drives.ContainsKey(letter) && Connection(letter) is { } unc) drives[letter] = unc;
        }
        foreach (var (letter, unc) in drives.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (HostOf(unc) is { } host) yield return new NetworkShare(false, letter, unc, host, 445, unc);
        }
    }

    private static string? Connection(string letter)
    {
        var buffer = new StringBuilder(512);
        var size = buffer.Capacity;
        return WNetGetConnection(letter, buffer, ref size) == 0 ? buffer.ToString() : null;
    }

    /// <summary>Shared printers on a print server, and printers on a TCP/IP port (RAW 9100 or LPR 515).</summary>
    private static IEnumerable<NetworkShare> NetworkPrinters()
    {
        var ports = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);
        Query("SELECT Name, HostAddress, PortNumber, Protocol FROM Win32_TCPIPPrinterPort", item =>
        {
            if (item["Name"] is not string name || item["HostAddress"] is not string { Length: > 0 } host) return;
            var protocol = Convert.ToInt32(item["Protocol"] ?? 1);
            var port = protocol == 2 ? 515 : Convert.ToInt32(item["PortNumber"] ?? 9100);
            ports[name] = (host, port > 0 ? port : 9100);
        });
        var list = new List<NetworkShare>();
        Query("SELECT Name, Network, PortName, ServerName, WorkOffline FROM Win32_Printer", item =>
        {
            var name = item["Name"] as string ?? "";
            var offline = item["WorkOffline"] is true;
            // A shared printer is named "\\server\printer" and its server is "\\server".
            var server = item["ServerName"] as string ?? "";
            if (item["Network"] is true && HostOf(server.Length > 0 ? server : name) is { } host)
                list.Add(new NetworkShare(true, name, name.StartsWith(@"\\", StringComparison.Ordinal) ? name : server, host, 445, null, offline));
            else if (item["PortName"] is string port && ports.TryGetValue(port, out var tcp))
                list.Add(new NetworkShare(true, name, $"{tcp.Host}:{tcp.Port}", tcp.Host, tcp.Port, null, offline));
        });
        return list.OrderBy(p => p.Name, StringComparer.CurrentCulture);
    }

    private static void Query(string wql, Action<ManagementObject> read)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", wql);
            using var results = searcher.Get();
            foreach (var item in results.Cast<ManagementObject>())
            {
                using (item) read(item);
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
