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

    partial void OnSelectedItemChanged(StartupRow? value) =>
        SelectedStartType = value?.Item.StartType is { } type ? StartTypes.FirstOrDefault(o => o.Type == type) : null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "正在讀取開機項目、排程工作與服務…";
        try
        {
            var result = await Task.Run(_manager.Load);
            _all = result.Items.Select(i => new StartupRow(i)).ToList();
            _loaded = true;
            ApplyFilter();
            StatusText = result.Errors.Count > 0 ? string.Join("；", result.Errors) : "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;

    /// <summary>Exports every item, Microsoft ones included and marked, regardless of the current filter.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (Dialogs.AskCsvPath(InventoryExport.StartupKind) is not { } path) return;
        try
        {
            var count = InventoryExport.WriteStartup(path, _all.Select(r => r.Item), Environment.MachineName);
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
        var query = _all.Where(r => ShowMicrosoft || !r.Item.IsMicrosoft);
        if (SelectedFilter.Sources is { } sources) query = query.Where(r => sources.Contains(r.Item.Source));
        if (!string.IsNullOrWhiteSpace(SearchText)) query = query.Where(r => r.Matches(SearchText.Trim()));
        Items.Clear();
        foreach (var row in query) Items.Add(row);
        SelectedItem = Items.FirstOrDefault(r => r.Item.Key == selectedKey);
        var hidden = ShowMicrosoft ? 0 : _all.Count(r => r.Item.IsMicrosoft);
        CountText = $"顯示 {Items.Count} 項，共 {_all.Count} 項" + (hidden > 0 ? $"（已隱藏 {hidden} 項 Microsoft 項目）" : "");
    }
}

public sealed class StartupRow(StartupItem item)
{
    public StartupItem Item { get; } = item;
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
