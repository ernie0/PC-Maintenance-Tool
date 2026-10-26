using System.Security;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.DiskAnalysis;

/// <summary>A folder with no files at any depth; <see cref="Subfolders"/> empty folders inside it go along with it.</summary>
public sealed record EmptyFolder(string Path, int Subfolders);

/// <summary>
/// Finds folders that hold no files at any depth and reports only the outermost of each, never the search root itself.
/// Junctions, hidden and system folders, folders named with a leading dot (".cache", ".android": program settings by
/// convention), cloud placeholders and Windows' own folders count as content and are never entered: programs often
/// expect their empty folders there to exist.
/// </summary>
public static class EmptyFolderFinder
{
    // Hidden and system entries count too (a folder holding only desktop.ini is not empty), and a folder that cannot
    // be listed must raise instead of looking empty.
    private static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    public static IReadOnlyList<EmptyFolder> Find(string root, SystemFolders folders, CancellationToken ct = default)
    {
        var results = new List<EmptyFolder>();
        var leaveAlone = new[] { folders.WindowsDir, folders.ProgramFiles, folders.ProgramFilesX86, folders.ProgramData };
        foreach (var child in Subdirectories(PathSafety.Normalize(root)))
        {
            if (IsLeftAlone(child, leaveAlone)) continue;
            if (CountIfEmpty(child, leaveAlone, results, ct) is { } count) results.Add(new EmptyFolder(child.FullName, count - 1));
        }
        return results.OrderBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>True when the folder is still empty, so a stale search result is not acted on.</summary>
    public static bool IsStillEmpty(string path, SystemFolders folders) =>
        Directory.Exists(path) && !PathSafety.IsReparsePoint(path) &&
        CountIfEmpty(new DirectoryInfo(path), [folders.WindowsDir, folders.ProgramFiles, folders.ProgramFilesX86, folders.ProgramData], [], CancellationToken.None) is not null;

    /// <summary>
    /// The number of folders in this one's tree (itself included) when it holds no files at all, otherwise null; in that
    /// case its own empty subfolders are the outermost ones and go into <paramref name="results"/>.
    /// </summary>
    private static int? CountIfEmpty(DirectoryInfo dir, string[] leaveAlone, List<EmptyFolder> results, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos("*", Everything).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Unreadable: treat it as holding something.
            return null;
        }

        var isEmpty = true;
        var emptyChildren = new List<(string Path, int Count)>();
        foreach (var entry in entries)
        {
            if (entry is not DirectoryInfo sub || IsLeftAlone(sub, leaveAlone))
            {
                isEmpty = false;
                continue;
            }
            if (CountIfEmpty(sub, leaveAlone, results, ct) is { } count) emptyChildren.Add((sub.FullName, count));
            else isEmpty = false;
        }
        if (isEmpty) return 1 + emptyChildren.Sum(c => c.Count);
        results.AddRange(emptyChildren.Select(c => new EmptyFolder(c.Path, c.Count - 1)));
        return null;
    }

    private static bool IsLeftAlone(DirectoryInfo dir, string[] leaveAlone)
    {
        const FileAttributes skipped = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
        if (dir.Name.StartsWith('.')) return true;
        try
        {
            if ((dir.Attributes & skipped) != 0 || PathSafety.IsCloudPlaceholder(dir.Attributes)) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        return leaveAlone.Any(root => !string.IsNullOrEmpty(root) && PathSafety.IsSameOrUnder(root, dir.FullName));
    }

    private static IEnumerable<DirectoryInfo> Subdirectories(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories("*", Everything).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }
}

public sealed record EmptyFolderRemoval(EmptyFolder Item, bool Success, string Message);

public sealed class EmptyFolderRemover(ChangeJournal journal, SystemFolders folders, FileLog? log = null)
{
    public EmptyFolderRemoval Remove(EmptyFolder item)
    {
        if (!Directory.Exists(item.Path)) return new EmptyFolderRemoval(item, true, "資料夾已不存在");
        if (!EmptyFolderFinder.IsStillEmpty(item.Path, folders)) return new EmptyFolderRemoval(item, false, "資料夾裡已經有檔案，未移除");
        if (!RecycleBin.Send(item.Path)) return new EmptyFolderRemoval(item, false, "無法移到資源回收筒");
        journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.FolderRecycled,
            Target = item.Path,
            Description = "移除空資料夾（已移到資源回收筒）",
            RestoreData = new Dictionary<string, string> { ["path"] = item.Path },
        });
        log?.Info($"空資料夾移到資源回收筒：{item.Path}");
        return new EmptyFolderRemoval(item, true, "已移到資源回收筒");
    }
}
