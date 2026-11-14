using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.App.ViewModels;

/// <summary>How update, device and battery findings read on the computer information page and in one-click maintenance.</summary>
public static class HealthText
{
    /// <summary>Months without a Windows update before one-click maintenance calls it out; patches come every month.</summary>
    public const int StaleUpdateDays = 40;

    /// <summary>Below this share of its design capacity a battery's shorter runtime is noticeable.</summary>
    public const int WornBatteryPercent = 60;

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
