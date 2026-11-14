using System.Globalization;
using System.Text.RegularExpressions;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Inventory;

/// <summary>The latest computer-information export of one PC, with what in it needs attention.</summary>
/// <param name="Trend">How fast the fullest drive is filling, from an export a few weeks older; null without one.</param>
public sealed record FleetComputer(
    string Name, string Windows, DateTime? LastUpdate, double? LowestFreePercent, DateTime? ExportedAt,
    IReadOnlyList<string> Concerns, IReadOnlyDictionary<string, string> Fields, string SourceFile, string? Trend = null)
{
    public bool NeedsAttention => Concerns.Count > 0;
}

/// <param name="FilesRead">Computer-information exports found in the folder.</param>
/// <param name="FilesSkipped">CSV files in the folder that are not exports this page reads, or could not be read.</param>
/// <param name="Software">Installed software and browser extensions over the PCs whose lists are in the folder.</param>
public sealed record FleetLoad(
    IReadOnlyList<FleetComputer> Computers, int FilesRead, int FilesSkipped, string? Problem,
    IReadOnlyList<FleetSoftware>? Software = null, int SoftwareComputers = 0)
{
    public IReadOnlyList<FleetSoftware> SoftwareList => Software ?? [];
}

/// <summary>
/// Gathers the exports many PCs wrote to one folder (--export-system, --export-software, --export-extensions or the
/// automatic export) into one row per PC from its latest export, judged by the same limits as one-click maintenance and
/// the health report, plus who has which software. Files named the way the program names them are picked by name, so a
/// folder holding years of daily exports reads only the newest few per PC; columns are found by their names, so exports
/// from older versions with fewer columns still read.
/// </summary>
public static partial class FleetSummary
{
    /// <summary>An export older than this suggests the PC is off, gone, or no longer runs the export.</summary>
    public const int StaleExportDays = 14;
    /// <summary>A disk filling at this pace or faster is reported before it runs out.</summary>
    public const int FillWarningDays = 90;
    private const int TrendMinDays = 14, TrendMaxDays = 120;
    private const int MaxListedFiles = 200_000, MaxUnnamedFiles = 2000;

