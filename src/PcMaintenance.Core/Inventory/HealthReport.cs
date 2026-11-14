using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Maintenance;
using PcMaintenance.Core.Network;
using PcMaintenance.Core.Startup;
using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.Core.Inventory;

/// <summary>Limits shared by one-click maintenance, the computer information page and the health report.</summary>
public static class HealthLimits
{
    /// <summary>Windows ships security updates every month.</summary>
    public const int StaleUpdateDays = 40;
    /// <summary>Below this share of its design capacity a battery's shorter runtime is noticeable.</summary>
    public const int WornBatteryPercent = 60;
    public const int LongUptimeDays = 14;
    public const double LowFreePercent = 10;
}

public sealed record HealthReportData(
    ComputerInfo Info, ComputerHealth Health, BootReport Boot, IReadOnlyList<AntivirusInfo>? Antivirus,
    bool RestartPending, TimeSpan Uptime, DateTime CreatedAt)
{
    /// <summary>Reads everything the report shows; takes a few seconds.</summary>
    public static HealthReportData Collect(bool isElevated)
    {
        var info = SystemInfoReader.Read();
        return new(info, ComputerHealth.Read(isElevated, info), BootPerformance.Read(), SecurityStatus.ReadAntivirus(),
            RestartStatus.IsUpdateRestartPending(), RestartStatus.Uptime, DateTime.Now);
    }
}

/// <summary>
/// One printable page about a PC for whoever looks after it: what needs attention first, then the hardware, disks,
/// updates, security settings, what went wrong lately, start-up time, devices, battery and printers. Read only.
/// </summary>
public static class HealthReport
{
    public const string Kind = "電腦健檢報告";

    /// <summary>The things worth acting on, most serious first.</summary>
    public static IReadOnlyList<string> Concerns(HealthReportData d)
    {
        var list = new List<string>();
        var h = d.Health;
        foreach (var disk in d.Info.Disks.Disks.Where(x => !x.IsHealthy)) list.Add($"磁碟「{disk.Model}」回報健康狀態{disk.Health}，請盡快備份資料");
        if (h.Windows11?.Concern is { } windows) list.Add(windows);
        if (h.Stability.HasSystemProblems) list.Add($"最近 30 天有 {Problems(h.Stability)}");
        foreach (var f in h.Security.Where(s => s.Result == DiagnosisResult.Fail)) list.Add($"{f.Title}：{f.Detail}");
        switch (d.Antivirus)
        {
            case null: break;
            case { Count: 0 }: list.Add("找不到防毒軟體"); break;
            case var av when !av.Any(a => a.Enabled): list.Add("防毒軟體的即時保護沒有開啟"); break;
            case var av when !av.Any(a => a.Enabled && a.UpToDate): list.Add("防毒軟體的病毒碼不是最新"); break;
        }
        if (h.Updates.Readable)
        {
            if (h.Updates.LastInstalledUtc is { } last && (d.CreatedAt.Date - last.ToLocalTime().Date).TotalDays >= HealthLimits.StaleUpdateDays)
                list.Add($"已經 {(int)(d.CreatedAt.Date - last.ToLocalTime().Date).TotalDays} 天沒有安裝 Windows 更新");
            if (h.Updates.Failures.Count > 0) list.Add($"最近有 {h.Updates.Failures.Count} 個 Windows 更新安裝失敗");
        }
        if (h.Backup is { Read: true, AnyFound: false }) list.Add("沒有偵測到檔案備份（OneDrive 資料夾備份、檔案歷程記錄或備份軟體）");
        foreach (var v in d.Info.Disks.Volumes.Where(v => !v.IsRemovable && v.FreePercent < HealthLimits.LowFreePercent))
            list.Add($"{v.Letter}: 只剩 {v.FreePercent:0}% 空間");
        foreach (var device in h.Devices.Where(x => !x.IsDisabled)) list.Add($"裝置「{device.Name}」{device.Reason}");
        foreach (var f in h.Security.Where(s => s.Result == DiagnosisResult.Warning)) list.Add($"{f.Title}：{f.Detail}");
        foreach (var b in h.Batteries.Where(b => b.HealthPercent < HealthLimits.WornBatteryPercent)) list.Add($"電池健康度只剩 {b.HealthPercent}%");
        if (d.RestartPending) list.Add("Windows 更新正在等待重新開機");
        else if (d.Uptime.TotalDays >= HealthLimits.LongUptimeDays) list.Add($"已經連續開機 {(int)d.Uptime.TotalDays} 天");
        return list;
    }

    private static string Problems(StabilityReport s) => string.Join("、", new[]
    {
        s.BlueScreens.Count > 0 ? $" {s.BlueScreens.Count} 次藍色畫面" : "",
        s.UnexpectedShutdowns.Count > 0 ? $" {s.UnexpectedShutdowns.Count} 次不正常關機" : "",
        s.DiskErrors.Count > 0 ? $" {s.DiskErrors.Count} 次磁碟錯誤" : "",
        s.HardwareErrors.Count > 0 ? $" {s.HardwareErrors.Count} 次無法更正的硬體錯誤" : "",
    }.Where(x => x.Length > 0)).Trim();

