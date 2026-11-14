using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Startup;

/// <summary>
/// Signs that a start-up entry deserves a second look: it runs from a temporary, download or public folder, it has no
/// valid digital signature, or it calls itself Microsoft without Microsoft's signature. None of these proves harm, so the
/// page only marks the entry and never disables it on its own.
/// </summary>
public static class StartupRisk
{
    public static string? Describe(StartupItem item, FileSignature? signature, IReadOnlyList<(string Folder, string Name)> riskyFolders, string? windowsFolder = null)
    {
        var notes = new List<string>();
        if (item.ExecutablePath is { Length: > 0 } path)
        {
            if (riskyFolders.FirstOrDefault(f => PathSafety.IsSameOrUnder(f.Folder, path)) is { Name: { } folder })
                notes.Add($"從{folder}執行");
            // Windows' own files are signed through catalogs that a damaged or partial catalog database cannot find, and
            // only an administrator can place a file there; so inside the Windows folder only a foreign signer counts.
            windowsFolder ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var inWindows = windowsFolder.Length > 0 && PathSafety.IsSameOrUnder(windowsFolder, path);
            switch (signature?.State)
            {
                case SignatureState.Valid when item.IsMicrosoft && !signature.IsMicrosoft:
                    notes.Add($"標示為 Microsoft，但數位簽章是「{signature.Signer}」");
                    break;
                case SignatureState.Unsigned or SignatureState.Invalid when item.IsMicrosoft && inWindows:
                    break;
                case SignatureState.Unsigned or SignatureState.Invalid when item.IsMicrosoft:
                    notes.Add("標示為 Microsoft，但沒有有效的 Microsoft 數位簽章");
                    break;
                case SignatureState.Invalid:
                    notes.Add("數位簽章無效");
                    break;
                case SignatureState.Unsigned:
                    notes.Add("沒有數位簽章");
                    break;
            }
        }
        return notes.Count > 0 ? string.Join("；", notes) : null;
    }

    /// <summary>Folders programs are not normally installed in: where downloads, e-mail attachments and droppers land.</summary>
    public static IReadOnlyList<(string Folder, string Name)> RiskyFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = new List<(string, string)>
        {
            (Path.GetTempPath(), "暫存資料夾"),
            (Path.Combine(windows, "Temp"), "暫存資料夾"),
            (Path.Combine(profile, "Downloads"), "下載資料夾"),
            (Environment.GetEnvironmentVariable("PUBLIC") ?? Path.Combine(Path.GetDirectoryName(profile) ?? "C:\\Users", "Public"), "公用資料夾"),
        };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            list.Add((Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin"), "資源回收筒"));
        return list.Where(f => f.Item1.Length > 3).ToList();
    }
}
