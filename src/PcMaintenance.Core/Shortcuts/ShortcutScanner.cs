using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Shortcuts;

/// <summary>A folder whose shortcuts are checked. Removing shortcuts made for all users needs elevation.</summary>
public sealed record ShortcutFolder(string Path, string Name, bool Recursive, bool AllUsers);

public sealed record BrokenShortcut(string Path, string Target, ShortcutFolder Folder)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// Finds shortcuts whose target file or folder is gone. Only targets on local fixed disks are judged: a shortcut to a
/// network share, a removable drive or a drive letter that is absent right now may still work later, and a target that
/// cannot be inspected (no permission) is left alone too.
/// </summary>
public static class ShortcutScanner
{
    public static IReadOnlyList<ShortcutFolder> DefaultFolders() =>
    [
        new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面", false, false),
        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "公用桌面", false, true),
        new(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "開始功能表", true, false),
        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "開始功能表（所有使用者）", true, true),
        // Not recursive: its "User Pinned" subfolders belong to the taskbar, which keeps its own record of pins.
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch"),
            "快速啟動", false, false),
    ];

    public static IReadOnlyList<BrokenShortcut> Scan(IReadOnlyList<ShortcutFolder> folders, CancellationToken ct = default)
    {
        var results = new List<BrokenShortcut>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path)) continue;
            options.RecurseSubdirectories = folder.Recursive;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder.Path, "*.lnk", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (ShellLink.TryRead(file) is { } link && MissingTarget(link, PathProbe.State) is { } target)
                    results.Add(new BrokenShortcut(file, target, folder));
            }
        }
        return results;
    }

    /// <summary>The target that is gone, or null when the target exists, is not a file path, or cannot be judged.</summary>
    internal static string? MissingTarget(ShellLinkInfo link, Func<string, PathState> probe)
    {
        if (link.IsAdvertised) return null;
        var candidates = Candidates(link).Where(IsOnLocalFixedDisk).ToList();
        if (candidates.Count == 0) return null;
        foreach (var candidate in candidates)
            if (probe(candidate) != PathState.Missing) return null;
        return candidates[0];
    }

    /// <summary>
    /// The stored path is expanded in this 64-bit process, so "%ProgramFiles%" in a shortcut written by a 32-bit
    /// installer can come out as the other Program Files folder; both are tried before calling the target missing.
    /// </summary>
    private static IEnumerable<string> Candidates(ShellLinkInfo link)
    {
        var paths = new List<string>();
        void Add(string path)
        {
            if (path.Length > 0 && !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        }
        Add(link.Path);
        Add(Environment.ExpandEnvironmentVariables(link.RawPath));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var path in paths.ToList())
        {
            if (PathSafety.IsSameOrUnder(programFilesX86, path)) Add(programFiles + path[programFilesX86.Length..]);
            else if (PathSafety.IsSameOrUnder(programFiles, path)) Add(programFilesX86 + path[programFiles.Length..]);
        }
        return paths;
    }

    private static bool IsOnLocalFixedDisk(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        try
        {
            var drive = new DriveInfo(path[..1]);
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed record ShortcutRemoval(BrokenShortcut Item, bool Success, string Message);

public sealed class ShortcutRemover(ChangeJournal journal, FileLog? log = null)
{
    public ShortcutRemoval Remove(BrokenShortcut item)
    {
        if (!File.Exists(item.Path)) return new ShortcutRemoval(item, true, "捷徑已不存在");
        // The scan may be stale (a drive came back, the program was reinstalled); look again right before acting.
        if (ShellLink.TryRead(item.Path) is not { } link || ShortcutScanner.MissingTarget(link, PathProbe.State) is null)
            return new ShortcutRemoval(item, false, "目標已經存在，未移除");
        if (!RecycleBin.Send(item.Path))
            return new ShortcutRemoval(item, false, item.Folder.AllUsers ? "無法移到資源回收筒（需要系統管理員權限）" : "無法移到資源回收筒");
        journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.ShortcutRecycled,
            Target = item.Path,
            Description = $"移除無效捷徑「{item.Name}」（已移到資源回收筒）",
            RestoreData = new Dictionary<string, string> { ["path"] = item.Path, ["target"] = item.Target },
        });
        log?.Info($"無效捷徑移到資源回收筒：{item.Path}（目標 {item.Target}）");
        return new ShortcutRemoval(item, true, "已移到資源回收筒");
    }
}
