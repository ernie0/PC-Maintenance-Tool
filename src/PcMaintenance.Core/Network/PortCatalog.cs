namespace PcMaintenance.Core.Network;

/// <summary>A well-known service: its protocol name and what it is for, in plain words.</summary>
public sealed record PortService(int Port, string Name, string Description)
{
    public string Text => $"{Description}（{Name}）";
}

/// <summary>
/// Common services by port, picked by hand for office PCs from the IANA Service Name and Transport Protocol Port
/// Number Registry (plus 7680, which Windows uses for Delivery Optimization). Ports nobody registered are left out
/// rather than guessed.
/// </summary>
public static class PortCatalog
{
    private const byte Tcp = 1;
    private const byte Udp = 2;
    private const byte Both = Tcp | Udp;

    private static readonly (int Port, byte Protocols, string Name, string Description)[] Entries =
    [
        (20, Tcp, "FTP", "檔案傳輸"),
        (21, Tcp, "FTP", "檔案傳輸"),
        (22, Tcp, "SSH", "安全遠端登入"),
        (23, Tcp, "Telnet", "遠端登入（未加密）"),
        (25, Tcp, "SMTP", "郵件傳送"),
        (53, Both, "DNS", "網域名稱查詢"),
        (67, Udp, "DHCP", "自動取得 IP"),
        (68, Udp, "DHCP", "自動取得 IP"),
        (69, Udp, "TFTP", "簡易檔案傳輸"),
        (80, Tcp, "HTTP", "網頁"),
        (88, Both, "Kerberos", "網域登入驗證"),
        (110, Tcp, "POP3", "收信"),
        (123, Udp, "NTP", "網路校時"),
        (135, Tcp, "RPC", "Windows 遠端程序呼叫"),
        (137, Udp, "NetBIOS", "電腦名稱查詢"),
        (138, Udp, "NetBIOS", "網路芳鄰廣播"),
        (139, Tcp, "NetBIOS", "網路芳鄰檔案分享"),
        (143, Tcp, "IMAP", "收信"),
        (161, Udp, "SNMP", "網路設備管理"),
        (162, Udp, "SNMP", "網路設備通知"),
        (389, Both, "LDAP", "網域目錄服務"),
        (443, Tcp, "HTTPS", "加密網頁"),
        (443, Udp, "QUIC", "加密網頁（HTTP/3）"),
        (445, Tcp, "SMB", "檔案與印表機分享"),
        (465, Tcp, "SMTPS", "加密郵件傳送"),
        (500, Udp, "IKE", "VPN 金鑰交換"),
        (502, Tcp, "Modbus", "工業設備通訊"),
        (514, Udp, "Syslog", "系統記錄"),
        (515, Tcp, "LPD", "網路列印"),
        (554, Both, "RTSP", "串流影音（攝影機）"),
        (587, Tcp, "Submission", "郵件送出"),
        (631, Tcp, "IPP", "網路列印"),
        (636, Tcp, "LDAPS", "加密網域目錄服務"),
        (853, Tcp, "DNS over TLS", "加密網域名稱查詢"),
        (993, Tcp, "IMAPS", "加密收信"),
        (995, Tcp, "POP3S", "加密收信"),
        (1194, Both, "OpenVPN", "VPN"),
        (1433, Tcp, "SQL Server", "資料庫"),
        (1434, Udp, "SQL Server", "資料庫瀏覽"),
        (1701, Udp, "L2TP", "VPN"),
        (1723, Tcp, "PPTP", "VPN"),
        (1883, Tcp, "MQTT", "物聯網訊息"),
        (1900, Udp, "SSDP", "裝置探索（UPnP）"),
        (2869, Tcp, "UPnP", "裝置服務"),
        (3268, Tcp, "LDAP GC", "網域通用類別目錄"),
        (3306, Tcp, "MySQL", "資料庫"),
        (3389, Both, "RDP", "遠端桌面"),
        (3478, Udp, "STUN", "通訊軟體連線"),
        (3702, Udp, "WS-Discovery", "裝置探索"),
        (4500, Udp, "IPsec NAT-T", "VPN"),
        (5060, Both, "SIP", "網路電話"),
        (5353, Udp, "mDNS", "區域網路名稱探索"),
        (5355, Udp, "LLMNR", "區域網路名稱查詢"),
        (5357, Tcp, "WSD", "網路裝置服務"),
        (5432, Tcp, "PostgreSQL", "資料庫"),
        (5900, Tcp, "VNC", "遠端畫面"),
        (5985, Tcp, "WinRM", "Windows 遠端管理"),
        (5986, Tcp, "WinRM", "加密 Windows 遠端管理"),
        (7680, Tcp, "傳遞最佳化", "Windows 更新分享"),
        (8080, Tcp, "HTTP", "網頁（替代埠）"),
        (8443, Tcp, "HTTPS", "加密網頁（替代埠）"),
        (8883, Tcp, "MQTT", "加密物聯網訊息"),
        (9100, Tcp, "RAW", "印表機直接列印"),
        (47808, Udp, "BACnet", "樓宇自動控制"),
    ];

    private static readonly Dictionary<(int, TransportProtocol), PortService> ByPort = Build();

    public static PortService? Lookup(int port, TransportProtocol protocol) => ByPort.GetValueOrDefault((port, protocol));

    /// <summary>
    /// The service a connection talks to: the remote port when it is a known one (this PC is the client), otherwise
    /// the local port (this PC is the server, or the socket is listening).
    /// </summary>
    public static PortService? For(NetworkConnection connection) =>
        (connection.Remote is { } remote ? Lookup(remote.Port, connection.Protocol) : null) ?? Lookup(connection.Local.Port, connection.Protocol);

    private static Dictionary<(int, TransportProtocol), PortService> Build()
    {
        var map = new Dictionary<(int, TransportProtocol), PortService>();
        foreach (var (port, protocols, name, description) in Entries)
        {
            if ((protocols & Tcp) != 0) map[(port, TransportProtocol.Tcp)] = new PortService(port, name, description);
            if ((protocols & Udp) != 0) map[(port, TransportProtocol.Udp)] = new PortService(port, name, description);
        }
        return map;
    }
}
