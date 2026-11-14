using System.Globalization;
using System.Text.RegularExpressions;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Inventory;

/// <summary>The latest computer-information export of one PC, with what in it needs attention.</summary>
public sealed record FleetComputer(
    string Name, string Windows, DateTime? LastUpdate, double? LowestFreePercent, DateTime? ExportedAt,
    IReadOnlyList<string> Concerns, IReadOnlyDictionary<string, string> Fields, string SourceFile)
{
    public bool NeedsAttention => Concerns.Count > 0;
}

/// <param name="FilesSkipped">CSV files in the folder that are not computer-information exports, or could not be read.</param>
public sealed record FleetLoad(IReadOnlyList<FleetComputer> Computers, int FilesRead, int FilesSkipped, string? Problem);

/// <summary>
/// Gathers the computer-information exports many PCs wrote to one folder (--export-system "\\server\share\") into
/// one row per PC, its latest export, judged by the same limits as one-click maintenance and the health report.
/// Columns are found by their names, so exports from older versions with fewer columns still read.
/// </summary>
public static partial class FleetSummary
{
    /// <summary>An export older than this suggests the PC is off, gone, or no longer runs the export.</summary>
    public const int StaleExportDays = 14;
    private const int MaxFiles = 5000;

    public static FleetLoad Load(string folder, DateTime now)
    {
        if (!Directory.Exists(folder)) return new FleetLoad([], 0, 0, $"找不到資料夾 {folder}");
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.csv", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Take(MaxFiles).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FleetLoad([], 0, 0, $"無法讀取資料夾：{UserMessage.For(ex)}");
        }
        var rows = new List<(IReadOnlyDictionary<string, string> Fields, string File)>();
        var read = 0;
        var skipped = 0;
        foreach (var file in files)
        {
            List<string[]> table;
            try
            {
                table = CsvFile.Read(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }
            // A computer-information export starts with the computer name and carries the Windows build.
            if (table.Count < 2 || table[0].FirstOrDefault() != "電腦名稱" || !table[0].Contains("組建"))
            {
                skipped++;
                continue;
            }
            read++;
            var header = table[0];
            foreach (var values in table.Skip(1))
                rows.Add((header.Select((name, i) => (name, value: i < values.Length ? values[i] : ""))
                    .GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().value), file));
        }
        return new FleetLoad(Summarize(rows, now), read, skipped, null);
    }

    /// <summary>One computer per name, from its newest export.</summary>
    public static IReadOnlyList<FleetComputer> Summarize(IEnumerable<(IReadOnlyDictionary<string, string> Fields, string File)> rows, DateTime now) => rows
        .Where(r => r.Fields.GetValueOrDefault("電腦名稱", "").Length > 0)
        .GroupBy(r => r.Fields["電腦名稱"], StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(r => Date(r.Fields.GetValueOrDefault("匯出時間", "")) ?? DateTime.MinValue).First())
        .Select(r => Describe(r.Fields, r.File, now))
        .OrderByDescending(c => c.NeedsAttention).ThenBy(c => c.Name, StringComparer.CurrentCulture)
        .ToList();

    private static FleetComputer Describe(IReadOnlyDictionary<string, string> f, string file, DateTime now)
    {
        string Field(string name) => f.GetValueOrDefault(name, "").Trim();
        var concerns = new List<string>();

        var updateText = Field("上次 Windows 更新");
        var lastUpdate = Date(updateText);
        if (lastUpdate is { } updated && (now.Date - updated.Date).TotalDays >= HealthLimits.StaleUpdateDays)
            concerns.Add($"{(int)(now.Date - updated.Date).TotalDays} 天沒有安裝 Windows 更新");
        else if (updateText is "無法讀取" or "沒有紀錄") concerns.Add("讀不到 Windows 更新紀錄");
        if (Field("更新安裝失敗").Length > 0) concerns.Add("有更新安裝失敗");

        foreach (Match m in UnhealthyDisk().Matches(Field("磁碟"))) concerns.Add($"磁碟健康狀態{m.Groups[1].Value}");

        double? lowest = null;
        foreach (Match m in VolumeSpace().Matches(Field("磁碟空間")))
        {
            var percent = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            lowest = Math.Min(lowest ?? 100, percent);
            if (percent < HealthLimits.LowFreePercent) concerns.Add($"{m.Groups[1].Value}: 只剩 {percent:0}% 空間");
        }

        if (Field("有問題的裝置") is { Length: > 0 } devices) concerns.Add($"有問題的裝置 {devices.Split('；').Length} 個");
        foreach (Match m in Percent().Matches(Field("電池健康度")))
        {
            if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) < HealthLimits.WornBatteryPercent) concerns.Add($"電池健康度 {m.Groups[1].Value}%");
        }
        if (Field("安全性建議") is { Length: > 0 } security) concerns.Add($"安全性：{security.Replace("；", "、")}");
        var problems = Field("最近 30 天的問題").Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith("程式當機", StringComparison.Ordinal) && p != "無法讀取").ToList();
        if (problems.Count > 0) concerns.Add($"最近 30 天：{string.Join("、", problems.Select(p => p + " 次"))}");

        var exported = Date(Field("匯出時間"));
        if (exported is { } at && (now - at).TotalDays >= StaleExportDays) concerns.Add($"資料已經 {(int)(now - at).TotalDays} 天沒有更新");

        var windows = string.Join(" ", new[] { Field("Windows").Replace("Windows ", "", StringComparison.Ordinal), Field("版本") }.Where(s => s.Length > 0));
        return new FleetComputer(Field("電腦名稱"), windows, lastUpdate, lowest, exported, concerns, f, file);
    }

    private static DateTime? Date(string text) =>
        DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    [GeneratedRegex(@"（(警告|不良)）")]
    private static partial Regex UnhealthyDisk();

    [GeneratedRegex(@"([A-Za-z]): 剩 [^；]*?（(\d+)%）")]
    private static partial Regex VolumeSpace();

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex Percent();

    public const string Kind = "電腦彙整";

    /// <summary>One row per computer: what needs attention, then the main columns of its latest export.</summary>
    public static int WriteSummary(string path, IReadOnlyList<FleetComputer> computers, DateTime now)
    {
        string[] passThrough = ["網域／工作群組", "製造商", "型號", "序號", "Windows", "版本", "組建", "處理器", "記憶體", "磁碟", "磁碟空間", "IPv4 位址",
            "上次 Windows 更新", "有問題的裝置", "電池健康度", "安全性建議", "最近 30 天的問題", "匯出時間"];
        CsvFile.Write(path, ["電腦名稱", "需要注意", .. passThrough, "來源檔案", "彙整時間"],
            computers.Select(c => (IReadOnlyList<string>)[
                c.Name, string.Join("；", c.Concerns), .. passThrough.Select(name => c.Fields.GetValueOrDefault(name, "")),
                c.SourceFile, now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)]));
        return computers.Count;
    }
}
