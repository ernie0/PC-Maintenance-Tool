using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PcMaintenance.Core.Performance;

namespace PcMaintenance.Core.Network;

public enum DiagnosisResult
{
    Pass,
    Info,
    Warning,
    Fail,
    Skipped,
}

/// <summary>One line of the network check: what was looked at, how it went, and what to do about it.</summary>
public sealed record DiagnosisStep(string Title, DiagnosisResult Result, string Detail, string? NextStep = null);

/// <summary>The network card the PC uses to reach other networks, as far as the check needs it.</summary>
public sealed record ActiveAdapter(
    string Id, string Name, string Description, bool IsWireless, long SpeedBitsPerSecond,
    IPAddress? IPv4, bool IsDhcp, IPAddress? Gateway, IReadOnlyList<IPAddress> DnsServers);

/// <summary>What answered the internet test: the page's text, or the redirect a sign-in page answers with.</summary>
public sealed record TestPageReply(int Status, string Content);

/// <summary>
/// "Why is there no internet?": the network card, the address it got, the gateway, DNS, the internet and the clock,
/// in that order, each with a result and a next step. It contacts the gateway, the DNS servers, Microsoft's connection
/// test page (the one Windows itself uses to show "no internet") and the Windows time server, and only when asked.
/// </summary>
public sealed class NetworkDiagnosis
{
    public const string TestHost = "www.msftconnecttest.com";
    public const string TestContent = "Microsoft Connect Test";
    public const string TimeServer = "time.windows.com";
    private static readonly Uri TestPage = new($"http://{TestHost}/connecttest.txt");
    private static readonly TimeSpan SmallClockDrift = TimeSpan.FromMinutes(1);
    // Kerberos, and so signing in to a company domain, refuses clocks more than five minutes apart.
    private static readonly TimeSpan LargeClockDrift = TimeSpan.FromMinutes(5);

    public Func<ActiveAdapter?> ReadAdapter { get; init; } = ReadActiveAdapter;
    public Func<string, int?> ReadWifiSignal { get; init; } = WifiSignal.Read;
    public Func<IPAddress, CancellationToken, Task<long?>> PingGateway { get; init; } = PingAsync;
    public Func<string, CancellationToken, Task<bool>> ResolveName { get; init; } = ResolveAsync;
    public Func<CancellationToken, Task<TestPageReply?>> FetchTestPage { get; init; } = FetchAsync;
    public Func<CancellationToken, Task<TimeSpan?>> ReadClockOffset { get; init; } = ct => TimeCheck.QueryOffsetAsync(TimeServer, ct);
    public Func<ProxySetting> ReadProxy { get; init; } = ProxySetting.Read;
    public Func<HostsCheck> ReadHosts { get; init; } = HostsFile.Read;
    public Func<IReadOnlyList<NetworkShare>> ReadShares { get; init; } = NetworkShares.Read;
    public Func<NetworkShare, CancellationToken, Task<ShareReach>> ProbeShare { get; init; } = NetworkShares.ProbeAsync;

