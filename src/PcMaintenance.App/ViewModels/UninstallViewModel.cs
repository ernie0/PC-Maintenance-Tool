using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.App.ViewModels;

public sealed partial class UninstallViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly InstalledAppProvider _provider = new();
    private List<AppRow> _all = [];
    private bool _loaded;
    private InstalledApp? _pendingApp;
    private InstalledApp? _leftoverApp;

    public UninstallViewModel(AppServices services) => _services = services;

    public override string Title => "軟體解除安裝";
    public override string Subtitle =>
        "執行程式本身的解除安裝程式，完成後檢查留下的資料夾與登錄機碼；也可以移除 Windows 內建與商店 App。殘留資料夾會移到資源回收筒，登錄機碼刪除前會先備份。";

    public ObservableCollection<AppRow> Apps { get; } = [];
    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];
    public ObservableCollection<StoreAppRow> StoreAppRows { get; } = [];

    /// <summary>0 = desktop programs, 1 = built-in and Store apps (listed the first time the tab is opened).</summary>
    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshStoreAppsCommand), nameof(RemoveStoreAppsCommand))]
    private bool _isStoreBusy;

    [ObservableProperty]
    private string _storeStatus = "";

    private bool _storeLoaded;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 1 && !_storeLoaded && !IsStoreBusy) RefreshStoreAppsCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(NotStoreBusy))]
    private async Task RefreshStoreAppsAsync()
    {
        IsStoreBusy = true;
        StoreStatus = "正在讀取 App 清單…";
        try
        {
            var apps = await StoreApps.ListAsync();
            StoreAppRows.Clear();
            foreach (var app in apps)
            {
                var row = new StoreAppRow(app);
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(StoreAppRow.IsSelected)) RemoveStoreAppsCommand.NotifyCanExecuteChanged();
                };
                StoreAppRows.Add(row);
            }
            _storeLoaded = true;
            StoreStatus = $"共 {apps.Count} 個 App，其中 {apps.Count(a => a.IsCommonlyUnneeded)} 個是公司電腦通常用不到的內建 App（標示「常見不需要」）。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            StoreStatus = UserMessage.For(ex);
        }
        finally
        {
            IsStoreBusy = false;
        }
    }

    private bool NotStoreBusy() => !IsStoreBusy;

    [RelayCommand]
    private void SelectCommonlyUnneeded()
    {
        foreach (var row in StoreAppRows) row.IsSelected = row.App.IsCommonlyUnneeded;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveStoreApps))]
    private async Task RemoveStoreAppsAsync()
    {
        var selected = StoreAppRows.Where(r => r.IsSelected).ToList();
        var names = string.Join("、", selected.Take(8).Select(r => r.App.DisplayName)) + (selected.Count > 8 ? $" 等 {selected.Count} 個" : "");
        if (!Dialogs.Confirm($"將為目前使用者移除：{names}\n\n移除後可以從 Microsoft Store 重新安裝。確定要移除嗎？", "確認移除 App")) return;

        IsStoreBusy = true;
        var remover = new StoreAppRemover(_services.Journal, _services.Log);
        var failed = 0;
        try
        {
            foreach (var row in selected)
            {
                StoreStatus = $"正在移除「{row.App.DisplayName}」…";
                var (success, message) = await remover.RemoveAsync(row.App);
                if (success) StoreAppRows.Remove(row);
                else
                {
                    row.ResultText = message;
                    failed++;
                }
            }
            StoreStatus = $"已移除 {selected.Count - failed} 個 App" + (failed > 0 ? $"，{failed} 個無法移除（原因列在項目下方）。" : "。");
        }
        finally
        {
            IsStoreBusy = false;
        }
    }

    private bool CanRemoveStoreApps() => !IsStoreBusy && StoreAppRows.Any(r => r.IsSelected);

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private AppRow? _selectedApp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UninstallCommand), nameof(CheckAgainCommand), nameof(RemoveLeftoversCommand), nameof(ExportCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _leftoverTitle = "";

    [ObservableProperty]
    private bool _showLeftoverPanel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckAgainCommand))]
    private bool _hasPendingApp;

    public override void OnNavigatedTo()
    {
        if (!_loaded && !IsBusy) RefreshCommand.Execute(null);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task RefreshAsync() => LoadAsync();

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusyAndLoaded))]
    private void Export()
    {
        if (Dialogs.AskCsvPath(InventoryExport.SoftwareKind) is not { } path) return;
        try
        {
            var count = InventoryExport.WriteSoftware(path, _all.Select(r => r.App), Environment.MachineName);
            StatusText = $"已匯出 {count} 個程式到 {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"無法匯出：{UserMessage.For(ex)}");
        }
    }

    private bool NotBusyAndLoaded() => !IsBusy && _loaded;

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (SelectedApp is not { } row) return;
        var app = row.App;
        if (row.IsBroken)
        {
            await OfferBrokenEntryCleanupAsync(app);
            return;
        }
        if (!Dialogs.Confirm($"確定要解除安裝「{app.DisplayName}」嗎？\n\n將執行這個程式本身的解除安裝程式，請依照它的畫面完成。", "確認解除安裝"))
            return;

        IsBusy = true;
        StatusText = $"正在執行「{app.DisplayName}」的解除安裝程式，請依畫面指示完成…";
        UninstallRunResult result;
        try
        {
            result = await Uninstaller.RunAsync(app, _services.Log);
        }
        finally
        {
            IsBusy = false;
        }
        if (!result.Started)
        {
            StatusText = result.Message;
            return;
        }
        await LoadAsync();
        await AfterUninstallAsync(app);
    }

    private bool CanUninstall() => !IsBusy && SelectedApp is not null;

    /// <summary>The program was deleted by hand, so there is no uninstaller to run; offer what it left behind, list entry included.</summary>
    private async Task OfferBrokenEntryCleanupAsync(InstalledApp app)
    {
        if (!Dialogs.Confirm($"找不到「{app.DisplayName}」的解除安裝程式，程式可能已被手動刪除。\n\n要改為檢查它留下的資料夾與登錄項目（包含清單中的這一筆）嗎？移除前會再讓你確認，登錄機碼也會先備份。",
                "找不到解除安裝程式"))
            return;
        _pendingApp = null;
        HasPendingApp = false;
        var stillInstalled = _all.Select(r => r.App).ToList();
        var leftovers = await Task.Run(() => new LeftoverScanner(_services.Folders).Scan(app, stillInstalled));
        ShowLeftovers(app, leftovers);
        StatusText = leftovers.Count == 0
            ? $"沒有找到「{app.DisplayName}」可移除的項目。"
            : $"找不到「{app.DisplayName}」的解除安裝程式，請確認下方項目後再移除。";
    }

    /// <summary>Some uninstallers hand off to a background copy and exit at once, so the list is re-checked on demand.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckAgain))]
    private async Task CheckAgainAsync()
    {
        if (_pendingApp is not { } app) return;
        await LoadAsync();
        await AfterUninstallAsync(app);
    }

    private bool CanCheckAgain() => !IsBusy && HasPendingApp;

    [RelayCommand(CanExecute = nameof(CanRemoveLeftovers))]
    private async Task RemoveLeftoversAsync()
    {
        if (_leftoverApp is not { } app) return;
        var selected = Leftovers.Where(l => l.IsSelected).ToList();
        if (!_services.IsElevated && selected.Any(l => l.RequiresAdmin))
        {
            Dialogs.Info("勾選的項目中有需要系統管理員權限的資料夾或登錄機碼，請按上方「以系統管理員重新啟動」後再移除。");
            return;
        }
        var folders = selected.Count(l => l.Item.Kind == LeftoverKind.Folder);
        var keys = selected.Count - folders;
        var actions = new List<string>();
        if (folders > 0) actions.Add($"把 {folders} 個資料夾移到資源回收筒");
        if (keys > 0) actions.Add($"刪除 {keys} 個登錄機碼（刪除前會先備份，可在「變更紀錄」還原）");
        if (!Dialogs.Confirm($"將{string.Join("，並", actions)}。確定要移除嗎？", "確認移除殘留"))
            return;

        IsBusy = true;
        try
        {
            var remover = new LeftoverRemover(_services.Journal, AppPaths.BackupDir, _services.Log, _services.Folders);
            var outcomes = await Task.Run(() => remover.Remove(app, selected.Select(l => l.Item).ToList()));
            foreach (var outcome in outcomes)
            {
                var row = selected.First(r => ReferenceEquals(r.Item, outcome.Item));
                if (outcome.Success) Leftovers.Remove(row);
                else row.ResultText = outcome.Message;
            }
            var failed = outcomes.Count(o => !o.Success);
            var status = failed == 0
                ? $"已移除 {outcomes.Count} 個殘留項目。"
                : $"已移除 {outcomes.Count - failed} 個，{failed} 個無法移除，原因列在項目下方。";
            if (Leftovers.Count == 0) LeftoverTitle = $"「{app.DisplayName}」的殘留項目已處理完畢";
            // A removed list entry (for example one whose uninstaller was missing) must disappear from the list too.
            await LoadAsync();
            StatusText = status;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRemoveLeftovers() => !IsBusy && Leftovers.Any(l => l.IsSelected);

    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            // Qualified: inside this view model "UninstallCommand" is the generated button command.
            _all = await Task.Run(() => _provider.GetApps()
                .Select(a => new AppRow(a, PcMaintenance.Core.Uninstall.UninstallCommand.IsUninstallerMissing(a)))
                .ToList());
            _loaded = true;
            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AfterUninstallAsync(InstalledApp app)
    {
        if (_all.Any(r => r.App.Hive == app.Hive && r.App.View == app.View && r.App.KeyPath.Equals(app.KeyPath, StringComparison.OrdinalIgnoreCase)))
        {
            _pendingApp = app;
            HasPendingApp = true;
            StatusText = $"解除安裝程式已結束，但「{app.DisplayName}」仍在清單中（可能已取消，或解除安裝視窗還在進行）。完成後請按「重新檢查」。";
            return;
        }
        _pendingApp = null;
        HasPendingApp = false;
        StatusText = $"已解除安裝「{app.DisplayName}」，正在檢查殘留項目…";
        var stillInstalled = _all.Select(r => r.App).ToList();
        var scanner = new LeftoverScanner(_services.Folders);
        var leftovers = await Task.Run(() => scanner.Scan(app, stillInstalled));
        ShowLeftovers(app, leftovers);
    }

    private void ShowLeftovers(InstalledApp app, IReadOnlyList<Leftover> leftovers)
    {
        _leftoverApp = app;
        Leftovers.Clear();
        foreach (var leftover in leftovers)
        {
            var row = new LeftoverRow(leftover, RequiresAdmin(leftover));
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LeftoverRow.IsSelected)) RemoveLeftoversCommand.NotifyCanExecuteChanged();
            };
            Leftovers.Add(row);
        }
        ShowLeftoverPanel = true;
        LeftoverTitle = leftovers.Count == 0 ? $"「{app.DisplayName}」沒有發現殘留項目" : $"「{app.DisplayName}」的殘留項目（{leftovers.Count} 項）";
        StatusText = leftovers.Count == 0
            ? $"已解除安裝「{app.DisplayName}」，沒有發現殘留項目。"
            : $"已解除安裝「{app.DisplayName}」，請確認下方的殘留項目後再移除。";
        RemoveLeftoversCommand.NotifyCanExecuteChanged();
    }

    private bool RequiresAdmin(Leftover leftover)
    {
        if (leftover.Kind == LeftoverKind.RegistryKey) return leftover.Hive == RegistryHive.LocalMachine;
        var folders = _services.Folders;
        return new[] { folders.ProgramFiles, folders.ProgramFilesX86, folders.ProgramData, folders.WindowsDir }
            .Any(root => leftover.FolderPath is { } path && PathSafety.IsSameOrUnder(root, path));
    }

    private void ApplyFilter()
    {
        var selected = SelectedApp?.App;
        var query = string.IsNullOrWhiteSpace(SearchText) ? _all : _all.Where(r => r.Matches(SearchText.Trim()));
        Apps.Clear();
        foreach (var row in query) Apps.Add(row);
        SelectedApp = selected is null ? null : Apps.FirstOrDefault(r => r.App == selected);
        CountText = Apps.Count == _all.Count ? $"共 {_all.Count} 個程式" : $"顯示 {Apps.Count} 個，共 {_all.Count} 個程式";
    }
}