    public static FleetLoad Load(string folder, DateTime now)
    {
        if (!Directory.Exists(folder)) return new FleetLoad([], 0, 0, $"找不到資料夾 {folder}");
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(folder).EnumerateFiles("*.csv", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Take(MaxListedFiles).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FleetLoad([], 0, 0, $"無法讀取資料夾：{UserMessage.For(ex)}");
        }

        var named = new List<(FileInfo File, string Kind, string Computer, DateTime Date)>();
        var unnamed = new List<FileInfo>();
        foreach (var file in files)
        {
            if (ExportName().Match(file.Name) is { Success: true } m && KindOf(m.Groups[1].Value) is { } kind &&
                DateTime.TryParseExact(m.Groups[3].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                named.Add((file, kind, m.Groups[2].Value, date));
            else unnamed.Add(file);
        }

        var systemFiles = new List<FileInfo>();
        var listFiles = new List<FileInfo>();
        foreach (var group in named.GroupBy(n => (n.Kind, n.Computer.ToUpperInvariant())))
        {
            var ordered = group.OrderByDescending(n => n.Date).ThenByDescending(n => n.File.LastWriteTimeUtc).ToList();
            if (group.Key.Kind != InventoryExport.SystemKind)
            {
                listFiles.Add(ordered[0].File);
                continue;
            }
            systemFiles.Add(ordered[0].File);
            // One export a few weeks older shows how fast the disks are filling.
            var newest = ordered[0].Date;
            if (ordered.LastOrDefault(n => (newest - n.Date).TotalDays is >= TrendMinDays and <= TrendMaxDays) is { File: { } older })
                systemFiles.Add(older);
        }

        var rows = new List<(IReadOnlyDictionary<string, string> Fields, string File)>();
        var installs = new List<(string Computer, FleetItem Item, DateTime Written)>();
        var systemFound = named.Count(n => n.Kind == InventoryExport.SystemKind);
        var skipped = 0;
        void Parse(FileInfo file, bool countSystem)
        {
            List<string[]> table;
            try
            {
                table = CsvFile.Read(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                return;
            }
            var header = table.FirstOrDefault() ?? [];
            switch (HeaderKind(header))
            {
                case InventoryExport.SystemKind:
                    if (countSystem) systemFound++;
                    foreach (var values in table.Skip(1)) rows.Add((Fields(header, values), file.FullName));
                    break;
                case InventoryExport.SoftwareKind or InventoryExport.ExtensionsKind:
                    foreach (var values in table.Skip(1))
                    {
                        var fields = Fields(header, values);
                        if (FleetSoftwareList.ItemFrom(fields) is { } item && fields.GetValueOrDefault("電腦名稱", "") is { Length: > 0 } computer)
                            installs.Add((computer, item, file.LastWriteTimeUtc));
                    }
                    break;
                default:
                    skipped++;
                    break;
            }
        }
        foreach (var file in systemFiles.Concat(listFiles)) Parse(file, countSystem: false);
        foreach (var file in unnamed.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxUnnamedFiles)) Parse(file, countSystem: true);
        skipped += Math.Max(0, unnamed.Count - MaxUnnamedFiles);

        // A PC whose list sits in the folder twice under other names counts once, from the newest file.
        var newestLists = installs
            .GroupBy(i => (i.Computer.ToUpperInvariant(), i.Item.Kind == FleetSoftwareList.SoftwareKind))
            .SelectMany(g => g.Where(i => i.Written == g.Max(x => x.Written)))
            .Select(i => (i.Computer, i.Item))
            .ToList();
        var software = FleetSoftwareList.Aggregate(newestLists);
        var softwareComputers = newestLists.Select(i => i.Computer.ToUpperInvariant()).Distinct().Count();
        return new FleetLoad(Summarize(rows, now), systemFound, skipped, null, software, softwareComputers);
    }

    private static string? KindOf(string prefix) => prefix switch
    {
        InventoryExport.SystemKind or InventoryExport.SoftwareKind or InventoryExport.ExtensionsKind => prefix,
        _ => null,
    };

    /// <summary>Exports are recognised by their columns, so renamed files still count.</summary>
    private static string? HeaderKind(string[] header)
    {
        if (header.FirstOrDefault() != "電腦名稱") return null;
        if (header.Contains("組建")) return InventoryExport.SystemKind;
        if (header.Contains("瀏覽器") && header.Contains("識別碼")) return InventoryExport.ExtensionsKind;
        if (header.Contains("名稱") && header.Contains("版本") && !header.Contains("類型")) return InventoryExport.SoftwareKind;
        return null;
    }

    private static Dictionary<string, string> Fields(string[] header, string[] values) =>
        header.Select((name, i) => (name, value: i < values.Length ? values[i] : ""))
            .GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().value);

    /// <summary>"電腦配備-會計-01-20261114.csv": the kind, then the computer name (which may contain dashes), then the date.</summary>
    [GeneratedRegex(@"^([^-]+)-(.+)-(\d{8})\.csv$", RegexOptions.IgnoreCase)]
    private static partial Regex ExportName();

    /// <summary>One computer per name, from its newest export; an older one a few weeks back gives the disk trend.</summary>
    public static IReadOnlyList<FleetComputer> Summarize(IEnumerable<(IReadOnlyDictionary<string, string> Fields, string File)> rows, DateTime now) => rows
        .Where(r => r.Fields.GetValueOrDefault("電腦名稱", "").Length > 0)
        .GroupBy(r => r.Fields["電腦名稱"], StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var ordered = g.OrderByDescending(r => Date(r.Fields.GetValueOrDefault("匯出時間", "")) ?? DateTime.MinValue).ToList();
            var newest = ordered[0];
            var newestAt = Date(newest.Fields.GetValueOrDefault("匯出時間", ""));
            var older = newestAt is { } at
                ? ordered.Skip(1).LastOrDefault(r => Date(r.Fields.GetValueOrDefault("匯出時間", "")) is { } t && (at - t).TotalDays is >= TrendMinDays and <= TrendMaxDays)
                : default;
            return Describe(newest.Fields, newest.File, older.Fields, now);
        })
        .OrderByDescending(c => c.NeedsAttention).ThenBy(c => c.Name, StringComparer.CurrentCulture)
        .ToList();

