using System.Buffers.Binary;
using System.Net;
using PcMaintenance.Core.Network;

namespace PcMaintenance.Core.Tests;

public class NetworkDiagnosisTests
{
    private static readonly ActiveAdapter Wired = new("{00000000-0000-0000-0000-000000000001}", "乙太網路", "Realtek PCIe GbE", false,
        1_000_000_000, IPAddress.Parse("192.168.1.20"), true, IPAddress.Parse("192.168.1.1"), [IPAddress.Parse("192.168.1.1")]);

    private static NetworkDiagnosis Diagnosis(
        ActiveAdapter? adapter = null, long? ping = 2, bool resolves = true, TestPageReply? page = null, TimeSpan? clock = null,
        ProxySetting? proxy = null, HostsCheck? hosts = null, bool noAdapter = false) => new()
        {
            ReadAdapter = () => noAdapter ? null : adapter ?? Wired,
            ReadWifiSignal = _ => 30,
            PingGateway = (_, _) => Task.FromResult(ping),
            ResolveName = (_, _) => Task.FromResult(resolves),
            FetchTestPage = _ => Task.FromResult(page),
            ReadClockOffset = _ => Task.FromResult(clock),
            ReadProxy = () => proxy ?? new ProxySetting(ProxyKind.None, ""),
            ReadHosts = () => hosts ?? new HostsCheck(true, [], []),
        };

    private static readonly TestPageReply Online = new(200, NetworkDiagnosis.TestContent);

    private static DiagnosisStep Step(IReadOnlyList<DiagnosisStep> steps, string title) => Assert.Single(steps, s => s.Title == title);

    [Fact]
    public async Task A_working_network_passes_every_check()
    {
        var steps = await Diagnosis(page: Online, clock: TimeSpan.FromSeconds(0.4)).RunAsync();
        Assert.Equal(["網路卡", "IP 位址", "預設閘道", "DNS", "連上網際網路", "電腦時間", "Proxy", "hosts 檔"], steps.Select(s => s.Title));
        Assert.All(steps.Where(s => s.Title != "Proxy"), s => Assert.Equal(DiagnosisResult.Pass, s.Result));
        Assert.Equal("和標準時間一致", Step(steps, "電腦時間").Detail);
    }

    [Fact]
    public async Task No_dhcp_address_stops_at_the_ip_and_says_what_to_do()
    {
        var steps = await Diagnosis(adapter: Wired with { IPv4 = IPAddress.Parse("169.254.12.7"), Gateway = null }).RunAsync();
        var ip = Step(steps, "IP 位址");
        Assert.Equal(DiagnosisResult.Fail, ip.Result);
        Assert.Contains("DHCP", ip.Detail);
        Assert.NotNull(ip.NextStep);
        Assert.All(new[] { "預設閘道", "DNS", "連上網際網路", "電腦時間" }, t => Assert.Equal(DiagnosisResult.Skipped, Step(steps, t).Result));
    }

    [Fact]
    public async Task A_gateway_that_ignores_ping_is_fine_when_the_internet_works()
    {
        var steps = await Diagnosis(ping: null, page: Online, clock: TimeSpan.Zero).RunAsync();
        Assert.Equal(DiagnosisResult.Info, Step(steps, "預設閘道").Result);

        var offline = await Diagnosis(ping: null, page: null, clock: TimeSpan.Zero).RunAsync();
        Assert.Equal(DiagnosisResult.Fail, Step(offline, "預設閘道").Result);
        Assert.Equal(DiagnosisResult.Fail, Step(offline, "連上網際網路").Result);
    }

    [Fact]
    public async Task Dns_failure_skips_the_internet_and_clock_checks()
    {
        var steps = await Diagnosis(resolves: false).RunAsync();
        Assert.Equal(DiagnosisResult.Fail, Step(steps, "DNS").Result);
        Assert.Contains("清除 DNS 快取", Step(steps, "DNS").NextStep);
        Assert.Equal(DiagnosisResult.Skipped, Step(steps, "連上網際網路").Result);
        Assert.Equal(DiagnosisResult.Skipped, Step(steps, "電腦時間").Result);
    }

    [Fact]
    public async Task A_sign_in_page_is_told_apart_from_no_internet()
    {
        var steps = await Diagnosis(page: new TestPageReply(302, ""), clock: TimeSpan.Zero).RunAsync();
        var internet = Step(steps, "連上網際網路");
        Assert.Equal(DiagnosisResult.Warning, internet.Result);
        Assert.Contains("登入", internet.Detail);
    }

