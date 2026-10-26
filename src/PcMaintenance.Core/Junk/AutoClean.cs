using System.Text.Json;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Junk;

public sealed record AutoCleanItem(string Category, long Bytes, int Files, string? Note);

public sealed record AutoCleanReport(IReadOnlyList<AutoCleanItem> Items, bool DryRun)
{
    public long Bytes => Items.Sum(i => i.Bytes);
    public int Files => Items.Sum(i => i.Files);
}

/// <summary>
/// Unattended cleaning for logon or scheduled runs. Only categories checked by default are cleaned (never the
/// recycle bin), those needing elevation only when elevated, and a category is skipped while its program runs
/// instead of closing anything.
/// </summary>
public sealed class AutoCleaner(AppSettings settings, FileLog? log = null, Func<string, bool>? isProcessRunning = null)
{
    public AutoCleanReport Run(IReadOnlyList<JunkCategory> categories, bool isElevated, bool dryRun, CancellationToken ct = default)
    {
        var scanner = new JunkScanner(settings, isProcessRunning);
        var cleaner = new JunkCleaner(settings, log, isProcessRunning);
        var items = new List<AutoCleanItem>();
        foreach (var category in categories.Where(c => c.DefaultSelected && !c.IsRecycleBin && (isElevated || !c.RequiresAdmin)))
        {
            var scan = scanner.Scan(category, isElevated, ct);
            if (!scan.CanClean)
            {
                items.Add(new AutoCleanItem(category.Name, 0, 0, scan.Warning));
                log?.Info($"自動清理略過「{category.Name}」：{scan.Warning}");
            }
            else if (dryRun)
            {
                var count = (int)Math.Min(scan.ItemCount, int.MaxValue);
                items.Add(new AutoCleanItem(category.Name, scan.TotalBytes, count, null));
                log?.Info($"自動清理試跑「{category.Name}」：可清理 {count:N0} 個檔案（{ByteSize.Format(scan.TotalBytes)}）");
            }
            else
            {
                var result = cleaner.Clean(scan, ct);
                items.Add(new AutoCleanItem(category.Name, result.FreedBytes, result.Deleted, result.Note));
            }
        }

        var report = new AutoCleanReport(items, dryRun);
        log?.Info(dryRun
            ? $"自動清理試跑完成：可清理 {report.Files:N0} 個檔案，約 {ByteSize.Format(report.Bytes)}，沒有刪除任何檔案"
            : $"自動清理完成：刪除 {report.Files:N0} 個檔案，釋放 {ByteSize.Format(report.Bytes)}");
        return report;
    }
}

public sealed record AutoCleanState(DateTimeOffset LastRun, long FreedBytes, int DeletedFiles);

/// <summary>Per-user record of the last unattended run, so a logon task cleans at most once per interval.</summary>
public static class AutoCleanSchedule
{
    /// <summary>A last run dated in the future means the clock was turned back, so it does not block cleaning.</summary>
    public static bool IsDue(AutoCleanState? last, DateTimeOffset now, int intervalDays) =>
        last is null || intervalDays <= 0 || last.LastRun > now || now - last.LastRun >= TimeSpan.FromDays(intervalDays);

    public static AutoCleanState? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<AutoCleanState>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string file, AutoCleanState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, file, overwrite: true);
    }
}