    public static void Write(string path, HealthReportData data) => File.WriteAllText(path, ToHtml(data), new UTF8Encoding(true));

    public static string ToHtml(HealthReportData d)
    {
        var info = d.Info;
        var h = d.Health;
        var html = new StringBuilder();
        string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        string Or(string text) => text.Length > 0 ? text : "—";
        void Table(string title, IEnumerable<(string Label, string Value)> rows)
        {
            html.Append($"<section><h2>{E(title)}</h2><table>");
            foreach (var (label, value) in rows) html.Append($"<tr><th>{E(label)}</th><td>{E(value)}</td></tr>");
            html.Append("</table></section>");
        }
        string Date(DateTime utc) => utc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);

        html.Append("<!doctype html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">");
        html.Append($"<title>{E(Kind)} - {E(info.Machine.ComputerName)}</title><style>{Style}</style></head><body>");
        html.Append($"<header><h1>{E(Kind)}</h1><p>{E(info.Machine.ComputerName)}・{d.CreatedAt:yyyy/MM/dd HH:mm} 產生・電腦維護工具 {E(Version())}</p></header>");

        var concerns = Concerns(d);
        html.Append(concerns.Count == 0
            ? "<section class=\"summary ok\"><h2>檢查結果</h2><p>沒有發現需要處理的問題。</p></section>"
            : $"<section class=\"summary warn\"><h2>需要注意（{concerns.Count} 項）</h2><ol>{string.Concat(concerns.Select(c => $"<li>{E(c)}</li>"))}</ol></section>");

        var os = info.Os;
        var machine = info.Machine;
        Table("電腦",
        [
            ("電腦名稱", machine.ComputerName),
            (machine.IsDomainJoined ? "網域" : "工作群組", Or(machine.DomainOrWorkgroup)),
            ("製造商與型號", Or($"{machine.Manufacturer} {machine.Model}".Trim())),
            ("序號", Or(machine.SerialNumber)),
            ("Windows", $"{os.Name} {os.DisplayVersion}（組建 {os.Build}）"),
            ("安裝日期", os.InstallDate?.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) ?? "—"),
            ("上次開機", $"{os.LastBoot:yyyy/MM/dd HH:mm}（已連續開機 {(int)d.Uptime.TotalDays} 天）"),
            ("處理器", Or(info.Processor.Name)),
            ("記憶體", ByteSize.Format((long)info.Memory.InstalledPhysical)),
            ("顯示卡", Or(string.Join("、", info.Gpus.Select(g => g.Name)))),
            ("網路", Or(string.Join("；", info.Network.Select(n => $"{n.Name} {string.Join("、", n.IPv4)} {n.Mac}".Trim())))),
        ]);

        Table("磁碟",
        [
            .. info.Disks.Disks.Select(x => ($"磁碟 {x.Number}", $"{x.Model}・{x.MediaType}・{ByteSize.Format(x.SizeBytes)}・健康狀態{x.Health}")),
            .. info.Disks.Volumes.Where(v => !v.IsRemovable).Select(v => ($"{v.Letter}:", $"剩 {ByteSize.Format(v.FreeBytes)}／{ByteSize.Format(v.TotalBytes)}（{v.FreePercent:0}%）")),
        ]);

