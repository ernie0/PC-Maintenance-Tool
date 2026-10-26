using System.IO.Enumeration;
using System.Security.Cryptography;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.DiskAnalysis;

public sealed record DuplicateFile(string Path, long Size, DateTime LastWriteUtc);

public sealed record DuplicateGroup(long Size, string Hash, IReadOnlyList<DuplicateFile> Files)
{
    public long WastedBytes => Size * (Files.Count - 1);
}

public sealed record DuplicateProgress(string Stage, long Done, long Total);

/// <summary>
/// Finds byte-identical files: group by size, then by a SHA-256 of the first 64 KB, then by a full
/// SHA-256. Cloud placeholders are skipped because reading them would download them.
/// </summary>
public sealed class DuplicateFinder(AppSettings settings)
{
    private const int PrefixBytes = 64 * 1024;

    public IReadOnlyList<DuplicateGroup> Find(IReadOnlyList<string> roots, long minSize, IProgress<DuplicateProgress>? progress = null, CancellationToken ct = default)
    {
        minSize = Math.Max(minSize, 1);
        var bySize = new Dictionary<long, List<DuplicateFile>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots) Collect(root, minSize, bySize, seen, progress, ct);

        var candidates = bySize.Values.Where(g => g.Count > 1).ToList();
        long total = candidates.Sum(g => g.Count);
        long done = 0;
        var groups = new List<DuplicateGroup>();
        foreach (var sameSize in candidates)
        {
            var byPrefix = new Dictionary<string, List<DuplicateFile>>();
            foreach (var file in sameSize)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new DuplicateProgress("比對檔案內容", ++done, total));
                if (TryHash(file.Path, PrefixBytes) is { } prefix) AddTo(byPrefix, prefix, file);
            }
            foreach (var (prefix, prefixGroup) in byPrefix)
            {
                if (prefixGroup.Count < 2) continue;
                if (prefixGroup[0].Size <= PrefixBytes)
                {
                    groups.Add(new DuplicateGroup(prefixGroup[0].Size, prefix, prefixGroup));
                    continue;
                }
                var byFull = new Dictionary<string, List<DuplicateFile>>();
                foreach (var file in prefixGroup)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryHash(file.Path, null) is { } full) AddTo(byFull, full, file);
                }
                groups.AddRange(byFull.Where(kv => kv.Value.Count > 1).Select(kv => new DuplicateGroup(kv.Value[0].Size, kv.Key, kv.Value)));
            }
        }
        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private void Collect(string root, long minSize, Dictionary<long, List<DuplicateFile>> bySize, HashSet<string> seen, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            var entries = new FileSystemEnumerable<DuplicateFile?>(root,
                (ref FileSystemEntry e) => PathSafety.IsCloudPlaceholder(e.Attributes) || e.Length < minSize
                    ? null
                    : new DuplicateFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            };
            long found = 0;
            foreach (var file in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (file is null || settings.IsExcluded(file.Path) || !seen.Add(file.Path)) continue;
                AddTo(bySize, file.Size, file);
                if (++found % 2000 == 0) progress?.Report(new DuplicateProgress("搜尋檔案", found, 0));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? TryHash(string path, int? limit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            if (limit is null) return Convert.ToHexString(SHA256.HashData(stream));
            var buffer = new byte[limit.Value];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddTo<TKey>(Dictionary<TKey, List<DuplicateFile>> map, TKey key, DuplicateFile file) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(file);
    }
}

public sealed record DuplicateRemovalResult(int Recycled, long FreedBytes, IReadOnlyList<string> Failed);

public static class DuplicateRemoval
{
    /// <summary>
    /// Refuses any selection that would leave a group without an intact copy. A copy that was removed or changed
    /// after the search, for example in an earlier round, no longer counts as the one that is kept.
    /// </summary>
    public static void EnsureOneKept(IEnumerable<DuplicateGroup> groups, IReadOnlySet<string> selectedPaths)
    {
        foreach (var group in groups)
        {
            var selected = group.Files.FirstOrDefault(f => selectedPaths.Contains(f.Path));
            if (selected is not null && !group.Files.Any(f => !selectedPaths.Contains(f.Path) && IsUnchanged(f)))
                throw new InvalidOperationException($"每組重複檔至少要保留一個：{selected.Path}");
        }
    }

    private static bool IsUnchanged(DuplicateFile file)
    {
        var info = new FileInfo(file.Path);
        return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc == file.LastWriteUtc;
    }

    public static DuplicateRemovalResult Recycle(IReadOnlyList<DuplicateGroup> groups, IReadOnlySet<string> selectedPaths, AppSettings settings, FileLog? log = null)
    {
        EnsureOneKept(groups, selectedPaths);
        var recycled = 0;
        long freed = 0;
        var failed = new List<string>();
        foreach (var file in groups.SelectMany(g => g.Files).Where(f => selectedPaths.Contains(f.Path)))
        {
            if (!File.Exists(file.Path)) continue;
            // Unchanged since hashing, otherwise the copy may no longer be a duplicate.
            if (settings.IsExcluded(file.Path) || !IsUnchanged(file) || !RecycleBin.Send(file.Path))
            {
                failed.Add(file.Path);
                continue;
            }
            recycled++;
            freed += file.Size;
        }
        log?.Info($"重複檔移到資源回收筒：{recycled} 個（{ByteSize.Format(freed)}），失敗 {failed.Count} 個");
        return new DuplicateRemovalResult(recycled, freed, failed);
    }
}
