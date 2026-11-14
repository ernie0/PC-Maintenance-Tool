using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Maintenance;
using PcMaintenance.Core.Network;
using PcMaintenance.Core.Startup;
using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.Core.Tests;

/// <summary>Stability, security, start-up time and the health report built from them.</summary>
public class ReportTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_event_log_reads_as_blue_screens_shutdowns_disk_errors_and_crashing_programs()
    {
        var report = StabilityReader.Summarize(
        [
            new("Microsoft-Windows-WER-SystemErrorReporting", 1001, Now.AddDays(-2), "0x00000133 (0x0000000000000001, 0x0000000000001e00)"),
            new("Microsoft-Windows-Kernel-Power", 41, Now.AddDays(-2), ""),
            new("disk", 7, Now.AddDays(-5), @"\Device\Harddisk0\DR0"),
            new("Microsoft-Windows-Ntfs", 55, Now.AddDays(-6), "C:"),
            new("Microsoft-Windows-WHEA-Logger", 17, Now.AddDays(-1), ""),
            new("Microsoft-Windows-WHEA-Logger", 17, Now.AddDays(-3), ""),
            new("Application Error", 1000, Now.AddDays(-1), "EXCEL.EXE"),
            new("Application Error", 1000, Now.AddDays(-4), "excel.exe"),
            new("Application Hang", 1002, Now.AddDays(-2), "chrome.exe"),
        ]);
        Assert.True(report.Readable);
        Assert.Equal("藍色畫面（停止代碼 0x00000133）", Assert.Single(report.BlueScreens).Text);
        Assert.Single(report.UnexpectedShutdowns);
        Assert.Equal(2, report.DiskErrors.Count);
        Assert.Contains(@"\Device\Harddisk0\DR0", report.DiskErrors.Single(d => d.Text.StartsWith("磁碟有損壞")).Text);
        // Corrected hardware errors are counted, but are no sign of a fault on their own.
        Assert.Empty(report.HardwareErrors);
        Assert.Equal(2, report.CorrectedHardwareErrors);
        Assert.Equal(("EXCEL.EXE", 2), (report.ProgramCrashes[0].Program, report.ProgramCrashes[0].Count));
        Assert.True(report.HasSystemProblems);

        var quiet = StabilityReader.Summarize([new("Microsoft-Windows-WHEA-Logger", 19, Now, ""), new("Application Error", 1000, Now, "app.exe")]);
        Assert.False(quiet.HasSystemProblems);
        Assert.True(StabilityReader.Summarize([new("Microsoft-Windows-WHEA-Logger", 18, Now, "")]).HasSystemProblems);
    }

    private static DiagnosisStep Find(IReadOnlyList<DiagnosisStep> steps, string title) => Assert.Single(steps, s => s.Title == title);

    [Fact]
    public void Default_security_settings_pass_and_risky_ones_say_what_to_change()
    {
        var safe = SecurityChecks.Evaluate(new SecuritySettings
        {
            DomainFirewall = true, PrivateFirewall = true, PublicFirewall = true, SystemDrive = DiskEncryption.Encrypted, Administrators = ["PC\\admin"],
        }, isElevated: true, isLaptop: true);
        Assert.DoesNotContain(safe, s => s.Result is DiagnosisResult.Warning or DiagnosisResult.Fail);

        var risky = SecurityChecks.Evaluate(new SecuritySettings
        {
            DomainFirewall = true, PrivateFirewall = false, PublicFirewall = false, SystemDrive = DiskEncryption.NotEncrypted,
            RemoteDesktopOn = true, RemoteDesktopNla = false, Smb1ClientOn = true, UacOn = false,
            AutoLogon = true, AutoLogonPasswordStored = true, GuestEnabled = true,
        }, isElevated: true, isLaptop: true);
        Assert.Equal(DiagnosisResult.Fail, Find(risky, "防火牆").Result);
        Assert.Contains("私人、公用", Find(risky, "防火牆").Detail);
        Assert.Equal(DiagnosisResult.Warning, Find(risky, "磁碟加密").Result);
        Assert.Equal(DiagnosisResult.Warning, Find(risky, "遠端桌面").Result);
        Assert.Equal(DiagnosisResult.Warning, Find(risky, "舊版檔案分享（SMBv1）").Result);
        Assert.Equal(DiagnosisResult.Fail, Find(risky, "使用者帳戶控制").Result);
        Assert.Equal(DiagnosisResult.Fail, Find(risky, "自動登入").Result);
        Assert.Equal(DiagnosisResult.Warning, Find(risky, "Guest 帳號").Result);
        Assert.All(risky.Where(s => s.Result is DiagnosisResult.Warning or DiagnosisResult.Fail), s => Assert.False(string.IsNullOrEmpty(s.NextStep)));
    }

    [Fact]
    public void Another_firewall_replaces_windows_firewall_and_desktops_are_not_pressed_to_encrypt()
    {
        var steps = SecurityChecks.Evaluate(new SecuritySettings
        {
            OtherFirewalls = ["ESET Firewall"], DomainFirewall = false, PrivateFirewall = false, PublicFirewall = false, SystemDrive = DiskEncryption.NotEncrypted,
        }, isElevated: true, isLaptop: false);
        Assert.Equal(DiagnosisResult.Pass, Find(steps, "防火牆").Result);
        Assert.Equal(DiagnosisResult.Info, Find(steps, "磁碟加密").Result);
        Assert.Contains("系統管理員", Find(SecurityChecks.Evaluate(new SecuritySettings(), isElevated: false, isLaptop: true), "磁碟加密").Detail);
    }

    [Fact]
    public void Reading_security_settings_works_without_elevation()
    {
        var steps = SecurityChecks.Read(isElevated: false, isLaptop: false);
        Assert.Equal(["防火牆", "磁碟加密", "遠端桌面", "舊版檔案分享（SMBv1）", "使用者帳戶控制", "自動登入", "Guest 帳號", "系統管理員帳號"],
            steps.Select(s => s.Title));
        Assert.NotEmpty(SecurityChecks.ReadAdministrators());
        Assert.True(StabilityReader.Read().Readable);
    }

    private const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static string Event(int id, DateTime utc, params (string Name, string Value)[] data) =>
        $"<Event xmlns='{Ns}'><System><EventID>{id}</EventID><TimeCreated SystemTime='{utc:yyyy-MM-ddTHH:mm:ss.fffffffZ}'/></System>" +
        $"<EventData>{string.Concat(data.Select(d => $"<Data Name='{d.Name}'>{d.Value}</Data>"))}</EventData></Event>";

    [Fact]
    public void Boot_events_give_the_start_up_times_and_what_slowed_them()
    {
        var report = BootPerformance.Parse(
        [
            Event(100, Now, ("BootTime", "61000"), ("MainPathBootTime", "38000"), ("BootPostBootTime", "23000")),
            Event(101, Now, ("Name", "OneDrive.exe"), ("FriendlyName", "Microsoft OneDrive"), ("TotalTime", "4200"), ("DegradationTime", "3100")),
            Event(103, Now, ("Name", "Spooler"), ("FriendlyName", ""), ("TotalTime", "900"), ("DegradationTime", "800")),
            Event(100, Now.AddDays(-1), ("BootTime", "50000"), ("MainPathBootTime", "30000")),
            Event(101, Now.AddDays(-1), ("Name", "OneDrive.exe"), ("FriendlyName", "Microsoft OneDrive"), ("DegradationTime", "9000")),
            "<not xml",
        ]);
        Assert.True(report.Readable);
        Assert.Equal(2, report.Boots.Count);
        Assert.Equal(TimeSpan.FromSeconds(38), report.Last!.ToDesktop);
        Assert.Equal(TimeSpan.FromSeconds(61), report.Last.ToReady);
        Assert.Equal(TimeSpan.FromSeconds(34), report.AverageToDesktop);
        // One line per program, its latest occurrence; the friendly name when there is one.
        Assert.Equal(["Microsoft OneDrive", "Spooler"], report.SlowStarters.Select(s => s.Name));
        Assert.Equal(TimeSpan.FromMilliseconds(3100), report.SlowStarters[0].Delay);
        Assert.Equal("服務", report.SlowStarters[1].Kind);
    }

    [Fact]
    public void Boot_times_need_elevation_to_read()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return;
        Assert.False(BootPerformance.Read().Readable);
    }

    [Fact]
    public void The_report_lists_what_needs_attention_first_and_escapes_what_it_shows()
    {
        var health = new ComputerHealth(
            new UpdateStatus(true, Now.AddDays(-60), [new FailedUpdate("2026-10 安全性修補程式 (KB5127648)", Now.AddDays(-3))]),
            [new ProblemDevice("USB <Camera>", "Camera", 28, "沒有安裝驅動程式", "更新驅動程式")],
            [new BatteryHealth("BAT0", 50_000, 25_000, 600)],
            [new PrinterEntry("Office Printer", true, false, true, 2)],
            [new DiagnosisStep("使用者帳戶控制", DiagnosisResult.Fail, "已關閉", "調回預設")],
            StabilityReader.Summarize([new("Microsoft-Windows-Kernel-Power", 41, Now.AddDays(-1), "")]));
        var data = new HealthReportData(SystemInfoReader.Read(), health, BootReport.Unreadable, [new AntivirusInfo("Defender", true, true)],
            RestartPending: true, Uptime: TimeSpan.FromDays(3), CreatedAt: Now.ToLocalTime());

        var concerns = HealthReport.Concerns(data);
        Assert.Contains(concerns, c => c.Contains("1 次不正常關機"));
        Assert.Contains(concerns, c => c.StartsWith("使用者帳戶控制"));
        Assert.Contains("已經 60 天沒有安裝 Windows 更新", concerns);
        Assert.Contains(concerns, c => c.Contains("USB <Camera>"));
        Assert.Contains("電池健康度只剩 50%", concerns);
        Assert.Contains("Windows 更新正在等待重新開機", concerns);
        Assert.True(concerns.ToList().FindIndex(c => c.Contains("不正常關機")) < concerns.ToList().FindIndex(c => c.Contains("電池")));

        var html = HealthReport.ToHtml(data);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("USB &lt;Camera&gt;", html);
        Assert.DoesNotContain("USB <Camera>", html);
        Assert.Contains(Environment.MachineName, html);
        Assert.Contains("以系統管理員執行才能讀取", html);
    }

    [Fact]
    public void The_report_has_a_command_line_and_an_html_file_name()
    {
        Assert.Equal(new CliCommand(CliAction.ExportReport, Path: @"\\server\share\"), CliCommand.Parse(["--export-report", @"\\server\share\"]));
        Assert.NotNull(CliCommand.Parse(["--export-report"]).Error);
        // A quoted folder ending in a backslash arrives with a stray quote instead of the backslash's closing quote.
        Assert.Equal(@"\\server\share\", CliCommand.Parse(["--export-software", "\\\\server\\share\\\""]).Path);
        using var temp = new TempDir();
        Assert.Equal(Path.Combine(temp.Path, "電腦健檢報告-PC01-20261114.html"),
            InventoryExport.ResolveTarget(temp.Path, HealthReport.Kind, "PC01", new DateTime(2026, 11, 14), ".html"));
    }
}
