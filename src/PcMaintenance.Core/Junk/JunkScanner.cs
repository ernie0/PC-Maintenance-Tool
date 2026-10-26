using System.Diagnostics;
using System.IO.Enumeration;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Junk;

public sealed class JunkScanner(AppSettings settings, Func<string, bool>? isProcessRunning = null)
{
    private readonly Func<string, bool> _isProcessRunning = isProcessRunning ?? ProcessCheck.IsRunning;

    private const int ProgressIntervalMs = 200;

    /// <param name="progress">Told every so often how many files were read, for folders that take a while.</param>
    public JunkScanResult Scan(JunkCategory category, bool isElevated, CancellationToken ct = default, Action<JunkScanProgress>? progress = null)
    {
        if (category.RequiresAdmin && !isElevated)
            return new JunkScanResult { Category = category, CanClean = false, Warning = "需要系統管理員權限" };

        if (category.IsRecycleBin)
        {
            var (bytes, items) = RecycleBin.Query();
            return new JunkScanResult { Category = category, TotalBytes = bytes, ItemCount = items };
        }

        var cutoff = DateTime.UtcNow.AddHours(-settings.MinFileAgeHours);
        var files = new List<JunkFile>();
        var read = new ReadCount(progress);
        string? warning = null;
        foreach (var location in category.Locations)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(location.Path)) continue;
            if (PathSafety.IsReparsePoint(location.Path))
            {
                warning = "部分資料夾是連結，已略過";
                continue;
            }
            if (location.FileName is { } pattern)
            {
                foreach (var fileName in MatchingNames(location.Path, pattern)) AddSingleFile(location, fileName, cutoff, files);
            }
            else if (!TryEnumerate(location, cutoff, files, read, ct))
            {
                warning ??= "部分資料夾無法讀取，已略過";
            }
        }

        var blocking = category.BlockingProcesses.FirstOrDefault(_isProcessRunning);
        if (blocking is not null) warning = $"{category.BlockingAppName ?? blocking} 正在執行，請先關閉再清理";

        return new JunkScanResult
        {
            Category = category,
            Files = files,
            TotalBytes = files.Sum(f => f.Size),
            ItemCount = files.Count,
            Warning = warning,
            CanClean = blocking is null,
        };
    }

    private static IEnumerable<string> MatchingNames(string folder, string pattern)
    {
        if (pattern.IndexOfAny(['*', '?']) < 0) return [pattern];
        try
        {
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void AddSingleFile(JunkLocation location, string fileName, DateTime cutoffUtc, List<JunkFile> into)
    {
        var info = new FileInfo(System.IO.Path.Combine(location.Path, fileName));
        try
        {
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || settings.IsExcluded(info.FullName)) return;
            if (location.ApplyAgeFilter && (info.LastWriteTimeUtc > cutoffUtc || info.CreationTimeUtc > cutoffUtc)) return;
            into.Add(new JunkFile(info.FullName, info.Length, info.LastWriteTimeUtc, location.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool TryEnumerate(JunkLocation location, DateTime cutoffUtc, List<JunkFile> into, ReadCount read, CancellationToken ct)
    {
        var root = location.Path;
        var applyAge = location.ApplyAgeFilter;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Do not descend into junctions or count symlinked files.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            var entries = new FileSystemEnumerable<JunkFile?>(root, (ref FileSystemEntry e) =>
            {
                if (applyAge && (e.LastWriteTimeUtc.UtcDateTime > cutoffUtc || e.CreationTimeUtc.UtcDateTime > cutoffUtc))
                    return null;
                return new JunkFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime, root);
            }, options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            };
            foreach (var file in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (file is not null && !settings.IsExcluded(file.Path)) into.Add(file);
                read.Add(into);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Counts every file looked at, including recent ones that are kept, and passes the figures on at most every 200 ms.
    private sealed class ReadCount(Action<JunkScanProgress>? progress)
    {
        private int _files;
        private long _nextReport = Environment.TickCount64 + ProgressIntervalMs;

        public void Add(List<JunkFile> found)
        {
            _files++;
            if (progress is null || Environment.TickCount64 < _nextReport) return;
            _nextReport = Environment.TickCount64 + ProgressIntervalMs;
            progress(new JunkScanProgress(_files, found.Count));
        }
    }
}

internal static class ProcessCheck
{
    public static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }
}
