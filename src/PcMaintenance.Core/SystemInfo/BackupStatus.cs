using System.Security;
using Microsoft.Win32;

namespace PcMaintenance.Core.SystemInfo;

/// <summary>
/// Signs that this user's files are backed up somewhere: Desktop, Documents or Pictures moved into OneDrive (OneDrive's
/// folder backup, which Windows Backup also uses), File History set up, or backup and sync software installed. These
/// say a backup is configured, not that it last ran fine.
/// </summary>
/// <param name="Read">False for the placeholder used before anything was read.</param>
public sealed record BackupStatus(bool Read, IReadOnlyList<string> OneDriveFolders, bool FileHistory, IReadOnlyList<string> BackupSoftware)
{
    public static readonly BackupStatus Unknown = new(false, [], false, []);

    public bool AnyFound => OneDriveFolders.Count > 0 || FileHistory || BackupSoftware.Count > 0;

    public const string NothingFound = "沒有偵測到";

    /// <summary>One line for the CSV and the multi-computer page.</summary>
    public string Summary => !Read ? "" : !AnyFound ? NothingFound : string.Join("；", new[]
    {
        OneDriveFolders.Count > 0 ? $"OneDrive 備份：{string.Join("、", OneDriveFolders)}" : "",
        FileHistory ? "檔案歷程記錄" : "",
        BackupSoftware.Count > 0 ? $"備份或同步軟體：{string.Join("、", BackupSoftware)}" : "",
    }.Where(s => s.Length > 0));

    // Backup and file sync products as they name themselves in "Installed apps"; matched as a whole word or phrase.
    private static readonly string[] Products =
    [
        "Acronis", "Veeam", "Active Backup", "Synology Drive", "Macrium", "EaseUS Todo Backup", "AOMEI Backupper", "Google Drive",
        "Dropbox", "Backblaze", "Carbonite", "Cobian", "Iperius", "Duplicati", "Qsync", "NetBackup", "Arcserve", "Commvault",
        "Paragon Backup", "SyncBack", "FreeFileSync", "Box Drive", "MEGAsync", "MEGA Desktop", "pCloud", "Nextcloud", "ownCloud",
        "Seafile", "Cyber Backup", "UrBackup", "Bvckup", "Genie Timeline", "Rubrik", "Druva", "CrashPlan", "IDrive",
    ];

    public static BackupStatus ReadCurrent(IEnumerable<string> installedApps) =>
        Evaluate(OneDriveRoots(), KnownFolders(), FileHistoryConfigured(), installedApps);

    public static BackupStatus Evaluate(
        IReadOnlyList<string> oneDriveRoots, IReadOnlyList<(string Name, string Path)> folders, bool fileHistory, IEnumerable<string> installedApps)
    {
        var inOneDrive = folders
            .Where(f => f.Path.Length > 0 && oneDriveRoots.Any(root => IsUnder(root, f.Path)))
            .Select(f => f.Name)
            .ToList();
        var software = installedApps
            .SelectMany(app => Products.Where(p => app.Contains(p, StringComparison.OrdinalIgnoreCase)).Select(_ => app))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.CurrentCulture)
            .ToList();
        return new BackupStatus(true, inOneDrive, fileHistory, software);
    }

    private static bool IsUnder(string root, string path)
    {
        var r = root.TrimEnd('\\') + "\\";
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<(string, string)> KnownFolders() =>
    [
        ("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        ("文件", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        ("圖片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
    ];

    /// <summary>The local folder of every OneDrive account signed in for this user (personal and work or school).</summary>
    private static IReadOnlyList<string> OneDriveRoots()
    {
        var roots = new List<string>();
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var name in accounts?.GetSubKeyNames() ?? [])
            {
                using var account = accounts!.OpenSubKey(name);
                if (account?.GetValue("UserFolder") is string { Length: > 3 } folder) roots.Add(folder);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>File History keeps its per-user setup in Config1.xml/Config2.xml once a drive has been chosen.</summary>
    private static bool FileHistoryConfigured()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\FileHistory\Configuration");
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "Config*.xml").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