    [Fact]
    public async Task Without_internet_behind_a_proxy_the_proxy_is_suspected()
    {
        var steps = await Diagnosis(page: null, proxy: new ProxySetting(ProxyKind.Server, "Proxy 10.0.0.8:8080")).RunAsync();
        Assert.Contains("Proxy", Step(steps, "連上網際網路").NextStep);
        Assert.Equal("經由 Proxy 10.0.0.8:8080", Step(steps, "Proxy").Detail);
    }

    [Theory]
    [InlineData(20, DiagnosisResult.Pass)]
    [InlineData(-150, DiagnosisResult.Warning)]
    [InlineData(600, DiagnosisResult.Fail)]
    public async Task Clock_drift_is_graded_by_what_it_breaks(int seconds, DiagnosisResult expected)
    {
        var steps = await Diagnosis(page: Online, clock: TimeSpan.FromSeconds(seconds)).RunAsync();
        var clock = Step(steps, "電腦時間");
        Assert.Equal(expected, clock.Result);
        Assert.Contains(seconds > 0 ? "電腦慢" : "電腦快", clock.Detail);
        if (expected != DiagnosisResult.Pass) Assert.Contains("校正電腦時間", clock.NextStep);
    }

    [Fact]
    public async Task No_connected_card_fails_first_and_skips_the_rest()
    {
        var steps = await Diagnosis(noAdapter: true).RunAsync();
        Assert.Equal(DiagnosisResult.Fail, steps[0].Result);
        Assert.Equal(5, steps.Count(s => s.Result == DiagnosisResult.Skipped));
    }

    [Fact]
    public async Task Weak_wifi_is_reported_with_advice()
    {
        var steps = await Diagnosis(adapter: Wired with { IsWireless = true }, page: Online, clock: TimeSpan.Zero).RunAsync();
        var wifi = Step(steps, "Wi-Fi 訊號");
        Assert.Equal(DiagnosisResult.Warning, wifi.Result);
        Assert.Contains("30%", wifi.Detail);
    }

    [Fact]
    public void Hosts_entries_for_update_and_antivirus_sites_are_flagged()
    {
        var check = HostsFile.Parse(
        [
            "# Copyright (c) 1993-2009 Microsoft Corp.",
            "#\t127.0.0.1       localhost",
            "127.0.0.1 localhost",
            "10.0.0.5   erp.example.local   # company ERP",
            "0.0.0.0 update.microsoft.com download.windowsupdate.com",
            "not-an-address something",
        ]);
        Assert.True(check.Readable);
        Assert.Equal(["erp.example.local", "update.microsoft.com", "download.windowsupdate.com"], check.Entries.Select(e => e.Name));
        Assert.Equal(["update.microsoft.com", "download.windowsupdate.com"], check.Blocked);
        Assert.Empty(HostsFile.Parse(["# only comments", "", "127.0.0.1 localhost", "::1 localhost"]).Entries);
    }

    [Fact]
    public void Ntp_replies_give_the_clock_offset()
    {
        var sent = new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc);
        // The server is 90 seconds ahead; the request takes 20 ms each way and 1 ms inside the server.
        var reply = NtpReply(sent.AddSeconds(90).AddMilliseconds(20), sent.AddSeconds(90).AddMilliseconds(21));
        var offset = TimeCheck.Offset(reply, sent, sent.AddMilliseconds(41));
        Assert.NotNull(offset);
        Assert.InRange(offset!.Value.TotalMilliseconds, 89_999, 90_001);

        reply[0] = 0x23; // a client packet, not a server reply
        Assert.Null(TimeCheck.Offset(reply, sent, sent));
        Assert.Null(TimeCheck.Offset(new byte[20], sent, sent));
    }

    private static byte[] NtpReply(DateTime received, DateTime transmitted)
    {
        var data = new byte[48];
        data[0] = 0x24; // version 4, server
        WriteTimestamp(data, 32, received);
        WriteTimestamp(data, 40, transmitted);
        return data;
    }

    private static void WriteTimestamp(byte[] data, int offset, DateTime utc)
    {
        var ticks = (utc - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks;
        var seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        var fraction = (ulong)(ticks % TimeSpan.TicksPerSecond) * 0x1_0000_0000UL / TimeSpan.TicksPerSecond;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4), (uint)fraction);
    }
}
