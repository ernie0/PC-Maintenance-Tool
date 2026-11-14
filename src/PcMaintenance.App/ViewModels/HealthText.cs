using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Network;
using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.App.ViewModels;

/// <summary>How update, device and battery findings read on the computer information page and in one-click maintenance.</summary>
public static class HealthText
{
    public const int StaleUpdateDays = HealthLimits.StaleUpdateDays;
    public const int WornBatteryPercent = HealthLimits.WornBatteryPercent;

    public static int? DaysSinceUpdate(UpdateStatus status, DateTime now) =>
        status.LastInstalledUtc is { } last ? Math.Max(0, (int)(now.Date - last.ToLocalTime().Date).TotalDays) : null;

    public static string LastUpdate(UpdateStatus status, DateTime now)
    {
        if (!status.Readable) return "無法讀取 Windows Update 紀錄";
        if (status.LastInstalledUtc is not { } last) return "沒有安裝紀錄";
        var days = DaysSinceUpdate(status, now)!.Value;
        return $"{last.ToLocalTime():yyyy/MM/dd}（{(days == 0 ? "今天" : $"{days} 天前")}）";
    }

    public static string Failures(UpdateStatus status) => status.Failures.Count switch
    {
        0 => "",
        1 => status.Failures[0].Title,
        var n => $"{n} 個：{string.Join("；", status.Failures.Take(3).Select(f => f.Title))}{(n > 3 ? "…" : "")}",
    };

    /// <summary>Labels that fit the narrow label column of the computer information page.</summary>
    public static string ShortTitle(string title) => title switch
    {
        "舊版檔案分享（SMBv1）" => "SMBv1",
        "使用者帳戶控制" => "帳戶控制",
        "系統管理員帳號" => "管理員帳號",
        _ => title,
    };

    public static IReadOnlyList<LabeledValue> Security(IReadOnlyList<DiagnosisStep> findings) => findings
        .Select(f => new LabeledValue(ShortTitle(f.Title),
            f.Result is DiagnosisResult.Warning or DiagnosisResult.Fail && f.NextStep is { } next ? $"{f.Detail}。建議：{next}" : f.Detail))
        .ToList();

    public static IReadOnlyList<LabeledValue> Stability(StabilityReport report)
    {
        if (!report.Readable) return [new("狀態", "無法讀取 Windows 事件記錄")];
        static string Events(IReadOnlyList<StabilityEvent> list) =>
            list.Count == 0 ? "沒有" : $"{list.Count} 次，最近一次 {list[0].TimeUtc.ToLocalTime():yyyy/MM/dd HH:mm}：{list[0].Text}";
        var hardware = report.HardwareErrors.Count > 0
            ? Events(report.HardwareErrors)
            : report.CorrectedHardwareErrors > 0 ? $"沒有（另有 {report.CorrectedHardwareErrors} 次硬體已自動更正的錯誤，多半不影響使用）" : "沒有";
        return
        [
            new("藍色畫面", Events(report.BlueScreens)),
            new("不正常關機", Events(report.UnexpectedShutdowns)),
            new("磁碟錯誤", Events(report.DiskErrors)),
            new("硬體錯誤", hardware),
            new("程式當機", ProgramCrashes(report)),
        ];
    }

    public static string ProgramCrashes(StabilityReport report) => report.ProgramCrashes.Count == 0
        ? "沒有"
        : $"{report.ProgramCrashes.Sum(p => p.Count)} 次：" + string.Join("、", report.ProgramCrashes.Take(3).Select(p => $"{p.Program}（{p.Count} 次）")) +
          (report.ProgramCrashes.Count > 3 ? $" 等 {report.ProgramCrashes.Count} 個程式" : "");

    /// <summary>One line naming the system problems found, e.g. "2 次藍色畫面、1 次不正常關機".</summary>
    public static string SystemProblems(StabilityReport report) => string.Join("、", new[]
    {
        report.BlueScreens.Count > 0 ? $"{report.BlueScreens.Count} 次藍色畫面" : "",
        report.UnexpectedShutdowns.Count > 0 ? $"{report.UnexpectedShutdowns.Count} 次不正常關機" : "",
        report.DiskErrors.Count > 0 ? $"{report.DiskErrors.Count} 次磁碟錯誤" : "",
        report.HardwareErrors.Count > 0 ? $"{report.HardwareErrors.Count} 次無法更正的硬體錯誤" : "",
    }.Where(s => s.Length > 0));

    public static IReadOnlyList<LabeledValue> Battery(BatteryHealth battery, bool isElevated)
    {
        var rows = new List<LabeledValue> { new("電池", battery.Name) };
        if (battery.HealthPercent is { } percent)
        {
            rows.Add(new("健康度", $"{percent}%（新的時候 {battery.DesignedMWh:N0} mWh，現在充飽 {battery.FullChargeMWh:N0} mWh）"));
            if (percent < WornBatteryPercent) rows.Add(new("建議", "電池已明顯老化，充飽後能用的時間會變短，可以考慮更換"));
        }
        else
        {
            rows.Add(new("健康度", isElevated ? "這顆電池沒有提供容量資訊" : "以系統管理員重新啟動後才能讀取容量"));
        }
        if (battery.CycleCount is { } cycles) rows.Add(new("充電次數", $"{cycles:N0} 次"));
        return rows;
    }
}
