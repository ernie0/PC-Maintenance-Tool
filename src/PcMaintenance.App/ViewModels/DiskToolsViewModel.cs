using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.DiskTools;

namespace PcMaintenance.App.ViewModels;

public sealed partial class DiskToolsViewModel(AppServices services) : PageViewModel
{
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public override string Title => "磁碟工具";

    public override string Subtitle =>
        "查看各磁碟的健康狀態與剩餘空間，檢查磁碟錯誤，或最佳化磁碟（SSD 執行修剪，傳統硬碟重組）。Windows 預設每週會自動最佳化，平常不必手動執行。";

    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    public string AdminHint => services.IsElevated
        ? ""
        : "檢查錯誤與最佳化需要系統管理員權限，請按上方「以系統管理員重新啟動」。磁碟溫度、使用時數與磨損程度也只有系統管理員能讀取。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CheckCommand), nameof(OptimizeCommand), nameof(ScheduleRepairCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    public override void OnNavigatedTo()
    {
        if (!_loaded && !IsBusy) RefreshCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var elevated = services.IsElevated;
            var inventory = await Task.Run(() => DiskInventory.Read(includeReliability: elevated));
            Volumes.Clear();
            foreach (var volume in inventory.Volumes)
                Volumes.Add(new VolumeRow(volume, inventory.Disks.FirstOrDefault(d => d.Number == volume.DiskNumber)));
            // Every disk counts here, including one without a drive letter that has no card of its own.
            StatusText = inventory.Error ?? (inventory.Disks.Any(d => !d.IsHealthy && d.Health != "未知")
                ? "有磁碟的健康狀態不正常，請盡快備份資料並安排檢修。"
                : "");
            _loaded = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task CheckAsync(VolumeRow? row) =>
        RunAsync(row, "正在檢查磁碟錯誤，大型硬碟可能需要數分鐘…", (volume, ct) => DiskMaintenance.CheckAsync(volume, services.Log, ct));

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task OptimizeAsync(VolumeRow? row)
    {
        if (row?.Disk?.MediaType == "傳統硬碟" &&
            !Dialogs.Confirm($"重組傳統硬碟 {row.Volume.Letter}: 可能需要較長時間，期間電腦會變慢，可以隨時取消。要開始嗎？", "最佳化磁碟"))
            return Task.CompletedTask;
        return RunAsync(row, "正在最佳化…", (volume, ct) => DiskMaintenance.OptimizeAsync(volume, services.Log, ct));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ScheduleRepairAsync(VolumeRow? row)
    {
        if (row is null || !Dialogs.Confirm($"下次開機時，Windows 會先檢查並修復 {row.Volume.Letter}:，可能需要數分鐘到數小時，期間請不要關機。\n\n建議先備份重要資料。確定要排定嗎？", "排定開機時修復"))
            return Task.CompletedTask;
        return RunAsync(row, "正在排定…", (volume, _) => DiskMaintenance.ScheduleRepairAsync(volume, services.Log));
    }

    private bool CanRun(VolumeRow? row) => services.IsElevated && !IsBusy && row is not null;

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(VolumeRow? row, string runningText, Func<VolumeInfo, CancellationToken, Task<DiskJobResult>> job)
    {
        if (row is null) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        row.Begin(runningText);
        try
        {
            row.Show(await job(row.Volume, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            row.ShowCancelled();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            row.ShowError(UserMessage.For(ex));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }
}

public sealed partial class VolumeRow : ObservableObject
{
    public VolumeRow(VolumeInfo volume, PhysicalDiskInfo? disk)
    {
        Volume = volume;
        Disk = disk;
    }

    public VolumeInfo Volume { get; }
    public PhysicalDiskInfo? Disk { get; }

    public string Title => $"{Volume.Letter}:" + (Volume.Label.Length > 0 ? $" {Volume.Label}" : "");

    public string Details => string.Join(" · ", new[]
    {
        Volume.IsSystem ? "系統磁碟" : Volume.IsRemovable ? "抽取式磁碟" : "本機磁碟",
        Volume.FileSystem,
        Disk is null ? "" : Disk.MediaType == "未知" ? Disk.BusType : $"{Disk.MediaType}（{Disk.BusType}）",
        Disk?.Model ?? "",
    }.Where(s => s.Length > 0));

    /// <summary>Counters the drive reports itself; only available when elevated, otherwise empty.</summary>
    public string ReliabilityText => Disk?.Reliability is not { } r
        ? ""
        : string.Join(" · ", new[]
        {
            r.TemperatureC is { } t ? $"溫度 {t}°C" : "",
            r.PowerOnHours is { } h ? $"已使用 {h:N0} 小時" : "",
            Disk.MediaType == "SSD" && r.WearPercent is { } w ? $"壽命已用 {w}%" : "",
        }.Where(s => s.Length > 0));

    public string HealthText => Disk is null ? "健康狀態：未知" : $"健康狀態：{Disk.Health}";
    public string HealthLevel => Disk?.Health ?? "未知";
    public double UsedPercent => Volume.TotalBytes == 0 ? 0 : 100.0 * Volume.UsedBytes / Volume.TotalBytes;
    public bool IsLowSpace => Volume.FreePercent < 10;

    public string UsageText =>
        $"已用 {ByteSize.Format(Volume.UsedBytes)}／{ByteSize.Format(Volume.TotalBytes)}，可用 {ByteSize.Format(Volume.FreeBytes)}（{Volume.FreePercent:0}%）" +
        (IsLowSpace ? "，空間不足" : "");

    public bool CanCheck => DiskMaintenance.CanCheck(Volume.FileSystem);

    // Flash drives gain nothing from optimisation, and Windows itself does not schedule it for them.
    public bool CanOptimize => !Volume.IsRemovable;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private string _resultDetail = "";

    [ObservableProperty]
    private bool _canScheduleRepair;

    public void Begin(string text)
    {
        IsRunning = true;
        ResultText = text;
        ResultDetail = "";
        CanScheduleRepair = false;
    }

    public void Show(DiskJobResult result)
    {
        IsRunning = false;
        ResultText = result.Summary;
        ResultDetail = result.Outcome == DiskJobOutcome.Ok ? "" : DiskMaintenance.Tail(result.Output);
        CanScheduleRepair = result.Outcome == DiskJobOutcome.NeedsRepair && Volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    public void ShowCancelled()
    {
        IsRunning = false;
        ResultText = "已取消";
    }

    public void ShowError(string message)
    {
        IsRunning = false;
        ResultText = $"無法執行：{message}";
    }
}
