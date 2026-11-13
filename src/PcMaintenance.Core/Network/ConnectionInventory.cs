using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PcMaintenance.Core.Processes;

namespace PcMaintenance.Core.Network;

/// <summary>A connection with what the network tab and the export show about it.</summary>
public sealed record ConnectionInfo(
    NetworkConnection Connection, string ProgramName, string? ProgramPath, PortService? Service, AddressScope Scope, bool IsSuspicious)
{
    public IPAddress? RemoteAddress => Connection.Remote?.Address;

    /// <summary>Traffic that never leaves this PC (127.0.0.1, or a socket that only listens on it).</summary>
    public bool IsLoopbackOnly => Scope == AddressScope.Loopback;

    /// <summary>A connection its program already closed, which Windows keeps for a while (process id 0, e.g. TIME_WAIT).</summary>
    public bool IsClosed => Connection.ProcessId == 0;
}

public static class ConnectionInventory
{
    /// <summary>Reads the connection tables and names their programs; for the command-line export.</summary>
    public static IReadOnlyList<ConnectionInfo> ReadCurrent(SuspiciousAddressList? suspicious) =>
        Describe(ConnectionTable.Read(), ProcessSnapshot.ReadAll(), new ProcessNames(), suspicious);

    public static IReadOnlyList<ConnectionInfo> Describe(
        IReadOnlyList<NetworkConnection> connections, IReadOnlyList<ProcessSample> processes, ProcessNames names, SuspiciousAddressList? suspicious)
    {
        var byId = processes.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        return connections.Select(c =>
        {
            var (name, path) = c.ProcessId == 0 ? ("—", null)
                : byId.TryGetValue(c.ProcessId, out var process) ? names.Get(process)
                : ($"處理程序 {c.ProcessId}", null);
            // A listening socket has no peer: what matters is who can reach it, i.e. the address it listens on.
            var scope = AddressClassifier.Classify(c.Remote?.Address ?? c.Local.Address);
            var flagged = suspicious is not null && c.Remote is { } remote && suspicious.Contains(remote.Address);
            return new ConnectionInfo(c, name, path, PortCatalog.For(c), scope, flagged);
        }).ToList();
    }
}

/// <summary>The words the network tab and the CSV export use, kept in one place so both say the same.</summary>
public static class ConnectionText
{
    public static string Protocol(TransportProtocol protocol) => protocol == TransportProtocol.Tcp ? "TCP" : "UDP";

    public static string State(NetworkConnection connection) => connection.State switch
    {
        null or TcpState.Listen => "接聽中",
        TcpState.Established => "已連線",
        TcpState.SynSent or TcpState.SynReceived => "連線中",
        TcpState.TimeWait => "等待逾時",
        TcpState.CloseWait => "對方已關閉",
        TcpState.FinWait1 or TcpState.FinWait2 or TcpState.Closing or TcpState.LastAck => "關閉中",
        _ => "已關閉",
    };

    public static string Scope(ConnectionInfo info) => info.Connection.IsListening
        ? info.Scope == AddressScope.Loopback ? "只限本機" : "對外開放"
        : info.Scope switch
        {
            AddressScope.Internet => "網際網路",
            AddressScope.LocalNetwork => "區域網路",
            AddressScope.Loopback => "本機",
            AddressScope.Multicast => "多點傳送",
            _ => "—",
        };

    /// <summary>1.2.3.4:443, or [2001:db8::1]:443 for IPv6; the scope id (%12) is left out.</summary>
    public static string Endpoint(IPEndPoint endpoint)
    {
        var address = AddressClassifier.Normalize(endpoint.Address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return $"{address}:{endpoint.Port}";
        var plain = new IPAddress(address.GetAddressBytes());
        return $"[{plain}]:{endpoint.Port}";
    }

    /// <summary>The peer, or "TCP 埠 3389" for a socket that waits for others to connect (IPv4 and IPv6 often both listen).</summary>
    public static string Address(NetworkConnection connection) =>
        connection.Remote is { } remote ? Endpoint(remote)
        : $"{Protocol(connection.Protocol)} 埠 {connection.Local.Port}" +
          (AddressClassifier.Normalize(connection.Local.Address).AddressFamily == AddressFamily.InterNetworkV6 ? "（IPv6）" : "");

    public static string Service(PortService? service) => service?.Text ?? "—";
}