    /// <summary>Runs every check; each finished line is reported as soon as it is known.</summary>
    public async Task<IReadOnlyList<DiagnosisStep>> RunAsync(IProgress<DiagnosisStep>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<DiagnosisStep>();
        void Add(DiagnosisStep step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

        var proxy = ReadProxy();
        var adapter = ReadAdapter();
        if (adapter is null)
        {
            Add(new("網路卡", DiagnosisResult.Fail, "沒有已連線的網路卡",
                "確認網路線有插好、Wi-Fi 已連線；網路卡被停用時，到「系統工具」的「網路連線」啟用"));
            SkipRest(Add, "網路卡沒有連線");
            AddSettings(Add, proxy);
            return steps;
        }
        var speed = adapter.SpeedBitsPerSecond >= 1_000_000_000 ? $"{adapter.SpeedBitsPerSecond / 1e9:0.#} Gbps"
            : adapter.SpeedBitsPerSecond > 0 ? $"{adapter.SpeedBitsPerSecond / 1e6:0} Mbps" : "";
        Add(new("網路卡", DiagnosisResult.Pass, $"{adapter.Name}（{adapter.Description}）已連線" + (speed.Length > 0 ? $"，速度 {speed}" : "")));
        if (adapter.IsWireless && ReadWifiSignal(adapter.Id) is { } signal)
        {
            Add(signal >= 40
                ? new("Wi-Fi 訊號", DiagnosisResult.Pass, $"訊號強度 {signal}%")
                : new("Wi-Fi 訊號", DiagnosisResult.Warning, $"訊號強度只有 {signal}%，連線可能不穩、速度變慢", "靠近無線基地台，或改用有線網路"));
        }

        if (adapter.IPv4 is null || IsAutomaticPrivate(adapter.IPv4))
        {
            Add(new("IP 位址", DiagnosisResult.Fail,
                adapter.IPv4 is null ? "沒有 IPv4 位址" : $"沒有從 DHCP 伺服器取得位址（目前是自動產生的 {adapter.IPv4}）",
                "重新插拔網路線或重新連線 Wi-Fi；仍然一樣時重新啟動路由器，或請資訊人員檢查 DHCP"));
            SkipRest(Add, "沒有取得 IP 位址", from: 1);
            AddSettings(Add, proxy);
            return steps;
        }
        Add(new("IP 位址", DiagnosisResult.Pass, $"{adapter.IPv4}（{(adapter.IsDhcp ? "自動取得" : "手動設定")}）"));

        if (adapter.Gateway is not { } gateway)
        {
            Add(new("預設閘道", DiagnosisResult.Fail, "沒有預設閘道，只能連到同一個網段的電腦",
                adapter.IsDhcp ? "請資訊人員檢查 DHCP 是否有發送預設閘道" : "手動設定的 IP 需要補上預設閘道（通常是路由器的位址）"));
            SkipRest(Add, "沒有預設閘道", from: 2);
            AddSettings(Add, proxy);
            return steps;
        }

        // The gateway's verdict depends on whether the internet works: many routers ignore ping.
        var latency = await PingGateway(gateway, ct);
        var resolves = await ResolveName(TestHost, ct);
        TestPageReply? reply = resolves ? await FetchTestPage(ct) : null;
        var online = reply is { Status: 200 } && reply.Content.Trim() == TestContent;

        Add(latency is { } ms
            ? new("預設閘道", DiagnosisResult.Pass, $"{gateway} 有回應（{(ms == 0 ? "不到 1" : ms)} 毫秒）")
            : online
                ? new("預設閘道", DiagnosisResult.Info, $"{gateway} 不回應 ping（有些路由器會關閉回應），但可以上網")
                : new("預設閘道", DiagnosisResult.Fail, $"{gateway} 沒有回應", "檢查網路線、交換器與路由器的電源和燈號；其他電腦也不能上網時請交給資訊人員"));

        var servers = adapter.DnsServers.Count > 0 ? string.Join("、", adapter.DnsServers) : "（沒有設定）";
        Add(resolves
            ? new("DNS", DiagnosisResult.Pass, $"DNS 伺服器 {servers} 正常")
            : new("DNS", DiagnosisResult.Fail, $"DNS 伺服器 {servers} 沒有回應，網址都查不到",
                "先試「系統工具」的「清除 DNS 快取」；仍然失敗時請資訊人員檢查 DNS 伺服器"));

        var via = proxy.Kind == ProxyKind.None ? "" : $"（經由 {proxy.Text}）";
        Add(!resolves
            ? new("連上網際網路", DiagnosisResult.Skipped, "DNS 查不到網址，無法測試")
            : online
                ? new("連上網際網路", DiagnosisResult.Pass, $"可以連上網際網路{via}")
                : reply is not null
                    ? new("連上網際網路", DiagnosisResult.Warning, "網路要求先在網頁上登入才能上網（例如訪客 Wi-Fi、飯店網路）",
                        "開啟瀏覽器，依畫面完成登入")
                    : new("連上網際網路", DiagnosisResult.Fail, $"無法連上網際網路{via}",
                        proxy.Kind == ProxyKind.None
                            ? "其他電腦也不能上網時請檢查路由器或對外線路；只有這台不行時，可試「系統工具」的「重設網路連線（Winsock）」"
                            : "請確認 Proxy 正常，或請資訊人員檢查 Proxy 設定"));

        if (!resolves)
        {
            Add(new("電腦時間", DiagnosisResult.Skipped, "DNS 查不到網址，無法和時間伺服器比對"));
        }
        else if (await ReadClockOffset(ct) is not { } offset)
        {
            Add(new("電腦時間", DiagnosisResult.Skipped, $"連不到時間伺服器 {TimeServer}（公司網路可能擋住），無法比對"));
        }
        else
        {
            var drift = offset.Duration();
            var text = $"和標準時間相差 {DriftText(drift)}（電腦{(offset > TimeSpan.Zero ? "慢" : "快")}）";
            Add(drift < SmallClockDrift ? new("電腦時間", DiagnosisResult.Pass, drift < TimeSpan.FromSeconds(1) ? "和標準時間一致" : text)
                : drift < LargeClockDrift ? new("電腦時間", DiagnosisResult.Warning, text, "用「系統工具」的「校正電腦時間」對時")
                : new("電腦時間", DiagnosisResult.Fail, $"{text}，網域登入與加密網站都可能失敗", "用「系統工具」的「校正電腦時間」對時"));
        }

        await AddSharesAsync(Add, ct);
        AddSettings(Add, proxy);
        return steps;
    }

    /// <summary>
    /// Mapped drives and network printers, each tried at once: a line for each kind the PC has, none when it has none.
    /// </summary>
    private async Task AddSharesAsync(Action<DiagnosisStep> add, CancellationToken ct)
    {
        var shares = ReadShares();
        if (shares.Count == 0) return;
        var reach = await Task.WhenAll(shares.Select(s => ProbeShare(s, ct)));
        var results = shares.Zip(reach).ToList();
        foreach (var printers in new[] { false, true })
        {
            var group = results.Where(r => r.First.IsPrinter == printers).ToList();
            if (group.Count == 0) continue;
            var title = printers ? "網路印表機" : "網路磁碟機";
            string Label(NetworkShare s) => printers ? $"「{s.Name}」" : $"{s.Name}（{s.Target}）";
            var bad = group.Where(r => r.Second != ShareReach.Reachable).ToList();
            var offline = printers ? group.Where(r => r.Second == ShareReach.Reachable && r.First.Offline).ToList() : [];
            if (bad.Count > 0)
            {
                add(new(title, DiagnosisResult.Fail, string.Join("；", bad.Select(r => r.Second == ShareReach.HostUnreachable
                        ? $"{Label(r.First)}連不到 {r.First.Host}"
                        : $"{Label(r.First)}的伺服器有回應，但打不開資料夾")) + (bad.Count < group.Count ? $"；其餘 {group.Count - bad.Count} 個正常" : ""),
                    printers
                        ? "確認印表機有開機、網路燈號正常、IP 位址沒有變更；仍然不行時交給資訊人員"
                        : bad.Any(r => r.Second == ShareReach.HostUnreachable)
                            ? "確認 NAS 或檔案伺服器有開機、這台電腦連得到公司網路（在公司外面要先連 VPN）"
                            : "通常是帳號密碼或權限的問題：在檔案總管重新連線並輸入帳號密碼，或交給資訊人員"));
            }
            else if (offline.Count > 0)
            {
                add(new(title, DiagnosisResult.Warning, $"{string.Join("、", offline.Select(r => Label(r.First)))}連得到，但 Windows 設定成「離線使用印表機」",
                    "到「設定 → 藍牙與裝置 → 印表機與掃描器」開啟這台印表機的列印佇列，取消「離線使用印表機」"));
            }
            else
            {
                add(new(title, DiagnosisResult.Pass, $"{group.Count} 個都連得到：{string.Join("、", group.Take(3).Select(r => Label(r.First)))}{(group.Count > 3 ? "…" : "")}"));
            }
        }
    }

    private static readonly string[] Checks = ["IP 位址", "預設閘道", "DNS", "連上網際網路", "電腦時間"];

    private static void SkipRest(Action<DiagnosisStep> add, string reason, int from = 0)
    {
        foreach (var title in Checks.Skip(from)) add(new(title, DiagnosisResult.Skipped, $"{reason}，無法測試"));
    }

    /// <summary>Settings that explain odd results rather than pass or fail: the proxy and the hosts file.</summary>
    private void AddSettings(Action<DiagnosisStep> add, ProxySetting proxy)
    {
        add(new("Proxy", DiagnosisResult.Info, proxy.Kind == ProxyKind.None ? "沒有使用 Proxy" : $"經由 {proxy.Text}"));
        var hosts = ReadHosts();
        add(hosts switch
        {
            { Readable: false } => new("hosts 檔", DiagnosisResult.Info, "無法讀取 hosts 檔"),
            { Entries.Count: 0 } => new("hosts 檔", DiagnosisResult.Pass, "沒有自訂項目"),
            { Blocked.Count: > 0 } => new("hosts 檔", DiagnosisResult.Warning,
                $"把 {string.Join("、", hosts.Blocked.Take(3))} 導到固定位址，可能是惡意程式在阻擋更新或防毒軟體",
                $"請資訊人員檢查 {HostsFile.Path}"),
            _ => new("hosts 檔", DiagnosisResult.Info,
                $"有 {hosts.Entries.Count} 筆自訂項目：{string.Join("、", hosts.Entries.Take(3).Select(e => e.Name))}{(hosts.Entries.Count > 3 ? "…" : "")}",
                "這些網址會被導到固定位址；不認識的項目請資訊人員確認"),
        });
    }

    internal static string DriftText(TimeSpan drift) =>
        drift >= TimeSpan.FromHours(1) ? $"{(int)drift.TotalHours} 小時 {drift.Minutes} 分"
        : drift >= TimeSpan.FromMinutes(1) ? $"{(int)drift.TotalMinutes} 分 {drift.Seconds} 秒"
        : $"{(int)Math.Round(drift.TotalSeconds)} 秒";

    /// <summary>169.254.x.x: what Windows gives itself when no DHCP server answered.</summary>
    internal static bool IsAutomaticPrivate(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..];

    /// <summary>
    /// The card the PC reaches other networks through: the one with an IPv4 default gateway, a physical card before a
    /// virtual one. Inside a virtual machine (Windows Sandbox, a company's virtual desktop) the only card is virtual, so
    /// virtual cards are not ruled out, only ranked after physical ones.
    /// </summary>
    public static ActiveAdapter? ReadActiveAdapter()
    {
        ActiveAdapter? best = null;
        var bestRank = int.MaxValue;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            var ipv4 = properties.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            var gateway = properties.GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            bool dhcp;
            try
            {
                dhcp = properties.GetIPv4Properties()?.IsDhcpEnabled ?? false;
            }
            catch (NetworkInformationException)
            {
                dhcp = false;
            }
            var adapter = new ActiveAdapter(nic.Id, nic.Name, nic.Description, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                nic.Speed, ipv4, dhcp, gateway, properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList());
            var rank = Preference(gateway is not null, NetworkActivitySampler.IsShown(nic.NetworkInterfaceType, nic.Description, nic.OperationalStatus));
            if (rank < bestRank)
            {
                best = adapter;
                bestRank = rank;
            }
        }
        return best;
    }

    /// <summary>Lower is better: a gateway matters more than being physical (a VPN without one is not the way out).</summary>
    internal static int Preference(bool hasGateway, bool physical) => (hasGateway ? 0 : 2) + (physical ? 0 : 1);

    private static async Task<long?> PingAsync(IPAddress address, CancellationToken ct)
    {
        using var ping = new Ping();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1), null, null, ct);
                if (reply.Status == IPStatus.Success) return reply.RoundtripTime;
            }
            catch (PingException)
            {
            }
        }
        return null;
    }

    private static async Task<bool> ResolveAsync(string name, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return (await Dns.GetHostAddressesAsync(name, timeout.Token)).Length > 0;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Through the system proxy, without following redirects: a sign-in page answers the test with one.</summary>
    private static async Task<TestPageReply?> FetchAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            using var response = await client.GetAsync(TestPage, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            return new TestPageReply((int)response.StatusCode, content.Length > 200 ? content[..200] : content);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
