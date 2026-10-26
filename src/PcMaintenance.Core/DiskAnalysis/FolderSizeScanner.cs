using System.Diagnostics;
using System.IO.Enumeration;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.DiskAnalysis;

public sealed class FolderNode
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public FolderNode? Parent { get; init; }
    public long Size { get; internal set; }
    public long FileCount { get; internal set; }
    public bool AccessDenied { get; internal set; }
    public List<FolderNode> Children { get; } = [];
}

public sealed record LargeFile(string Path, long Size, DateTime LastWriteUtc);

public sealed record ScanProgress(string CurrentPath, long Files, long Bytes);

public sealed record FolderScanResult(
    FolderNode Root, IReadOnlyList<LargeFile> LargestFiles, IReadOnlyList<FileKindTotal> Kinds, long TotalFiles, int AccessDeniedFolders, TimeSpan Elapsed);

/// <summary>
/// Sizes every folder under a root. Junctions and symbolic links are not followed, so nothing is
/// counted twice and link loops cannot hang the scan. Cloud placeholders count as zero local bytes.
/// </summary>
public sealed class FolderSizeScanner(int largestFileCount = 100)
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public FolderScanResult Scan(string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var root = new FolderNode { Path = PathSafety.Normalize(rootPath), Name = PathSafety.Normalize(rootPath) };
        var visited = new List<FolderNode>();
        var pending = new Stack<FolderNode>();
        pending.Push(root);
        var largest = new PriorityQueue<LargeFile, long>();
        var kindBytes = new long[Enum.GetValues<FileKind>().Length];
        var kindFiles = new long[kindBytes.Length];
        long totalFiles = 0, totalBytes = 0;
        var denied = 0;
        var lastReport = TimeSpan.Zero;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
        };

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = pending.Pop();
            visited.Add(node);
            try
            {
                var entries = new FileSystemEnumerable<Entry>(node.Path,
                    (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.Length, e.IsDirectory, e.Attributes, e.LastWriteTimeUtc.UtcDateTime),
                    options);
                foreach (var entry in entries)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var fullPath = System.IO.Path.Combine(node.Path, entry.Name);
                    if (entry.IsDirectory)
                    {
                        var child = new FolderNode { Path = fullPath, Name = entry.Name, Parent = node };
                        node.Children.Add(child);
                        pending.Push(child);
                        continue;
                    }
                    var size = PathSafety.IsCloudPlaceholder(entry.Attributes) ? 0 : entry.Length;
                    node.Size += size;
                    node.FileCount++;
                    totalFiles++;
                    totalBytes += size;
                    var kind = (int)FileKinds.Classify(entry.Name);
                    kindBytes[kind] += size;
                    kindFiles[kind]++;
                    KeepLargest(largest, new LargeFile(fullPath, size, entry.LastWriteUtc), size);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                node.AccessDenied = true;
                denied++;
            }

            if (progress is not null && stopwatch.Elapsed - lastReport > ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                progress.Report(new ScanProgress(node.Path, totalFiles, totalBytes));
            }
        }

        // Children are always visited after their parent, so walking backwards finalizes every
        // child before it is added to its parent.
        for (var i = visited.Count - 1; i > 0; i--)
        {
            var node = visited[i];
            node.Parent!.Size += node.Size;
            node.Parent.FileCount += node.FileCount;
        }
        foreach (var node in visited) node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));

        var files = new List<LargeFile>(largest.Count);
        while (largest.TryDequeue(out var file, out _)) files.Add(file);
        files.Reverse();
        var kinds = Enum.GetValues<FileKind>()
            .Where(k => kindFiles[(int)k] > 0)
            .Select(k => new FileKindTotal(k, kindBytes[(int)k], kindFiles[(int)k]))
            .OrderByDescending(k => k.Bytes)
            .ToList();
        return new FolderScanResult(root, files, kinds, totalFiles, denied, stopwatch.Elapsed);
    }

    private void KeepLargest(PriorityQueue<LargeFile, long> largest, LargeFile file, long size)
    {
        if (size <= 0 || largestFileCount <= 0) return;
        if (largest.Count < largestFileCount)
        {
            largest.Enqueue(file, size);
            return;
        }
        if (largest.TryPeek(out _, out var smallest) && size > smallest) largest.EnqueueDequeue(file, size);
    }

    private readonly record struct Entry(string Name, long Length, bool IsDirectory, FileAttributes Attributes, DateTime LastWriteUtc);
}
