using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Junk;

namespace PcMaintenance.App.ViewModels;

public sealed partial class JunkViewModel : PageViewModel
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;
    private bool _hasScanned;

    public JunkViewModel(AppServices services)
    {
        _services = services;
        Categories = new ObservableCollection<JunkCategoryRow>(
            JunkCatalog.Create(services.Settings, services.Folders).Select(c => new JunkCategoryRow(c, services.IsElevated)));
        foreach (var row in Categories)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(JunkCategoryRow.IsSelected)) return;
                CleanCommand.NotifyCanExecuteChanged();
                UpdateSelectionSummary();
            };
        }
        _summaryText = AutoCleanSchedule.Load(AppPaths.AutoCleanStateFile) is { } last
            ? $"按「掃描」檢查可以清理的檔案。上次自動清理在 {last.LastRun.LocalDateTime:yyyy/MM/dd HH:mm}，釋放 {ByteSize.Format(last.FreedBytes)}。"
            : "按「掃描」檢查可以清理的檔案。";
    }

    public override string Title => "垃圾檔清理";

    public override string Subtitle =>
        $"先掃描，確認後再清理已勾選的項目。暫存類檔案只處理超過 {_services.Settings.MinFileAgeHours} 小時沒有變動的檔案；瀏覽器只清快取，不動瀏覽紀錄、Cookie 與密碼。";

    public ObservableCollection<JunkCategoryRow> Categories { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _summaryText;

    /// <summary>The summary reports a finished cleanup (shown with a tick).</summary>
    [ObservableProperty]
    private bool _hasCleaned;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        HasCleaned = false;
        var cts = Begin();
        var scanner = new JunkScanner(_services.Settings);
        try
        {
            foreach (var row in Categories)
            {
                ProgressText = $"正在掃描：{row.Name}";
                row.MarkScanning();
                var result = await Task.Run(() => scanner.Scan(row.Category, _services.IsElevated, cts.Token), cts.Token);
                row.ShowResult(result);
            }
            _hasScanned = true;
        }
        catch (OperationCanceledException)
        {
            foreach (var row in Categories.Where(r => r.IsScanning)) row.Reset();
            SummaryText = "已取消掃描。";
        }
        finally
        {
            End(cts);
        }
        UpdateSelectionSummary();
    }

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var targets = Cleanable().ToList();
        if (targets.Count == 0) return;
        var items = targets.Sum(t => t.Result!.ItemCount);
        var bytes = targets.Sum(t => t.Result!.TotalBytes);
        var names = string.Join("、", targets.Select(t => t.Name));
        if (!Dialogs.Confirm($"即將清理：{names}\n\n共 {items:N0} 個項目，約 {ByteSize.Format(bytes)}。刪除後無法復原，確定要清理嗎？", "確認清理"))
            return;

        var cts = Begin();
        var cleaner = new JunkCleaner(_services.Settings, _services.Log);
        long freed = 0;
        int deleted = 0, skipped = 0;
        var notes = new List<string>();
        try
        {
            foreach (var row in targets)
            {
                ProgressText = $"正在清理：{row.Name}";
                var result = await Task.Run(() => cleaner.Clean(row.Result!, cts.Token), cts.Token);
                row.ShowCleaned(result);
                freed += result.FreedBytes;
                deleted += result.Deleted;
                skipped += result.TotalSkipped;
                if (result.Note is not null) notes.Add($"{row.Name}：{result.Note}");
            }
        }
        catch (OperationCanceledException)
        {
            notes.Add("已取消，其餘項目未清理。");
        }
        finally
        {
            End(cts);
        }

        var summary = $"已刪除 {deleted:N0} 個項目，釋放 {ByteSize.Format(freed)}。";
        if (skipped > 0) summary += $"略過 {skipped:N0} 個使用中、已變更或沒有權限的檔案。";
        if (notes.Count > 0) summary += "\n" + string.Join("\n", notes);
        SummaryText = summary;
        HasCleaned = true;
    }

    private bool CanClean() => !IsBusy && Cleanable().Any();

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private IEnumerable<JunkCategoryRow> Cleanable() =>
        Categories.Where(r => r.IsSelected && r.Result is { CanClean: true, ItemCount: > 0 });

    private CancellationTokenSource Begin()
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        return _cts;
    }

    private void End(CancellationTokenSource cts)
    {
        _cts = null;
        cts.Dispose();
        IsBusy = false;
        ProgressText = "";
        CleanCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSelectionSummary()
    {
        if (!_hasScanned || IsBusy) return;
        var selected = Cleanable().ToList();
        SummaryText = selected.Count == 0
            ? "沒有勾選可以清理的項目。"
            : $"已勾選的項目約可釋放 {ByteSize.Format(selected.Sum(r => r.Result!.TotalBytes))}（{selected.Sum(r => r.Result!.ItemCount):N0} 個項目）。";
    }
}

