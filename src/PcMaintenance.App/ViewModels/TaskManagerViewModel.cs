using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.DiskTools;
using PcMaintenance.Core.Performance;
using PcMaintenance.Core.Processes;
using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.App.ViewModels;

public sealed partial class TaskManagerViewModel : PageViewModel
{
    private const int HistoryLength = 60;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly int _session;
    // Sampling state below is only touched by Collect, which never runs twice at once.
    private readonly ProcessCpuTracker _processCpu = new();
    private readonly ConcurrentDictionary<(int, long), (string Name, string? Path)> _identities = new();
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private PerformanceSampler? _performance;
    private ComputerInfo? _hardware;
    // UI state.
    private readonly Dictionary<(int, long), ProcessRow> _rows = [];
    private readonly Dictionary<string, PerfResource> _resources = [];
    private bool _sampling;

    public TaskManagerViewModel(AppServices services)
    {
        _services = services;
        using (var self = Process.GetCurrentProcess()) _session = self.SessionId;
        ProcessView = new ListCollectionView(Processes) { Filter = MatchesSearch };
        ProcessView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.MemoryBytes), ListSortDirection.Descending));
        ProcessView.IsLiveSorting = true;
        ProcessView.LiveSortingProperties.Add(nameof(ProcessRow.CpuPercent));
        ProcessView.LiveSortingProperties.Add(nameof(ProcessRow.MemoryBytes));
        _timer.Tick += async (_, _) => await SampleAsync();
    }

    public override string Title => "工作管理員";

    public override string Subtitle =>
        "即時查看執行中的程式與電腦效能，可結束沒有回應的程式。Windows 必要的處理程序與系統服務不提供結束，服務請到「開機與背景」管理。";

    public ObservableCollection<ProcessRow> Processes { get; } = [];
    public ListCollectionView ProcessView { get; }
    public ObservableCollection<PerfResource> Resources { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndProcessCommand), nameof(OpenLocationCommand))]
    [NotifyPropertyChangedFor(nameof(SelectionHint))]
    private ProcessRow? _selectedProcess;

    [ObservableProperty]
    private PerfResource? _selectedResource;

    [ObservableProperty]
    private string _summaryText = "正在讀取…";

    [ObservableProperty]
    private string _statusText = "";

    public string SelectionHint => SelectedProcess?.WhyNotEnd ?? "";

    partial void OnSearchTextChanged(string value) => ProcessView.Refresh();

    public override void OnNavigatedTo()
    {
        _timer.Start();
        _ = SampleAsync();
    }

    public override void OnNavigatedFrom() => _timer.Stop();

    [RelayCommand(CanExecute = nameof(CanEnd))]
    private async Task EndProcessAsync()
    {
        if (SelectedProcess is not { } row) return;
        if (!Dialogs.Confirm($"確定要結束「{row.Name}」嗎？\n\n程式會立即關閉，尚未儲存的資料會遺失。", "結束工作")) return;
        try
        {
            ProcessControl.End(row.Sample, _services.Log);
            StatusText = $"已結束「{row.Name}」。";
        }
        catch (ArgumentException)
        {
            StatusText = $"「{row.Name}」已經結束了。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Dialogs.Error($"無法結束「{row.Name}」：{UserMessage.For(ex)}");
        }
        await SampleAsync();
    }

    private bool CanEnd() => SelectedProcess is { WhyNotEnd: null };

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenLocation()
    {
        if (SelectedProcess?.Path is { } path) Shell.ShowInExplorer(path);
    }

    private bool CanOpenLocation() => SelectedProcess?.Path is not null;

    private bool MatchesSearch(object item) =>
        string.IsNullOrWhiteSpace(SearchText) || item is ProcessRow row && row.Matches(SearchText.Trim());

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            Apply(await Task.Run(Collect));
        }
        finally
        {
            _sampling = false;
        }
    }

    private Snapshot Collect()
    {
        var timestamp = Stopwatch.GetTimestamp();
        // Id 0 is the idle process: its "CPU" is the unused time.
        var processes = ProcessSnapshot.ReadAll().Where(p => p.Id != 0).ToList();
        var cpu = _processCpu.Update(processes, timestamp);
        var alive = processes.Select(p => p.Key).ToHashSet();
        foreach (var process in processes) _identities.GetOrAdd(process.Key, _ => Identify(process));
        foreach (var gone in _identities.Keys.Where(k => !alive.Contains(k)).ToList()) _identities.TryRemove(gone, out _);

        _hardware ??= SystemInfoReader.Read();
        _performance ??= new PerformanceSampler(_hardware.Processor.BaseMHz);
        var performance = _performance.Sample(timestamp);
        // A disk or network adapter that came and went needs its details read again.
        if (performance.Disks.Any(d => _hardware.Disks.Disks.All(x => x.Number != d.DiskNumber)) ||
            performance.Network.Any(n => _hardware.Network.All(a => a.Id != n.Id)))
            _hardware = _hardware with { Disks = DiskInventory.Read(includeReliability: false), Network = SystemInfoReader.ReadNetwork() };
        return new Snapshot(processes, cpu, performance, _hardware);
    }

    private (string Name, string? Path) Identify(ProcessSample process)
    {
        var path = ProcessSnapshot.ExecutablePath(process.Id);
        string? description = null;
        if (path is not null && !_descriptions.TryGetValue(path, out description))
            _descriptions[path] = description = ProcessSnapshot.Describe(path);
        return (description ?? Path.GetFileNameWithoutExtension(process.ImageName), path);
    }

    private void Apply(Snapshot snapshot)
    {
        var seen = new HashSet<(int, long)>();
        foreach (var process in snapshot.Processes)
        {
            seen.Add(process.Key);
            if (!_rows.TryGetValue(process.Key, out var row))
            {
                var (name, path) = _identities.TryGetValue(process.Key, out var identity) ? identity : (process.ImageName, null);
                row = new ProcessRow(process, name, path, ProcessControl.WhyNotEnd(process, _session, Environment.ProcessId));
                _rows[process.Key] = row;
                Processes.Add(row);
            }
            row.Update(process, snapshot.Cpu.GetValueOrDefault(process.Key));
        }
        foreach (var (key, row) in _rows.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            _rows.Remove(key);
            Processes.Remove(row);
        }

        var performance = snapshot.Performance;
        SummaryText = $"處理程序 {snapshot.Processes.Count:N0} 個　CPU {performance.Cpu.Usage:0}%　記憶體 {performance.Memory.Status.UsedPercent:0}%";
        UpdateResources(snapshot);
    }

    private void UpdateResources(Snapshot snapshot)
    {
        var performance = snapshot.Performance;
        var hardware = snapshot.Hardware;
        var shown = new HashSet<string>();
        PerfResource Use(string key, string kind, string title)
        {
            shown.Add(key);
            if (_resources.TryGetValue(key, out var resource)) return resource;
            resource = new PerfResource(kind, title, HistoryLength);
            _resources[key] = resource;
            Resources.Add(resource);
            return resource;
        }

        ShowCpu(Use("cpu", "cpu", "CPU"), performance.Cpu, hardware.Processor, snapshot.Processes);
        ShowMemory(Use("memory", "memory", "記憶體"), performance.Memory, hardware);
        foreach (var disk in performance.Disks)
        {
            var title = disk.Letters.Length > 0 ? $"磁碟 {disk.DiskNumber}（{disk.Letters}）" : $"磁碟 {disk.DiskNumber}";
            ShowDisk(Use($"disk:{disk.DiskNumber}", "disk", title), disk, hardware.Disks);
        }
        foreach (var adapter in performance.Network)
            ShowNetwork(Use($"net:{adapter.Id}", "network", adapter.Name), adapter, hardware.Network.FirstOrDefault(a => a.Id == adapter.Id));
        for (var i = 0; i < hardware.Gpus.Count; i++)
        {
            var gpu = hardware.Gpus[i];
            ShowGpu(Use($"gpu:{gpu.Luid}", "gpu", $"GPU {i}"), gpu, performance.Gpus.FirstOrDefault(g => g.Luid == gpu.Luid));
        }

        foreach (var key in _resources.Keys.Where(k => !shown.Contains(k)).ToList())
        {
            Resources.Remove(_resources[key]);
            _resources.Remove(key);
        }
        SelectedResource ??= Resources.FirstOrDefault();
    }

    private static void ShowCpu(PerfResource resource, CpuSample cpu, ProcessorInfo processor, IReadOnlyList<ProcessSample> processes)
    {
        var speed = cpu.SpeedMHz is { } mhz ? $"{mhz / 1000:0.00} GHz" : "—";
        resource.Push(cpu.Usage);
        resource.Subtitle = processor.Name;
        resource.Summary = cpu.SpeedMHz is null ? $"{cpu.Usage:0}%" : $"{cpu.Usage:0}%　{speed}";
        resource.Stats =
        [
            new("使用率", $"{cpu.Usage:0}%"),
            new("速度", speed),
            new("處理程序", $"{processes.Count:N0}"),
            new("執行緒", $"{processes.Sum(p => p.Threads):N0}"),
            new("控制代碼", $"{processes.Sum(p => (long)p.Handles):N0}"),
            new("已開機時間", Uptime()),
            new("基本速度", processor.BaseMHz is { } baseMhz ? $"{baseMhz / 1000.0:0.00} GHz" : "—"),
            new("插槽", processor.Sockets?.ToString() ?? "—"),
            new("核心", processor.Cores?.ToString() ?? "—"),
            new("邏輯處理器", $"{processor.LogicalProcessors}"),
            new("虛擬化", processor.Virtualization switch { true => "已啟用", false => "已停用", null => "—" }),
            new("L2 快取", processor.L2KB is { } l2 ? Kilobytes(l2) : "—"),
            new("L3 快取", processor.L3KB is { } l3 ? Kilobytes(l3) : "—"),
        ];
    }

    private static void ShowMemory(PerfResource resource, MemorySample memory, ComputerInfo hardware)
    {
        var status = memory.Status;
        var modules = hardware.MemoryModules;
        resource.Push(status.UsedPercent);
        resource.Subtitle = string.Join(" ", new[] { ByteSize.Format((long)status.InstalledPhysical), modules.Modules.FirstOrDefault()?.Type ?? "" }.Where(s => s.Length > 0));
        resource.Summary = $"{ByteSize.Format((long)status.UsedPhysical)}／{ByteSize.Format((long)status.TotalPhysical)}（{status.UsedPercent:0}%）";
        var speed = modules.Modules.Select(m => m.SpeedMHz).Where(s => s > 0).DefaultIfEmpty().Max();
        resource.Stats =
        [
            new("使用中", ByteSize.Format((long)status.UsedPhysical)),
            new("可用", ByteSize.Format((long)status.AvailablePhysical)),
            new("已認可", $"{ByteSize.Format((long)status.CommitUsed)}／{ByteSize.Format((long)status.CommitLimit)}"),
            new("已快取", Bytes(memory.CachedBytes)),
            new("分頁集區", Bytes(memory.PagedPoolBytes)),
            new("非分頁集區", Bytes(memory.NonPagedPoolBytes)),
            new("速度", speed > 0 ? $"{speed} MHz" : "—"),
            new("已使用的插槽", modules.SlotCount is { } slots ? $"{modules.Modules.Count}／{slots}" : $"{modules.Modules.Count}"),
            new("外型規格", modules.Modules.FirstOrDefault(m => m.FormFactor.Length > 0)?.FormFactor ?? "—"),
            new("硬體保留", ByteSize.Format((long)(status.InstalledPhysical - Math.Min(status.InstalledPhysical, status.TotalPhysical)))),
        ];
    }

    private static void ShowDisk(PerfResource resource, DiskSample disk, DiskInventoryResult inventory)
    {
        var info = inventory.Disks.FirstOrDefault(d => d.Number == disk.DiskNumber);
        var volumes = inventory.Volumes.Where(v => v.DiskNumber == disk.DiskNumber).ToList();
        var type = info is null ? "" : info.MediaType == "未知" ? info.BusType : $"{info.MediaType}（{info.BusType}）";
        resource.Push(disk.ActivePercent);
        resource.Subtitle = info?.Model ?? "";
        resource.Summary = type.Length > 0 ? $"{type}　{disk.ActivePercent:0}%" : $"{disk.ActivePercent:0}%";
        resource.Stats =
        [
            new("使用中時間", $"{disk.ActivePercent:0}%"),
            new("平均回應時間", disk.ResponseMs is { } ms ? $"{ms:0.0} 毫秒" : "—"),
            new("讀取速度", $"{ByteSize.Format((long)disk.ReadBytesPerSecond)}/秒"),
            new("寫入速度", $"{ByteSize.Format((long)disk.WriteBytesPerSecond)}/秒"),
            new("容量", info is null ? "—" : ByteSize.Format(info.SizeBytes)),
            new("已格式化", volumes.Count == 0 ? "—" : ByteSize.Format(volumes.Sum(v => v.TotalBytes))),
            new("系統磁碟", volumes.Any(v => v.IsSystem) ? "是" : "否"),
            new("分頁檔", volumes.Any(v => File.Exists(Path.Combine(v.Root, "pagefile.sys"))) ? "是" : "否"),
            new("類型", type.Length > 0 ? type : "—"),
        ];
    }

    private static void ShowNetwork(PerfResource resource, NetworkActivity activity, NetworkAdapterInfo? adapter)
    {
        resource.PushScaled(activity.SentBitsPerSecond + activity.ReceivedBitsPerSecond, 100_000, Bits);
        resource.Subtitle = activity.Description;
        resource.Summary = $"傳送 {Bits(activity.SentBitsPerSecond)}　接收 {Bits(activity.ReceivedBitsPerSecond)}";
        resource.Stats =
        [
            new("傳送", Bits(activity.SentBitsPerSecond)),
            new("接收", Bits(activity.ReceivedBitsPerSecond)),
            new("介面卡名稱", activity.Name),
            new("連線類型", adapter?.Kind ?? "—"),
            new("連結速度", adapter is { SpeedBitsPerSecond: > 0 } ? Bits(adapter.SpeedBitsPerSecond) : "—"),
            new("IPv4 位址", adapter is { IPv4.Count: > 0 } ? string.Join("、", adapter.IPv4) : "—"),
            new("IPv6 位址", adapter is { IPv6.Count: > 0 } ? adapter.IPv6[0] : "—"),
            new("實體位址", adapter?.Mac is { Length: > 0 } mac ? mac : "—"),
        ];
    }

    private static void ShowGpu(PerfResource resource, GpuInfo gpu, GpuSample? sample)
    {
        var utilization = sample?.Utilization ?? 0;
        resource.Push(utilization);
        resource.Subtitle = gpu.Name;
        resource.Summary = $"{utilization:0}%";
        resource.Stats =
        [
            new("使用率", $"{utilization:0}%"),
            new("專用 GPU 記憶體", $"{ByteSize.Format(sample?.DedicatedUsed ?? 0)}／{ByteSize.Format((long)gpu.DedicatedBytes)}"),
            new("共用 GPU 記憶體", $"{ByteSize.Format(sample?.SharedUsed ?? 0)}／{ByteSize.Format((long)gpu.SharedBytes)}"),
            new("驅動程式版本", gpu.DriverVersion.Length > 0 ? gpu.DriverVersion : "—"),
            new("驅動程式日期", gpu.DriverDate?.ToString("yyyy/MM/dd") ?? "—"),
        ];
    }

    private static string Uptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"{(int)uptime.TotalDays}:{uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
    }

    private static string Bytes(long? bytes) => bytes is { } value ? ByteSize.Format(value) : "—";

    private static string Kilobytes(int kilobytes) => kilobytes >= 1024 ? $"{kilobytes / 1024.0:0.#} MB" : $"{kilobytes} KB";

    private static string Bits(double bitsPerSecond) => bitsPerSecond switch
    {
        < 1_000 => $"{bitsPerSecond:0} bps",
        < 1_000_000 => $"{bitsPerSecond / 1_000:0.#} Kbps",
        < 1_000_000_000 => $"{bitsPerSecond / 1_000_000:0.#} Mbps",
        _ => $"{bitsPerSecond / 1_000_000_000:0.##} Gbps",
    };

    private sealed record Snapshot(
        IReadOnlyList<ProcessSample> Processes, IReadOnlyDictionary<(int Id, long CreateTime), double> Cpu,
        PerformanceSnapshot Performance, ComputerInfo Hardware);
}