public sealed class AppRow(InstalledApp app, bool isBroken)
{
    public InstalledApp App { get; } = app;
    public bool IsBroken { get; } = isBroken;
    public string Name => App.DisplayName;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public string InstallDateText => App.InstallDate?.ToString("yyyy/MM/dd") ?? "";
    public string SizeText => App.EstimatedSizeBytes is { } bytes ? ByteSize.Format(bytes) : "";
    public long SizeSort => App.EstimatedSizeBytes ?? 0;
    public string Scope => App.Scope;

    public bool Matches(string text) =>
        Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        Publisher.Contains(text, StringComparison.CurrentCultureIgnoreCase);
}

public sealed partial class StoreAppRow(StoreApp app) : ObservableObject
{
    public StoreApp App { get; } = app;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string? _resultText;
}

public sealed partial class LeftoverRow : ObservableObject
{
    public LeftoverRow(Leftover item, bool requiresAdmin)
    {
        Item = item;
        RequiresAdmin = requiresAdmin;
        _isSelected = item.DefaultSelected;
    }

    public Leftover Item { get; }
    public bool RequiresAdmin { get; }
    public string KindText => Item.Kind == LeftoverKind.Folder ? "資料夾" : "登錄機碼";
    public string PathText => Item.DisplayPath;
    public string Reason => Item.Reason;
    public string SizeText => Item.SizeBytes is { } bytes ? ByteSize.Format(bytes) : "";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string? _resultText;
}
