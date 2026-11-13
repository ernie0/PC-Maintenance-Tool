using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Network;
using PcMaintenance.Core.Processes;

namespace PcMaintenance.Core.Tests;

public class NetworkTests
{
    [Fact]
    public void Connection_table_shows_this_process_listening_connected_and_bound()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, listenPort);
        using var accepted = listener.AcceptTcpClient();
        var clientEnd = (IPEndPoint)client.Client.LocalEndPoint!;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var mine = ConnectionTable.Read().Where(c => c.ProcessId == Environment.ProcessId).ToList();

        var listening = Assert.Single(mine, c => c.Protocol == TransportProtocol.Tcp && c.State == TcpState.Listen && c.Local.Port == listenPort);
        Assert.Null(listening.Remote);
        Assert.True(listening.IsListening);
        var outgoing = Assert.Single(mine, c => c.State == TcpState.Established && c.Local.Port == clientEnd.Port);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, listenPort), outgoing.Remote);
        Assert.Contains(mine, c => c.State == TcpState.Established && c.Local.Port == listenPort && c.Remote!.Port == clientEnd.Port);
        var bound = Assert.Single(mine, c => c.Protocol == TransportProtocol.Udp && c.Local.Port == udpPort);
        Assert.Null(bound.State);
        Assert.True(bound.IsListening);
    }

    [Fact]
    public void Connection_table_reads_ipv6_sockets()
    {
        if (!Socket.OSSupportsIPv6) return;
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var found = Assert.Single(ConnectionTable.Read(), c => c.ProcessId == Environment.ProcessId && c.Local.Port == port);
        Assert.Equal(IPAddress.IPv6Loopback, found.Local.Address);
        Assert.Equal("[::1]:" + port, ConnectionText.Endpoint(found.Local));
    }

    [Theory]
    [InlineData("0.0.0.0", AddressScope.Unspecified)]
    [InlineData("::", AddressScope.Unspecified)]
    [InlineData("127.0.0.1", AddressScope.Loopback)]
    [InlineData("::1", AddressScope.Loopback)]
    [InlineData("10.1.2.3", AddressScope.LocalNetwork)]
    [InlineData("172.16.0.1", AddressScope.LocalNetwork)]
    [InlineData("172.31.255.1", AddressScope.LocalNetwork)]
    [InlineData("172.32.0.1", AddressScope.Internet)]
    [InlineData("192.168.1.20", AddressScope.LocalNetwork)]
    [InlineData("169.254.10.10", AddressScope.LocalNetwork)]
    [InlineData("fe80::1", AddressScope.LocalNetwork)]
    [InlineData("fd12:3456::1", AddressScope.LocalNetwork)]
    [InlineData("::ffff:192.168.1.20", AddressScope.LocalNetwork)]
    [InlineData("224.0.0.251", AddressScope.Multicast)]
    [InlineData("255.255.255.255", AddressScope.Multicast)]
    [InlineData("ff02::fb", AddressScope.Multicast)]
    [InlineData("8.8.8.8", AddressScope.Internet)]
    [InlineData("2001:4860:4860::8888", AddressScope.Internet)]
    public void Addresses_are_classified_by_where_they_lead(string address, AddressScope scope) =>
        Assert.Equal(scope, AddressClassifier.Classify(IPAddress.Parse(address)));

    [Fact]
    public void Service_comes_from_the_known_port_on_either_side()
    {
        NetworkConnection Tcp(int localPort, int? remotePort, TcpState state) => new(TransportProtocol.Tcp,
            new IPEndPoint(IPAddress.Parse("192.168.1.5"), localPort),
            remotePort is { } port ? new IPEndPoint(IPAddress.Parse("203.0.113.9"), port) : null, state, 100);

        Assert.Equal("HTTPS", PortCatalog.For(Tcp(51000, 443, TcpState.Established))?.Name);
        // This PC is the server: the caller's port is random, the local one says what it is.
        Assert.Equal("遠端桌面", PortCatalog.For(Tcp(3389, 52000, TcpState.Established))?.Description);
        Assert.Equal("SMB", PortCatalog.For(Tcp(445, null, TcpState.Listen))?.Name);
        Assert.Null(PortCatalog.For(Tcp(51000, 52000, TcpState.Established)));
        // UDP 443 is QUIC, not HTTPS.
        Assert.Equal("QUIC", PortCatalog.Lookup(443, TransportProtocol.Udp)?.Name);
        Assert.Equal("加密網頁（HTTPS）", PortCatalog.Lookup(443, TransportProtocol.Tcp)!.Text);
    }

    [Fact]
    public void Suspicious_list_reads_addresses_ranges_and_comments()
    {
        var list = SuspiciousAddressList.Parse(
        [
            "# 公司提供的清單",
            "203.0.113.7",
            "198.51.100.0/24    # 整段",
            "2001:db8::/32,說明文字",
            "",
            "10",
            "不是位址",
        ]);

        Assert.Equal(3, list.Count);
        Assert.Equal(2, list.RejectedLines);
        Assert.True(list.Contains(IPAddress.Parse("203.0.113.7")));
        Assert.True(list.Contains(IPAddress.Parse("::ffff:203.0.113.7")));
        Assert.True(list.Contains(IPAddress.Parse("198.51.100.200")));
        Assert.True(list.Contains(IPAddress.Parse("2001:db8:1::5")));
        Assert.False(list.Contains(IPAddress.Parse("203.0.113.8")));
        Assert.False(list.Contains(IPAddress.Parse("0.0.0.10")));
    }

    [Fact]
    public void Suspicious_list_path_is_optional_and_a_missing_file_is_reported()
    {
        Assert.Equal(new SuspiciousListLoad(null, null, null), SuspiciousAddressList.Load(AppSettings.Default));

        using var temp = new TempDir();
        var file = temp.File("list.txt");
        File.WriteAllText(file, "203.0.113.7\n");
        var loaded = SuspiciousAddressList.Load(AppSettings.Default with { SuspiciousAddressList = file });
        Assert.Equal(1, loaded.List?.Count);
        Assert.Null(loaded.Error);

        var missing = SuspiciousAddressList.Load(AppSettings.Default with { SuspiciousAddressList = Path.Combine(temp.Path, "none.txt") });
        Assert.Null(missing.List);
        Assert.Contains("無法讀取可疑 IP 清單", missing.Error);
    }

    [Fact]
    public void Connections_are_named_by_program_and_flagged_against_the_list()
    {
        var me = ProcessSnapshot.ReadAll().Single(p => p.Id == Environment.ProcessId);
        var local = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 51000);
        var connections = new[]
        {
            new NetworkConnection(TransportProtocol.Tcp, local, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 443), TcpState.Established, me.Id),
            new NetworkConnection(TransportProtocol.Tcp, new IPEndPoint(IPAddress.Any, 3389), null, TcpState.Listen, me.Id),
            new NetworkConnection(TransportProtocol.Tcp, local, new IPEndPoint(IPAddress.Parse("192.168.1.1"), 80), TcpState.TimeWait, 0),
            new NetworkConnection(TransportProtocol.Udp, new IPEndPoint(IPAddress.Loopback, 5353), null, null, int.MaxValue),
        };
        var list = SuspiciousAddressList.Parse(["203.0.113.0/24"]);

        var infos = ConnectionInventory.Describe(connections, [me], new ProcessNames(), list);

        Assert.Equal(new ProcessNames().Get(me).Name, infos[0].ProgramName);
        Assert.True(infos[0].IsSuspicious);
        Assert.Equal(("網際網路", "已連線"), (ConnectionText.Scope(infos[0]), ConnectionText.State(connections[0])));
        Assert.Equal(("對外開放", "接聽中", "TCP 埠 3389"), (ConnectionText.Scope(infos[1]), ConnectionText.State(connections[1]), ConnectionText.Address(connections[1])));
        Assert.Equal("UDP 埠 5353（IPv6）", ConnectionText.Address(connections[3] with { Local = new IPEndPoint(IPAddress.IPv6Any, 5353) }));
        Assert.Equal(("—", "區域網路", "等待逾時"), (infos[2].ProgramName, ConnectionText.Scope(infos[2]), ConnectionText.State(connections[2])));
        Assert.False(infos[2].IsSuspicious);
        Assert.True(infos[2].IsClosed);
        Assert.Equal(($"處理程序 {int.MaxValue}", "只限本機"), (infos[3].ProgramName, ConnectionText.Scope(infos[3])));
        Assert.True(infos[3].IsLoopbackOnly);
    }

    [Fact]
    public void Connection_export_names_the_computer_and_fills_looked_up_host_names()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "connections.csv");
        var remote = IPAddress.Parse("203.0.113.7");
        var info = new ConnectionInfo(
            new NetworkConnection(TransportProtocol.Tcp, new IPEndPoint(IPAddress.Parse("192.168.1.5"), 51000), new IPEndPoint(remote, 443), TcpState.Established, 1234),
            "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", PortCatalog.Lookup(443, TransportProtocol.Tcp), AddressScope.Internet, true);

        Assert.Equal(1, InventoryExport.WriteConnections(file, [info], "PC-01", a => a.Equals(remote) ? "example.net" : null));

        var lines = File.ReadAllLines(file);
        Assert.Equal("電腦名稱,程式,處理程序編號,程式路徑,協定,本機位址,遠端位址,主機名稱,狀態,服務,範圍,可疑 IP", lines[0]);
        Assert.Equal(@"PC-01,Google Chrome,1234,C:\Program Files\Google\Chrome\Application\chrome.exe,TCP,192.168.1.5:51000,203.0.113.7:443,example.net,已連線,加密網頁（HTTPS）,網際網路,是", lines[1]);
    }

    [Fact]
    public void Neighbor_entries_are_only_real_devices()
    {
        var device = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        Assert.True(NeighborTable.IsDevice(IPAddress.Parse("192.168.1.1"), device));
        Assert.False(NeighborTable.IsDevice(IPAddress.Parse("192.168.1.1"), new byte[6]));
        Assert.False(NeighborTable.IsDevice(IPAddress.Parse("192.168.1.255"), [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.False(NeighborTable.IsDevice(IPAddress.Parse("224.0.0.22"), [0x01, 0x00, 0x5E, 0x00, 0x00, 0x16]));
        Assert.Equal("00-11-22-33-44-55", NeighborTable.FormatMac(device));

        // The real table reads without error; every entry kept is a unicast device.
        Assert.All(NeighborTable.Read(), d => Assert.Matches("^([0-9A-F]{2}-){5}[0-9A-F]{2}$", d.Mac));
    }
}
