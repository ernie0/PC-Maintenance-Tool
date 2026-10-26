namespace PcMaintenance.Core.Junk;

/// <summary>
/// A folder whose files are all disposable, or, when <see cref="FileName"/> is set, only the files directly inside
/// <see cref="Path"/> with that name (* and ? allowed); such a location never touches the rest of that folder.
/// </summary>
public sealed record JunkLocation(string Path, bool ApplyAgeFilter = true, string? FileName = null);

public sealed record JunkCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool DefaultSelected { get; init; } = true;
    public bool IsRecycleBin { get; init; }
    public IReadOnlyList<JunkLocation> Locations { get; init; } = [];
    /// <summary>Process names (without .exe) that keep these files in use; cleaning waits until none run.</summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = [];
    public string? BlockingAppName { get; init; }
}

public sealed record JunkFile(string Path, long Size, DateTime LastWriteUtc, string Root);

/// <summary>How far a scan of one category has got: files looked at so far, and how many of them can be cleaned.</summary>
public readonly record struct JunkScanProgress(int FilesRead, int FilesFound);

public sealed record JunkScanResult
{
    public required JunkCategory Category { get; init; }
    public IReadOnlyList<JunkFile> Files { get; init; } = [];
    public long TotalBytes { get; init; }
    public long ItemCount { get; init; }
    public string? Warning { get; init; }
    public bool CanClean { get; init; } = true;
}

public sealed record CleanResult
{
    public int Deleted { get; init; }
    public long FreedBytes { get; init; }
    public int SkippedInUse { get; init; }
    public int SkippedChanged { get; init; }
    public int SkippedDenied { get; init; }
    public int SkippedUnsafe { get; init; }
    public string? Note { get; init; }
    public int TotalSkipped => SkippedInUse + SkippedChanged + SkippedDenied + SkippedUnsafe;
}