/// <summary>Where a category stands, shown as the icon beside its status text.</summary>
public enum JunkRowState
{
    NotScanned,
    Scanning,
    Found,
    Nothing,
    Cleaned,
    NotCleaned,
    NeedsAdmin,
}

public sealed partial class JunkCategoryRow : ObservableObject
{
    public JunkCategoryRow(JunkCategory category, bool isElevated)
    {
        Category = category;
        IsAvailable = !category.RequiresAdmin || isElevated;
        _isSelected = category.DefaultSelected && IsAvailable;
        _statusText = IsAvailable ? "尚未掃描" : "需要系統管理員權限";
        _state = IsAvailable ? JunkRowState.NotScanned : JunkRowState.NeedsAdmin;
    }

    public JunkCategory Category { get; }
    public string Name => Category.Name;
    public string Description => Category.Description;
    public bool RequiresAdmin => Category.RequiresAdmin;
    public bool IsAvailable { get; }
    public bool IsScanning { get; private set; }
    public JunkScanResult? Result { get; private set; }

    public string Glyph => Category.Id switch
    {
        "user-temp" => "",
        "windows-temp" => "",
        "edge-cache" or "chrome-cache" or "firefox-cache" => "",
        "shader-cache" => "",
        "crash-dumps" or "system-crash-dumps" => "",
        "user-error-reports" or "system-error-reports" => "",
        "windows-update-cache" => "",
        "recycle-bin" => "",
        _ => "",
    };

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private JunkRowState _state;

    public void MarkScanning()
    {
        IsScanning = true;
        Result = null;
        WarningText = null;
        StatusText = "掃描中…";
        State = JunkRowState.Scanning;
    }

    public void Reset()
    {
        IsScanning = false;
        Result = null;
        StatusText = IsAvailable ? "尚未掃描" : "需要系統管理員權限";
        State = IsAvailable ? JunkRowState.NotScanned : JunkRowState.NeedsAdmin;
    }

    public void ShowResult(JunkScanResult result)
    {
        IsScanning = false;
        Result = result;
        if (!IsAvailable)
        {
            StatusText = "需要系統管理員權限";
            WarningText = null;
            State = JunkRowState.NeedsAdmin;
            return;
        }
        WarningText = result.Warning;
        var unit = Category.IsRecycleBin ? "個項目" : "個檔案";
        StatusText = result.ItemCount == 0 ? "沒有可清理的項目" : $"{ByteSize.Format(result.TotalBytes)}（{result.ItemCount:N0} {unit}）";
        State = result.ItemCount == 0 ? JunkRowState.Nothing : JunkRowState.Found;
    }

    public void ShowCleaned(CleanResult result)
    {
        Result = null;
        var notCleaned = result.Deleted == 0 && result.Note is not null;
        StatusText = notCleaned ? "未清理" : $"已釋放 {ByteSize.Format(result.FreedBytes)}";
        WarningText = result.Note ?? (result.TotalSkipped > 0 ? $"略過 {result.TotalSkipped:N0} 個使用中或沒有權限的檔案" : null);
        State = notCleaned ? JunkRowState.NotCleaned : JunkRowState.Cleaned;
    }
}
