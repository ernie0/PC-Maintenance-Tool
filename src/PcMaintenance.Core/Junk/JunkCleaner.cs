using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Junk;

public sealed class JunkCleaner(AppSettings settings, FileLog? log = null, Func<string, bool>? isProcessRunning = null)
{
    private readonly Func<string, bool> _isProcessRunning = isProcessRunning ?? ProcessCheck.IsRunning;

    /// <summary>
    /// Deletes exactly the files found by the scan. Each file is re-checked first: anything that
    /// moved behind a link, changed since the scan, or sits in an excluded folder is skipped.
    /// </summary>
    public CleanResult Clean(JunkScanResult scan, CancellationToken ct = default)
    {
        var category = scan.Category;
        if (!scan.CanClean) return new CleanResult { Note = scan.Warning ?? "此項目目前無法清理" };

        if (category.IsRecycleBin)
        {
            var emptied = RecycleBin.Empty();
            log?.Info($"清空資源回收筒：{(emptied ? "成功" : "失敗")}");
            return emptied
                ? new CleanResult { Deleted = (int)Math.Min(scan.ItemCount, int.MaxValue), FreedBytes = scan.TotalBytes }
                : new CleanResult { Note = "清空資源回收筒失敗" };
        }

        var blocking = category.BlockingProcesses.FirstOrDefault(_isProcessRunning);
        if (blocking is not null) return new CleanResult { Note = $"{category.BlockingAppName ?? blocking} 正在執行，已略過" };

        int deleted = 0, inUse = 0, changed = 0, denied = 0, unsafePath = 0;
        long freed = 0;
        foreach (var file in scan.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!PathSafety.IsSameOrUnder(file.Root, file.Path) || settings.IsExcluded(file.Path))
            {
                unsafePath++;
                continue;
            }
            var info = new FileInfo(file.Path);
            if (!info.Exists) continue;
            if (PathSafety.HasReparsePointOnPath(file.Root, file.Path))
            {
                unsafePath++;
                continue;
            }
            try
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    unsafePath++;
                    continue;
                }
                if (info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteUtc)
                {
                    changed++;
                    continue;
                }
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                deleted++;
                freed += file.Size;
            }
            catch (UnauthorizedAccessException)
            {
                denied++;
            }
            catch (IOException)
            {
                inUse++;
            }
        }

        // A single-file location sits in a folder that is not ours to tidy (for example C:\Windows).
        foreach (var location in category.Locations.Where(l => l.FileName is null)) RemoveEmptyFolders(location, ct);

        log?.Info($"清理「{category.Name}」：刪除 {deleted} 個檔案（{ByteSize.Format(freed)}），" +
                  $"略過 使用中 {inUse}、已變更 {changed}、無權限 {denied}、路徑不安全 {unsafePath}");
        return new CleanResult
        {
            Deleted = deleted,
            FreedBytes = freed,
            SkippedInUse = inUse,
            SkippedChanged = changed,
            SkippedDenied = denied,
            SkippedUnsafe = unsafePath,
        };
    }

    /// <summary>Removes empty subfolders deepest-first; the location folder itself is kept.</summary>
    private void RemoveEmptyFolders(JunkLocation location, CancellationToken ct)
    {
        if (!Directory.Exists(location.Path) || PathSafety.IsReparsePoint(location.Path)) return;
        var cutoff = DateTime.UtcNow.AddHours(-settings.MinFileAgeHours);
        List<string> folders;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            folders = Directory.EnumerateDirectories(location.Path, "*", options).OrderByDescending(d => d.Length).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (settings.IsExcluded(folder) || PathSafety.HasReparsePointOnPath(location.Path, Path.Combine(folder, "_"))) continue;
            try
            {
                // Creation time: deleting the contents just refreshed the folder's write time.
                if (location.ApplyAgeFilter && Directory.GetCreationTimeUtc(folder) > cutoff) continue;
                if (Directory.EnumerateFileSystemEntries(folder).Any()) continue;
                Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
