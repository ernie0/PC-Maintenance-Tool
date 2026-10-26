using System.Diagnostics;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Processes;

namespace PcMaintenance.Core.Performance;

/// <summary>Share of all processors each process used since the previous update, as Task Manager shows it (100% = every core busy).</summary>
public sealed class ProcessCpuTracker(int? processorCount = null)
{
    private readonly int _processors = processorCount ?? Environment.ProcessorCount;
    private Dictionary<(int, long), long> _previous = [];
    private long _previousTimestamp;

    public IReadOnlyDictionary<(int Id, long CreateTime), double> Update(IEnumerable<ProcessSample> samples, long timestamp)
    {
        // Process times are in 100 ns units.
        var capacity = _previousTimestamp == 0 ? 0 : (timestamp - _previousTimestamp) * 10_000_000.0 / Stopwatch.Frequency * _processors;
        var usage = new Dictionary<(int, long), double>();
        var current = new Dictionary<(int, long), long>();
        foreach (var sample in samples)
        {
            current[sample.Key] = sample.CpuTime;
            usage[sample.Key] = capacity > 0 && _previous.TryGetValue(sample.Key, out var before)
                ? Math.Clamp(100.0 * (sample.CpuTime - before) / capacity, 0, 100)
                : 0;
        }
        _previous = current;
        _previousTimestamp = timestamp;
        return usage;
    }
}

/// <summary>Whole-machine processor usage from the idle, kernel and user times Windows keeps.</summary>
public sealed class SystemCpuTracker
{
    private (long Idle, long Kernel, long User)? _previous;

    /// <summary>Null on the first call, which only records the starting point.</summary>
    public double? Update()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var previous = _previous;
        _previous = (idle, kernel, user);
        return previous is { } before ? Usage(before, (idle, kernel, user)) : null;
    }

    /// <summary>Kernel time includes idle time, so busy time is kernel plus user minus idle.</summary>
    internal static double Usage((long Idle, long Kernel, long User) before, (long Idle, long Kernel, long User) after)
    {
        var idle = after.Idle - before.Idle;
        var total = after.Kernel - before.Kernel + (after.User - before.User);
        return total <= 0 ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }
}
