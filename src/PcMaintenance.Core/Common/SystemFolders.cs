namespace PcMaintenance.Core.Common;

/// <summary>Well-known Windows folders. Injectable so tests can point every scanner at a sandbox.</summary>
public sealed record SystemFolders
{
    public required string WindowsDir { get; init; }
    public required string ProgramFiles { get; init; }
    public required string ProgramFilesX86 { get; init; }
    public required string CommonProgramFiles { get; init; }
    public required string CommonProgramFilesX86 { get; init; }
    public required string ProgramData { get; init; }
    public required string UserProfile { get; init; }
    public required string AppData { get; init; }
    public required string LocalAppData { get; init; }
    public required string UserTemp { get; init; }

    public static SystemFolders Current { get; } = new()
    {
        WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        CommonProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        CommonProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        UserTemp = Path.GetTempPath(),
    };

    /// <summary>Folders that must never be removed themselves, nor any of their ancestors.</summary>
    public IReadOnlyList<string> ProtectedRoots()
    {
        var roots = new List<string>
        {
            WindowsDir, ProgramFiles, ProgramFilesX86, CommonProgramFiles, CommonProgramFilesX86,
            ProgramData, UserProfile, AppData, LocalAppData, UserTemp,
        };
        var drive = Path.GetPathRoot(WindowsDir);
        if (!string.IsNullOrEmpty(drive)) roots.Add(drive);
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
    }
}
