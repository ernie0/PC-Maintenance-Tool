using PcMaintenance.Core.Processes;

namespace PcMaintenance.Core.Memory;

/// <summary>All processes of one program (for example every Chrome process) added together.</summary>
public sealed record ProgramMemory(string ImageName, string DisplayName, int Processes, long PrivateWorkingSet);

public static class MemoryUsage
{
    /// <summary>Groups processes by program and names each after its file description when that can be read.</summary>
    public static IReadOnlyList<ProgramMemory> TopPrograms(IEnumerable<ProcessSample> processes, int count, Func<int, string?>? describe = null)
    {
        describe ??= id => ProcessSnapshot.Describe(ProcessSnapshot.ExecutablePath(id));
        return processes
            .Where(p => p.Id > 4 && p.ImageName.Length > 0)
            .GroupBy(p => p.ImageName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Group: g, Bytes: g.Sum(p => p.PrivateWorkingSet)))
            .OrderByDescending(x => x.Bytes)
            .Take(count)
            .Select(x => new ProgramMemory(x.Group.Key, DisplayName(x.Group, describe), x.Group.Count(), x.Bytes))
            .ToList();
    }

    private static string DisplayName(IEnumerable<ProcessSample> group, Func<int, string?> describe)
    {
        foreach (var process in group.Take(3))
            if (describe(process.Id) is { Length: > 0 } description) return description;
        return Path.GetFileNameWithoutExtension(group.First().ImageName);
    }
}
