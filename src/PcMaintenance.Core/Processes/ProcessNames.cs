using System.Collections.Concurrent;

namespace PcMaintenance.Core.Processes;

/// <summary>
/// The name people know a process by (its file description, e.g. "Google Chrome", else the image name) and its
/// path, cached per process instance because both need opening the process or its file. Shared by the process and
/// network tabs, which sample on different threads.
/// </summary>
public sealed class ProcessNames
{
    private readonly ConcurrentDictionary<(int, long), (string Name, string? Path)> _identities = new();
    private readonly ConcurrentDictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);

    public (string Name, string? Path) Get(ProcessSample process) => _identities.GetOrAdd(process.Key, _ => Identify(process));

    public bool TryGet((int Id, long CreateTime) key, out (string Name, string? Path) identity) => _identities.TryGetValue(key, out identity);

    /// <summary>Drops processes that are gone, so a reused id is never shown with the old program's name.</summary>
    public void KeepOnly(IReadOnlyCollection<ProcessSample> alive)
    {
        var keys = alive.Select(p => p.Key).ToHashSet();
        foreach (var gone in _identities.Keys.Where(k => !keys.Contains(k)).ToList()) _identities.TryRemove(gone, out _);
    }

    private (string Name, string? Path) Identify(ProcessSample process)
    {
        var path = ProcessSnapshot.ExecutablePath(process.Id);
        var description = path is null ? null : _descriptions.GetOrAdd(path, ProcessSnapshot.Describe);
        return (description ?? Path.GetFileNameWithoutExtension(process.ImageName), path);
    }
}
