using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Journal;
using PcMaintenance.Core.Startup;

namespace PcMaintenance.App.ViewModels;

public sealed record FilterOption(string Label, StartupSource[]? Sources);

public sealed record StartTypeOption(ServiceStartType Type)
{
    public string Label => StartupText.StartTypeName(Type);

    // A drop-down item is read out (and found by UI Automation) by this text, not by its displayed member.
    public override string ToString() => Label;
}

public sealed partial class StartupViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly StartupManager _manager;
    private List<StartupRow> _all = [];
    private bool _loaded;

    public StartupViewModel(AppServices services)
    {
        _services = services;
        _manager = StartupManager.CreateDefault(services.Settings, services.Journal, services.Log);
        Filters =
        [
            new FilterOption("全部", null),
            new FilterOption("開機啟動項", [StartupSource.RegistryRun, StartupSource.StartupFolder]),
            new FilterOption("排程工作", [StartupSource.ScheduledTask]),
            new FilterOption("服務", [StartupSource.Service]),
        ];
        StartTypes =
        [
            new StartTypeOption(ServiceStartType.Automatic),
            new StartTypeOption(ServiceStartType.AutomaticDelayed),
            new StartTypeOption(ServiceStartType.Manual),
            new StartTypeOption(ServiceStartType.Disabled),
        ];
        _selectedFilter = Filters[0];
        _showMicrosoft = services.Settings.ShowMicrosoftItems;
    }

    public override string Title => "開機與背景";
    public override string Subtitle =>
        "停用不需要隨開機執行的程式、排程工作與服務。停用不會刪除項目，可在「變更紀錄」還原。服務的啟動類型下次開機生效，也可以立即啟動或停止。";

    public IReadOnlyList<FilterOption> Filters { get; }
    public IReadOnlyList<StartTypeOption> StartTypes { get; }
    public ObservableCollection<StartupRow> Items { get; } = [];

    [ObservableProperty]
    private FilterOption _selectedFilter;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showMicrosoft;

    /// <summary>Only entries marked for a second look: risky folder, no valid signature, or a false Microsoft claim.</summary>
    [ObservableProperty]
    private bool _onlyAttention;

    [ObservableProperty]
    private bool _isCheckingSignatures;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand), nameof(EnableCommand), nameof(ApplyStartTypeCommand),
        nameof(StartServiceCommand), nameof(StopServiceCommand), nameof(RestartServiceCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsServiceSelected), nameof(SelectionHint))]
    private StartupRow? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyStartTypeCommand))]
    private StartTypeOption? _selectedStartType;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DisableCommand), nameof(EnableCommand), nameof(ApplyStartTypeCommand), nameof(ExportCommand),
        nameof(StartServiceCommand), nameof(StopServiceCommand), nameof(RestartServiceCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _countText = "";

    /// <summary>How long the last starts took and what slowed them, from Windows' own measurements.</summary>
    [ObservableProperty]
    private string _bootText = "";

    [ObservableProperty]
    private string? _bootDetail;

    public bool HasSelection => SelectedItem is not null;
    public bool IsServiceSelected => SelectedItem?.Item.Source == StartupSource.Service;
    public string SelectionHint => SelectedItem is null ? "" : WhyLocked(SelectedItem.Item) ?? "";

    public override void OnNavigatedTo()
    {
        if (!_loaded && !IsBusy) RefreshCommand.Execute(null);
    }

    partial void OnSelectedFilterChanged(FilterOption value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowMicrosoftChanged(bool value) => ApplyFilter();
    partial void OnOnlyAttentionChanged(bool value) => ApplyFilter();

    partial void OnSelectedItemChanged(StartupRow? value) =>
        SelectedStartType = value?.Item.StartType is { } type ? StartTypes.FirstOrDefault(o => o.Type == type) : null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "正在讀取開機項目、排程工作與服務…";
        try
        {
            var (result, boot) = await Task.Run(() => (_manager.Load(), BootPerformance.Read()));
            (BootText, BootDetail) = DescribeBoot(boot, _services.IsElevated);
            _all = result.Items.Select(i => new StartupRow(i)).ToList();
            _loaded = true;
            ApplyFilter();
            StatusText = result.Errors.Count > 0 ? string.Join("；", result.Errors) : "";
        }
        finally
        {
            IsBusy = false;
        }
        await CheckSignaturesAsync(_all);
    }

    /// <summary>
    /// Verifies each program's digital signature after the list is shown (a few seconds for a few hundred files) and
    /// marks what deserves a second look. Nothing is disabled automatically.
    /// </summary>
    private async Task CheckSignaturesAsync(List<StartupRow> rows)
    {
        IsCheckingSignatures = true;
        ApplyFilter();
        try
        {
            var notes = await Task.Run(() =>
            {
                var risky = StartupRisk.RiskyFolders();
                return rows.Select(r => r.Item.ExecutablePath is { Length: > 0 } path
                    ? StartupRisk.Describe(r.Item, FileSignatures.Check(path), risky)
                    : null).ToList();
            });
            if (!ReferenceEquals(rows, _all)) return;
            for (var i = 0; i < rows.Count; i++) rows[i].Attention = notes[i];
            var marked = rows.Where(r => r.NeedsAttention).ToList();
            if (marked.Count > 0) _services.Log.Info($"開機與背景：{marked.Count} 項需要注意（{string.Join("、", marked.Take(10).Select(r => $"{r.Name}：{r.Attention}"))}）");
        }
        finally
        {
            IsCheckingSignatures = false;
            ApplyFilter();
        }
    }

    private bool CanRefresh() => !IsBusy;

    internal static (string Text, string? Detail) DescribeBoot(BootReport boot, bool isElevated)
    {
        static string Seconds(TimeSpan t) => $"{t.TotalSeconds:0} 秒";
        if (!boot.Readable)
            return (isElevated ? "無法讀取 Windows 的開機時間紀錄。" : "以系統管理員執行可以看到每次開機花多久，以及拖慢開機的程式、驅動程式與服務。", null);
        if (boot.Last is not { } last) return ("Windows 還沒有記錄開機時間。", null);
        var text = $"上次開機到桌面 {Seconds(last.ToDesktop)}、到完全就緒 {Seconds(last.ToReady)}" +
                   (boot.Boots.Count > 1 ? $"（最近 {Math.Min(10, boot.Boots.Count)} 次平均到桌面 {Seconds(boot.AverageToDesktop!.Value)}）" : "");
        if (boot.SlowStarters.Count == 0) return (text + "。", null);
        var worst = boot.SlowStarters[0];
        text += $"；拖慢開機最多的是{worst.Kind}「{worst.Name}」（多 {worst.Delay.TotalSeconds:0.#} 秒）。";
        var detail = "Windows 記錄到拖慢開機的項目：\n" + string.Join("\n", boot.SlowStarters.Take(8).Select(s =>
            $"{s.Kind}「{s.Name}」多 {s.Delay.TotalSeconds:0.#} 秒（{s.TimeUtc.ToLocalTime():yyyy/MM/dd}）"));
        return (text, detail);
    }

    /// <summary>Exports every item, Microsoft ones included and marked, regardless of the current filter.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (Dialogs.AskCsvPath(InventoryExport.StartupKind) is not { } path) return;
        try
        {
            var attention = _all.ToDictionary(r => r.Item.Key, r => r.Attention);
            var count = InventoryExport.WriteStartup(path, _all.Select(r => r.Item), Environment.MachineName, item => attention.GetValueOrDefault(item.Key));
            StatusText = $"已匯出 {count} 項到 {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"無法匯出：{UserMessage.For(ex)}");
        }
    }

    private bool CanExport() => !IsBusy && _loaded;

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private Task DisableAsync() => ChangeAsync(row => _manager.SetEnabled(row.Item, false));

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableAsync() => ChangeAsync(row => _manager.SetEnabled(row.Item, true));

    [RelayCommand(CanExecute = nameof(CanApplyStartType))]
    private Task ApplyStartTypeAsync() => ChangeAsync(row => _manager.SetServiceStartType(row.Item, SelectedStartType!.Type));

    private bool CanModify() => !IsBusy && SelectedItem is { } row && WhyLocked(row.Item) is null;
    private bool CanDisable() => CanModify() && SelectedItem!.Item.Enabled;
    private bool CanEnable() => CanModify() && !SelectedItem!.Item.Enabled;

    private bool CanApplyStartType() =>
        CanModify() && IsServiceSelected && SelectedStartType is not null && SelectedStartType.Type != SelectedItem!.Item.StartType;

    [RelayCommand(CanExecute = nameof(CanStartService))]
    private Task StartServiceAsync() => RunServiceAsync(ServiceAction.Start);

    [RelayCommand(CanExecute = nameof(CanStopService))]
    private Task StopServiceAsync() => RunServiceAsync(ServiceAction.Stop);

    [RelayCommand(CanExecute = nameof(CanStopService))]
    private Task RestartServiceAsync() => RunServiceAsync(ServiceAction.Restart);

    private bool CanStartService() => CanModify() && IsServiceSelected && SelectedItem!.Item.ServiceStatus == "已停止";
    private bool CanStopService() => CanModify() && IsServiceSelected && SelectedItem!.Item.ServiceStatus == "執行中";

    /// <summary>Starting or stopping lasts until the next restart; the start type decides what happens after that.</summary>
    private async Task RunServiceAsync(ServiceAction action)
    {
        if (SelectedItem is not { } row) return;
        var verb = ServiceRunner.ActionName(action);
        if (action != ServiceAction.Start &&
            !Dialogs.Confirm($"確定要{verb}「{row.Name}」嗎？\n\n使用這個服務的程式在這段時間可能無法運作。", $"{verb}服務"))
            return;
        IsBusy = true;
        StatusText = $"正在{verb}「{row.Name}」…";
        try
        {
            await Task.Run(() => ServiceRunner.Run(row.Item, action, _services.Log));
        }
        catch (Exception ex) when (StartupManager.IsExpected(ex))
        {
            StatusText = "";
            Dialogs.Error($"無法{verb}「{row.Name}」：{UserMessage.For(ex)}");
            return;
        }
        finally
        {
            IsBusy = false;
        }
        var key = row.Item.Key;
        await RefreshAsync();
        StatusText = $"已{verb}服務：{row.Name}";
        SelectedItem = Items.FirstOrDefault(r => r.Item.Key == key);
    }

    private string? WhyLocked(StartupItem item)
    {
        if (!item.CanChange)
        {
            if (item.Source != StartupSource.Service) return "此項目不提供變更。";
            return _services.Settings.AllowServiceChanges ? "Microsoft 系統服務與核心服務不提供變更。" : "公司設定已關閉服務變更。";
        }
        return item.RequiresAdmin && !_services.IsElevated ? "需要系統管理員權限，請按上方「以系統管理員重新啟動」。" : null;
    }

    private async Task ChangeAsync(Func<StartupRow, ChangeRecord> change)
    {
        if (SelectedItem is not { } row) return;
        try
        {
            var record = change(row);
            StatusText = $"已{record.Description}：{row.Name}";
        }
        catch (Exception ex) when (StartupManager.IsExpected(ex))
        {
            Dialogs.Error($"無法變更「{row.Name}」：{UserMessage.For(ex)}");
            return;
        }
        var key = row.Item.Key;
        var status = StatusText;
        await RefreshAsync();
        StatusText = status;
        SelectedItem = Items.FirstOrDefault(r => r.Item.Key == key);
    }

    private void ApplyFilter()
    {
        var selectedKey = SelectedItem?.Item.Key;
        // An entry that calls itself Microsoft without Microsoft's signature is always shown.
        var query = _all.Where(r => ShowMicrosoft || !r.Item.IsMicrosoft || r.NeedsAttention);
        if (OnlyAttention) query = query.Where(r => r.NeedsAttention);
        if (SelectedFilter.Sources is { } sources) query = query.Where(r => sources.Contains(r.Item.Source));
        if (!string.IsNullOrWhiteSpace(SearchText)) query = query.Where(r => r.Matches(SearchText.Trim()));
        Items.Clear();
        foreach (var row in query) Items.Add(row);
        SelectedItem = Items.FirstOrDefault(r => r.Item.Key == selectedKey);
        var hidden = ShowMicrosoft ? 0 : _all.Count(r => r.Item.IsMicrosoft && !r.NeedsAttention);
        var attention = _all.Count(r => r.NeedsAttention);
        CountText = $"顯示 {Items.Count} 項，共 {_all.Count} 項" + (hidden > 0 ? $"（已隱藏 {hidden} 項 Microsoft 項目）" : "") +
                    (IsCheckingSignatures ? "；正在檢查數位簽章…" : attention > 0 ? $"；{attention} 項需要注意" : _loaded ? "；數位簽章都正常" : "");
    }
}

public sealed partial class StartupRow(StartupItem item) : ObservableObject
{
    public StartupItem Item { get; } = item;

    /// <summary>Why this entry deserves a second look (see <see cref="StartupRisk"/>); null when nothing stands out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention), nameof(AttentionText))]
    private string? _attention;

    public bool NeedsAttention => Attention is not null;
    public string AttentionText => Attention ?? "";
    public string Name => Item.Name;
    public bool IsOn => Item.Enabled;
    public string StateText => StartupText.StateName(Item);
    public string SourceText => StartupText.SourceName(Item.Source);
    public string PublisherText => string.IsNullOrWhiteSpace(Item.Publisher) ? "（未知）" : Item.Publisher;
    public string DetailText => Item.Source == StartupSource.Service ? Item.ServiceStatus ?? "" : Item.Note;
    public string Command => Item.Command;
    public string Location => Item.Location;
    public string Description => Item.Source == StartupSource.Service ? Item.Note : "";

    public bool Matches(string text) =>
        Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        PublisherText.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        Command.Contains(text, StringComparison.OrdinalIgnoreCase);
}
