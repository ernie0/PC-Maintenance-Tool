using System.Globalization;
using PcMaintenance.Core.Memory;

namespace PcMaintenance.Core.Performance;

public sealed record CpuSample(double Usage, double? SpeedMHz);

public sealed record MemorySample(MemoryStatus Status, long? CachedBytes, long? PagedPoolBytes, long? NonPagedPoolBytes);

/// <summary>One physical disk; <see cref="Letters"/> come from Windows' instance name, such as "C: E:".</summary>
public sealed record DiskSample(int DiskNumber, string Letters, double ActivePercent, double ReadBytesPerSecond, double WriteBytesPerSecond, double? ResponseMs);

/// <summary>One graphics adapter, identified by the LUID its performance counters use.</summary>
public sealed record GpuSample(string Luid, double Utilization, long DedicatedUsed, long SharedUsed);

public sealed record PerformanceSnapshot(CpuSample Cpu, MemorySample Memory, IReadOnlyList<DiskSample> Disks, IReadOnlyList<NetworkActivity> Network, IReadOnlyList<GpuSample> Gpus);

/// <summary>
/// Everything the performance tab shows, from the same counters Task Manager reads. All of them are readable
/// without elevation; a counter missing on this computer (old GPU drivers) simply leaves its part empty.
/// </summary>
public sealed class PerformanceSampler : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly SystemCpuTracker _cpu = new();
    private readonly NetworkActivitySampler _network = new();
    private readonly int? _baseMHz;
    private readonly PdhCounter? _processorPerformance;
    private readonly PdhCounter?[] _cached;
    private readonly PdhCounter? _pagedPool, _nonPagedPool;
    private readonly PdhCounter? _diskIdle, _diskRead, _diskWrite, _diskResponse;
    private readonly PdhCounter? _gpuEngine, _gpuDedicated, _gpuShared;

    public PerformanceSampler(int? baseMHz)
    {
        _baseMHz = baseMHz;
        _processorPerformance = _query.Add(@"\Processor Information(_Total)\% Processor Performance");
        // What Task Manager calls "cached": the standby lists plus modified pages.
        _cached =
        [
            _query.Add(@"\Memory\Standby Cache Reserve Bytes"),
            _query.Add(@"\Memory\Standby Cache Normal Priority Bytes"),
            _query.Add(@"\Memory\Standby Cache Core Bytes"),
            _query.Add(@"\Memory\Modified Page List Bytes"),
        ];
        _pagedPool = _query.Add(@"\Memory\Pool Paged Bytes");
        _nonPagedPool = _query.Add(@"\Memory\Pool Nonpaged Bytes");
        _diskIdle = _query.Add(@"\PhysicalDisk(*)\% Idle Time");
        _diskRead = _query.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = _query.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _diskResponse = _query.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        _gpuEngine = _query.Add(@"\GPU Engine(*)\Utilization Percentage");
        _gpuDedicated = _query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _gpuShared = _query.Add(@"\GPU Adapter Memory(*)\Shared Usage");
        _query.Collect();
        _cpu.Update();
    }

    public PerformanceSnapshot Sample(long timestamp)
    {
        _query.Collect();
        var usage = _cpu.Update() ?? 0;
        double? speed = _baseMHz is { } mhz && _processorPerformance?.Read() is { } percent ? mhz * percent / 100 : null;
        var cachedParts = _cached.Select(c => c?.Read()).ToList();
        long? cached = cachedParts.Any(v => v is not null) ? (long)cachedParts.Sum(v => v ?? 0) : null;
        var memory = new MemorySample(MemoryInfo.Read(), cached, (long?)_pagedPool?.Read(), (long?)_nonPagedPool?.Read());
        return new PerformanceSnapshot(new CpuSample(usage, speed), memory, Disks(), _network.Sample(timestamp), Gpus());
    }

    private List<DiskSample> Disks()
    {
        var idle = _diskIdle?.ReadAll() ?? [];
        var read = _diskRead?.ReadAll() ?? [];
        var write = _diskWrite?.ReadAll() ?? [];
        var response = _diskResponse?.ReadAll() ?? [];
        return idle
            .Select(kv => (Parsed: ParseDiskInstance(kv.Key), kv.Key, Idle: kv.Value))
            .Where(x => x.Parsed is not null)
            .Select(x => new DiskSample(x.Parsed!.Value.Number, x.Parsed.Value.Letters, Math.Clamp(100 - x.Idle, 0, 100),
                read.GetValueOrDefault(x.Key), write.GetValueOrDefault(x.Key),
                response.TryGetValue(x.Key, out var seconds) ? seconds * 1000 : null))
            .OrderBy(d => d.DiskNumber)
            .ToList();
    }

    private List<GpuSample> Gpus()
    {
        var utilization = GpuUtilization(_gpuEngine?.ReadAll() ?? []);
        var dedicated = ByLuid(_gpuDedicated?.ReadAll() ?? []);
        var shared = ByLuid(_gpuShared?.ReadAll() ?? []);
        return dedicated.Keys.Union(utilization.Keys)
            .Select(luid => new GpuSample(luid, utilization.GetValueOrDefault(luid), (long)dedicated.GetValueOrDefault(luid), (long)shared.GetValueOrDefault(luid)))
            .ToList();
    }

    /// <summary>"1 C: E:" becomes disk 1 with letters "C: E:"; "_Total" is not a disk.</summary>
    internal static (int Number, string Letters)? ParseDiskInstance(string instance)
    {
        var parts = instance.Split(' ', 2);
        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (number, parts.Length > 1 ? parts[1].Trim() : "")
            : null;
    }

    /// <summary>The adapter id inside names like "pid_1234_luid_0x00000000_0x0000D1A3_phys_0_eng_0_engtype_3D".</summary>
    internal static string? LuidOf(string instance)
    {
        const int length = 21;
        var at = instance.IndexOf("luid_", StringComparison.Ordinal);
        return at >= 0 && instance.Length >= at + 5 + length ? instance.Substring(at + 5, length) : null;
    }

    /// <summary>Like Task Manager: each engine type (3D, copy, video…) is added up over all processes and the busiest one counts.</summary>
    internal static Dictionary<string, double> GpuUtilization(IReadOnlyDictionary<string, double> engines) =>
        engines
            .Select(kv => (Luid: LuidOf(kv.Key), Type: EngineType(kv.Key), kv.Value))
            .Where(x => x.Luid is not null && x.Type is not null)
            .GroupBy(x => x.Luid!)
            .ToDictionary(g => g.Key, g => Math.Min(100, g.GroupBy(x => x.Type).Max(t => t.Sum(x => x.Value))));

    private static string? EngineType(string instance)
    {
        const string marker = "_engtype_";
        var at = instance.IndexOf(marker, StringComparison.Ordinal);
        return at >= 0 ? instance[(at + marker.Length)..] : null;
    }

    private static Dictionary<string, double> ByLuid(Dictionary<string, double> values) =>
        values
            .Select(kv => (Luid: LuidOf(kv.Key), kv.Value))
            .Where(x => x.Luid is not null)
            .GroupBy(x => x.Luid!)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Value));

    public void Dispose() => _query.Dispose();
}