    private static FleetComputer Describe(IReadOnlyDictionary<string, string> f, string file, IReadOnlyDictionary<string, string>? older, DateTime now)
    {
        string Field(string name) => f.GetValueOrDefault(name, "").Trim();
        var concerns = new List<string>();
        var windowsName = Field("Windows");

        if (windowsName.Contains("Windows 10", StringComparison.Ordinal))
        {
            var upgrade = Field("Windows 11 升級");
            concerns.Add(upgrade switch
            {
                "可以升級" => "Windows 10 已結束支援，可以升級到 Windows 11",
                _ when upgrade.StartsWith("不能升級：", StringComparison.Ordinal) => $"Windows 10 已結束支援，硬體不能升級（{upgrade[5..]}）",
                _ when upgrade.StartsWith("無法判斷：", StringComparison.Ordinal) => $"Windows 10 已結束支援，請確認能否升級（{upgrade[5..]}）",
                _ => "Windows 10 已結束支援",
            });
        }

        var updateText = Field("上次 Windows 更新");
        var lastUpdate = Date(updateText);
        if (lastUpdate is { } updated && (now.Date - updated.Date).TotalDays >= HealthLimits.StaleUpdateDays)
            concerns.Add($"{(int)(now.Date - updated.Date).TotalDays} 天沒有安裝 Windows 更新");
        else if (updateText is "無法讀取" or "沒有紀錄") concerns.Add("讀不到 Windows 更新紀錄");
        if (Field("更新安裝失敗").Length > 0) concerns.Add("有更新安裝失敗");

        foreach (Match m in UnhealthyDisk().Matches(Field("磁碟"))) concerns.Add($"磁碟健康狀態{m.Groups[1].Value}");

        var volumes = Volumes(Field("磁碟空間"));
        double? lowest = volumes.Count > 0 ? volumes.Min(v => v.Percent) : null;
        foreach (var v in volumes.Where(v => v.Percent < HealthLimits.LowFreePercent)) concerns.Add($"{v.Letter}: 只剩 {v.Percent:0}% 空間");

        var exported = Date(Field("匯出時間"));
        string? trend = null;
        if (older is not null && exported is { } newAt && Date(older.GetValueOrDefault("匯出時間", "")) is { } oldAt)
        {
            var days = (newAt - oldAt).TotalDays;
            var before = Volumes(older.GetValueOrDefault("磁碟空間", "")).ToDictionary(v => v.Letter, StringComparer.OrdinalIgnoreCase);
            var filling = volumes
                .Where(v => before.ContainsKey(v.Letter) && v.TotalBytes > 0)
                .Select(v => (Volume: v, PerDay: (before[v.Letter].FreeBytes - v.FreeBytes) / days))
                .Where(x => x.PerDay * 30 >= GB)
                .Select(x => (x.Volume, x.PerDay, DaysLeft: (x.Volume.FreeBytes - x.Volume.TotalBytes * HealthLimits.LowFreePercent / 100) / x.PerDay))
                .OrderBy(x => x.DaysLeft)
                .ToList();
            if (filling.Count > 0)
            {
                trend = string.Join("；", filling.Select(x => $"{x.Volume.Letter}: 每月少 {ByteSize.Format((long)(x.PerDay * 30))}"));
                foreach (var x in filling.Where(x => x.Volume.Percent >= HealthLimits.LowFreePercent && x.DaysLeft <= FillWarningDays))
                    concerns.Add($"{x.Volume.Letter}: 照目前速度約 {Math.Max(1, (int)x.DaysLeft)} 天後空間不足");
            }
        }

        if (Field("有問題的裝置") is { Length: > 0 } devices) concerns.Add($"有問題的裝置 {devices.Split('；').Length} 個");
        foreach (Match m in Percent().Matches(Field("電池健康度")))
        {
            if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) < HealthLimits.WornBatteryPercent) concerns.Add($"電池健康度 {m.Groups[1].Value}%");
        }
        if (Field("安全性建議") is { Length: > 0 } security) concerns.Add($"安全性：{security.Replace("；", "、")}");
        if (Field("備份") == SystemInfo.BackupStatus.NothingFound) concerns.Add("沒有偵測到檔案備份");
        var problems = Field("最近 30 天的問題").Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith("程式當機", StringComparison.Ordinal) && p != "無法讀取").ToList();
        if (problems.Count > 0) concerns.Add($"最近 30 天：{string.Join("、", problems.Select(p => p + " 次"))}");

        if (exported is { } exportedAt && Date(Field("上次開機")) is { } boot && (exportedAt - boot).TotalDays >= HealthLimits.LongUptimeDays)
            concerns.Add($"已經 {(int)(exportedAt - boot).TotalDays} 天沒有重新開機");
        if (exported is { } at && (now - at).TotalDays >= StaleExportDays) concerns.Add($"資料已經 {(int)(now - at).TotalDays} 天沒有更新");

        var windows = string.Join(" ", new[] { windowsName.Replace("Windows ", "", StringComparison.Ordinal), Field("版本") }.Where(s => s.Length > 0));
        return new FleetComputer(Field("電腦名稱"), windows, lastUpdate, lowest, exported, concerns, f, file, trend);
    }

    private const double GB = 1024.0 * 1024 * 1024;

    private sealed record Volume(string Letter, double FreeBytes, double TotalBytes, double Percent);

    private static List<Volume> Volumes(string text) => VolumeSpace().Matches(text).Select(m => new Volume(
        m.Groups[1].Value,
        Bytes(m.Groups[2].Value, m.Groups[3].Value),
        Bytes(m.Groups[4].Value, m.Groups[5].Value),
        double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture))).ToList();

    private static double Bytes(string number, string unit) =>
        double.Parse(number, CultureInfo.InvariantCulture) * unit.ToUpperInvariant() switch
        {
            "TB" => GB * 1024,
            "GB" => GB,
            "MB" => 1024.0 * 1024,
            "KB" => 1024.0,
            _ => 1,
        };

    private static DateTime? Date(string text) =>
        DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    [GeneratedRegex(@"（(警告|不良)）")]
    private static partial Regex UnhealthyDisk();

    /// <summary>"C: 剩 242 GB／930 GB（26%）", as the computer-information export writes each drive.</summary>
    [GeneratedRegex(@"([A-Za-z]): 剩 ([\d.]+) ([KMGT]?B)／([\d.]+) ([KMGT]?B)（(\d+)%）")]
    private static partial Regex VolumeSpace();

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex Percent();

    public const string Kind = "電腦彙整";

    /// <summary>One row per computer: what needs attention, then the main columns of its latest export.</summary>
    public static int WriteSummary(string path, IReadOnlyList<FleetComputer> computers, DateTime now)
    {
        string[] passThrough = ["網域／工作群組", "製造商", "型號", "序號", "Windows", "版本", "組建", "處理器", "記憶體", "磁碟", "磁碟空間", "IPv4 位址",
            "上次 Windows 更新", "有問題的裝置", "電池健康度", "安全性建議", "最近 30 天的問題", "上次開機", "Windows 11 升級", "備份", "匯出時間"];
        CsvFile.Write(path, ["電腦名稱", "需要注意", "磁碟用量趨勢", .. passThrough, "來源檔案", "彙整時間"],
            computers.Select(c => (IReadOnlyList<string>)[
                c.Name, string.Join("；", c.Concerns), c.Trend ?? "", .. passThrough.Select(name => c.Fields.GetValueOrDefault(name, "")),
                c.SourceFile, now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)]));
        return computers.Count;
    }
}