        Table("Windows 更新與防毒",
        [
            ("上次 Windows 更新", !h.Updates.Readable ? "無法讀取" : h.Updates.LastInstalledUtc is { } last ? Date(last) : "沒有安裝紀錄"),
            ("安裝失敗", h.Updates.Failures.Count == 0 ? "沒有" : string.Join("；", h.Updates.Failures.Select(f => f.Title))),
            ("等待重新開機", d.RestartPending ? "是" : "否"),
            ("防毒軟體", d.Antivirus is null ? "無法讀取" : d.Antivirus.Count == 0 ? "找不到" :
                string.Join("；", d.Antivirus.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
                    $"{g.Key}：{(g.Any(a => a.Enabled) ? "即時保護開啟" : "即時保護關閉")}、{(g.Any(a => a.UpToDate) ? "病毒碼最新" : "病毒碼不是最新")}"))),
            ("檔案備份", h.Backup.Read ? h.Backup.Summary : "無法讀取"),
        ]);

        if (h.Windows11 is { IsWindows11: false } upgrade)
            Table("Windows 11 升級", upgrade.Checks.Select(c => (c.Item, $"{c.Pass switch { true => "符合", false => "不符合", null => "無法判斷" }}：{c.Detail}")));

        html.Append("<section><h2>安全性設定</h2><table class=\"findings\">");
        foreach (var f in h.Security)
        {
            var (css, label) = f.Result switch
            {
                DiagnosisResult.Pass => ("pass", "正常"),
                DiagnosisResult.Warning => ("warn", "注意"),
                DiagnosisResult.Fail => ("fail", "有問題"),
                _ => ("info", "說明"),
            };
            html.Append($"<tr><th>{E(f.Title)}</th><td><span class=\"badge {css}\">{label}</span></td><td>{E(f.Detail)}" +
                        (f.NextStep is { } next && f.Result is DiagnosisResult.Warning or DiagnosisResult.Fail ? $"<div class=\"next\">建議：{E(next)}</div>" : "") +
                        "</td></tr>");
        }
        html.Append("</table></section>");

        var s = h.Stability;
        string Events(IReadOnlyList<StabilityEvent> list) => list.Count == 0 ? "沒有" : string.Join("；", list
            .GroupBy(e => e.Text)
            .Select(g => g.Count() == 1 ? $"{Date(g.First().TimeUtc)} {g.Key}" : $"{g.Key} {g.Count()} 次，最近一次 {Date(g.Max(e => e.TimeUtc))}")
            .Take(5));
        Table("穩定度（最近 30 天）", !s.Readable ? [("狀態", "無法讀取 Windows 事件記錄")] :
        [
            ("藍色畫面", Events(s.BlueScreens)),
            ("不正常關機", Events(s.UnexpectedShutdowns)),
            ("磁碟錯誤", Events(s.DiskErrors)),
            ("硬體錯誤", Events(s.HardwareErrors) + (s.CorrectedHardwareErrors > 0 ? $"（另有 {s.CorrectedHardwareErrors} 次已自動更正）" : "")),
            ("程式當機", s.ProgramCrashes.Count == 0 ? "沒有" : string.Join("、", s.ProgramCrashes.Take(8).Select(p => $"{p.Program}（{p.Count} 次）"))),
        ]);

        var boot = d.Boot;
        Table("開機", !boot.Readable ? [("開機時間", "以系統管理員執行才能讀取")] : boot.Last is not { } lastBoot ? [("開機時間", "Windows 還沒有記錄")] :
        [
            ("上次開機", $"到桌面 {lastBoot.ToDesktop.TotalSeconds:0} 秒、到完全就緒 {lastBoot.ToReady.TotalSeconds:0} 秒"),
            ("最近平均到桌面", $"{boot.AverageToDesktop!.Value.TotalSeconds:0} 秒（{Math.Min(10, boot.Boots.Count)} 次）"),
            ("拖慢開機", boot.SlowStarters.Count == 0 ? "沒有記錄" : string.Join("；", boot.SlowStarters.Take(5).Select(x => $"{x.Kind}「{x.Name}」多 {x.Delay.TotalSeconds:0.#} 秒"))),
        ]);

        Table("裝置、電池與印表機",
        [
            ("有問題的裝置", h.Devices.Count(x => !x.IsDisabled) == 0 ? "沒有" : string.Join("；", h.Devices.Where(x => !x.IsDisabled).Select(x => $"{x.Name}：{x.Reason}"))),
            .. h.Batteries.Select(b => ("電池", b.HealthPercent is { } p ? $"{b.Name}：健康度 {p}%" + (b.CycleCount is { } c ? $"、充電 {c} 次" : "") : $"{b.Name}：無法讀取容量")),
            ("印表機", h.Printers.Count == 0 ? "沒有" : string.Join("、", h.Printers.Select(p => p.Name + (p.IsDefault ? "（預設）" : "") + (p.Jobs > 0 ? $"（{p.Jobs} 個工作等待中）" : "")))),
        ]);

        html.Append("<footer>本報告由電腦維護工具產生，只讀取這台電腦的狀態，沒有變更任何設定。報告含電腦名稱、序號與網路位址，請只交給負責維護的人員。</footer>");
        html.Append("</body></html>");
        return html.ToString();
    }

    private static string Version()
    {
        var version = typeof(HealthReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        return version.Split('+')[0];
    }

    private const string Style =
        "@page{size:A4;margin:14mm}" +
        "body{font-family:'Microsoft JhengHei','Segoe UI',sans-serif;color:#1f2328;margin:24px auto;max-width:960px;font-size:14px;line-height:1.55}" +
        "header h1{margin:0;font-size:24px}header p{margin:4px 0 16px;color:#57606a}" +
        "h2{font-size:16px;margin:0 0 8px}section{margin:0 0 18px;break-inside:avoid}" +
        "table{border-collapse:collapse;width:100%}th,td{border-top:1px solid #d8dee4;padding:6px 8px;text-align:left;vertical-align:top}" +
        "th{width:9em;color:#57606a;font-weight:normal}table.findings td:nth-child(2){width:5em}" +
        ".summary{border-radius:8px;padding:12px 16px}.summary.ok{background:#e6f4ea}.summary.warn{background:#fff4d6}" +
        ".summary ol{margin:0;padding-left:1.4em}" +
        ".badge{display:inline-block;padding:0 8px;border-radius:10px;font-size:12px;white-space:nowrap}" +
        ".badge.pass{background:#e6f4ea;color:#05603a}.badge.warn{background:#fff4d6;color:#8a4b00}.badge.fail{background:#fde8e8;color:#b42318}" +
        ".badge.info{background:#e8f0fe;color:#1d4ed8}.next{color:#1d4ed8;margin-top:2px}" +
        "footer{color:#57606a;font-size:12px;border-top:1px solid #d8dee4;padding-top:8px}";
}
