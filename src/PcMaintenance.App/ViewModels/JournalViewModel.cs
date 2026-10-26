using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;
using PcMaintenance.Core.Startup;
using PcMaintenance.Core.Tweaks;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.App.ViewModels;

public sealed partial class JournalViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly StartupManager _startup;
    private readonly LeftoverRemover _remover;
    private readonly TweakEngine _tweaks;
    private readonly PowerPlans _powerPlans;

    public JournalViewModel(AppServices services)
    {
        _services = services;
        _startup = StartupManager.CreateDefault(services.Settings, services.Journal, services.Log);
        _remover = new LeftoverRemover(services.Journal, AppPaths.BackupDir, services.Log, services.Folders);
        _tweaks = new TweakEngine(services.Journal, services.Log);
        _powerPlans = new PowerPlans(services.Journal, services.Log);
    }

    public override string Title => "變更紀錄";
    public override string Subtitle => "這個工具做過的系統變更都記錄在這裡，選取後可以還原。刪除的暫存檔與快取不會記錄，也無法還原。";

    public ObservableCollection<JournalRow> Records { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyPropertyChangedFor(nameof(SelectionHint))]
    private JournalRow? _selectedRecord;

    [ObservableProperty]
    private string _statusText = "";

    public string SelectionHint => SelectedRecord is null ? "" : RestoreBlocker(SelectedRecord.Record) ?? "";

    public override void OnNavigatedTo() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var selectedId = SelectedRecord?.Record.Id;
        Records.Clear();
        foreach (var record in _services.Journal.Records.OrderByDescending(r => r.Time)) Records.Add(new JournalRow(record));
        SelectedRecord = Records.FirstOrDefault(r => r.Record.Id == selectedId);
        StatusText = Records.Count == 0 ? "還沒有任何變更紀錄。" : $"共 {Records.Count} 筆紀錄";
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (SelectedRecord is not { } row) return;
        var record = row.Record;
        if (!Dialogs.Confirm($"確定要還原這項變更嗎？\n\n{record.Description}\n{record.Target}", "確認還原")) return;
        try
        {
            if (_startup.CanRestore(record)) _startup.Restore(record);
            else if (TweakEngine.CanRestore(record)) _tweaks.Restore(record);
            else if (PowerPlans.CanRestore(record)) await _powerPlans.RestoreAsync(record);
            else _remover.Restore(record);
            Refresh();
            StatusText = $"已還原：{record.Description}－{record.Target}";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or COMException
                                       or Win32Exception or InvalidOperationException or ArgumentException)
        {
            Dialogs.Error($"無法還原：{UserMessage.For(ex)}");
        }
    }

    private bool CanRestore() => SelectedRecord is { } row && RestoreBlocker(row.Record) is null;

    [RelayCommand]
    private void OpenDataFolder() => Shell.OpenFolder(AppPaths.UserDataDir);

    private string? RestoreBlocker(ChangeRecord record)
    {
        if (record.Restored) return "這項變更已經還原過。";
        if (record.Kind == ChangeKind.FolderRecycled) return "資料夾已移到資源回收筒，請開啟資源回收筒還原。";
        if (record.Kind == ChangeKind.ShortcutRecycled) return "捷徑已移到資源回收筒，請開啟資源回收筒還原。";
        if (record.Kind == ChangeKind.StoreAppRemoved) return "這個 App 可以從 Microsoft Store 重新安裝。";
        if (NeedsAdmin(record) && !_services.IsElevated) return "需要系統管理員權限才能還原，請按上方「以系統管理員重新啟動」。";
        return null;
    }

    private static bool NeedsAdmin(ChangeRecord record) =>
        record.Kind is ChangeKind.ScheduledTask or ChangeKind.ServiceStartType ||
        (record.RestoreData.TryGetValue("hive", out var hive) && hive == nameof(RegistryHive.LocalMachine)) ||
        (record.Kind == ChangeKind.RegistryKeyDeleted && record.Target.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.Ordinal)) ||
        (TweakEngine.CanRestore(record) && TweakEngine.RestoreRequiresAdmin(record));
}

public sealed class JournalRow(ChangeRecord record)
{
    public ChangeRecord Record { get; } = record;
    public string TimeText => Record.Time.LocalDateTime.ToString("yyyy/MM/dd HH:mm");

    public string KindText => Record.Kind switch
    {
        ChangeKind.StartupRegistry => "開機啟動項",
        ChangeKind.StartupFolder => "啟動資料夾",
        ChangeKind.ScheduledTask => "排程工作",
        ChangeKind.ServiceStartType => "服務",
        ChangeKind.RegistryKeyDeleted => "登錄機碼",
        ChangeKind.FolderRecycled => "資料夾",
        ChangeKind.ShortcutRecycled => "捷徑",
        ChangeKind.SettingChanged => "系統設定",
        ChangeKind.StoreAppRemoved => "App",
        _ => "",
    };

    public string KindGlyph => Record.Kind switch
    {
        ChangeKind.StartupRegistry => "",
        ChangeKind.StartupFolder => "",
        ChangeKind.ScheduledTask => "",
        ChangeKind.ServiceStartType => "",
        ChangeKind.RegistryKeyDeleted => "",
        ChangeKind.FolderRecycled => "",
        ChangeKind.ShortcutRecycled => "",
        ChangeKind.SettingChanged => "",
        ChangeKind.StoreAppRemoved => "",
        _ => "",
    };

    public bool IsRestored => Record.Restored;
    public string Description => Record.Description;
    public string Target => Record.Target;
    public string Detail => $"{Record.Description}\n{Record.Target}";

    public string StatusText => Record.Restored
        ? $"已還原（{Record.RestoredTime?.LocalDateTime:yyyy/MM/dd HH:mm}）"
        : Record.Kind switch
        {
            ChangeKind.FolderRecycled or ChangeKind.ShortcutRecycled => "可從資源回收筒還原",
            ChangeKind.StoreAppRemoved => "可從商店重新安裝",
            _ => "可還原",
        };
}