public sealed partial class ProcessRow(ProcessSample sample, string name, string? path, string? whyNotEnd) : ObservableObject
{
    public ProcessSample Sample { get; private set; } = sample;
    public string Name { get; } = name;
    public string? Path { get; } = path;
    public string? WhyNotEnd { get; } = whyNotEnd;
    public int Id => Sample.Id;
    public string Tooltip => Path ?? Sample.ImageName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuText))]
    private double _cpuPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText))]
    private long _memoryBytes;

    [ObservableProperty]
    private int _threads;

    public string CpuText => CpuPercent >= 0.05 ? $"{CpuPercent:0.0}%" : "0%";
    public string MemoryText => ByteSize.Format(MemoryBytes);

    public void Update(ProcessSample sample, double cpu)
    {
        Sample = sample;
        CpuPercent = Math.Round(cpu, 1);
        MemoryBytes = sample.PrivateWorkingSet;
        Threads = sample.Threads;
    }

    public bool Matches(string text) =>
        Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        Sample.ImageName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Id.ToString().Contains(text, StringComparison.Ordinal);
}

/// <summary>A label and its value, as shown in the detail grids.</summary>
public sealed record LabeledValue(string Label, string Value);

/// <summary>One entry of the performance tab (CPU, memory, a disk, an adapter, a GPU); <see cref="Kind"/> only picks its colour.</summary>
public sealed partial class PerfResource(string kind, string title, int capacity) : ObservableObject
{
    private readonly List<double> _values = [];

    public string Kind { get; } = kind;
    public string Title { get; } = title;
    public int Capacity { get; } = capacity;

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private double[] _history = [];

    [ObservableProperty]
    private double _maximum = 100;

    [ObservableProperty]
    private string _scaleText = "100%";

    [ObservableProperty]
    private IReadOnlyList<LabeledValue> _stats = [];

    public void Push(double percent) => Record(percent);

    /// <summary>For throughput: the scale follows the busiest moment on screen, with a floor so idle noise stays flat.</summary>
    public void PushScaled(double value, double floor, Func<double, string> format)
    {
        Record(value);
        Maximum = Math.Max(_values.Max() * 1.25, floor);
        ScaleText = format(Maximum);
    }

    private void Record(double value)
    {
        _values.Add(value);
        if (_values.Count > Capacity) _values.RemoveAt(0);
        History = [.. _values];
    }
}
