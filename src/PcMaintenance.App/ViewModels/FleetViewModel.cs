using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;

namespace PcMaintenance.App.ViewModels;

/// <summary>One PC on the multi-computer page.</summary>
public sealed record FleetRow(FleetComputer Computer, DateTime Now)
{
    public string Name => Computer.Name;
    public string Windows => Computer.Windows.Length > 0 ? Computer.Windows : "—";
    public bool NeedsAttention => Computer.NeedsAttention;

    public string UpdateText => Computer.LastUpdate is { } date
        ? $"{date:yyyy/MM/dd}（{Math.Max(0, (int)(Now.Date - date.Date).TotalDays)} 天前）"
        : Computer.Fields.GetValueOrDefault("上次 Windows 更新", "") is { Length: > 0 } text ? text : "—";

    /// <summary>The lowest free share over all the PC's drives.</summary>
    public string SpaceText => Computer.LowestFreePercent is { } percent ? $"{percent:0}%" : "—";
    public string ConcernText => Computer.NeedsAttention ? string.Join("；", Computer.Concerns) : "正常";

    public string ExportedText => Computer.ExportedAt is not { } at ? "—"
        : at.ToString(at.Year == Now.Year ? "MM/dd HH:mm" : "yyyy/MM/dd");

    /// <summary>Every column of the export, for the tooltip.</summary>
    public string Tooltip => string.Join("\n", Computer.Fields.Where(f => f.Value.Length > 0).Select(f => $"{f.Key}：{f.Value}"));
}

/// <summary>
/// The multi-computer page: reads the computer-information exports many PCs wrote to one folder and shows one row per
/// PC, those needing attention first. It reads only the chosen folder (often a share on the local network).
/// </summary>
public sealed partial class FleetViewModel : PageViewModel
{
    private static readonly string RememberedFolderFile = Path.Combine(AppPaths.UserDataDir, "fleet-folder.txt");
    private readonly AppServices _services;
    private bool _loaded;

    public FleetViewModel(AppServices services)
    {
        _services = services;
        RowsView = new ListCollectionView(Rows) { Filter = item => item is FleetRow row && (!OnlyAttention || row.NeedsAttention) };
    }

    public override string Title => "多台電腦";

    public override string Subtitle =>
        "彙整各台電腦匯出的電腦配備檔，一張表看出哪幾台需要處理：太久沒更新、磁碟快滿或健康警告、安全性設定、最近當機等。只讀取你選的資料夾。";

    public ObservableCollection<FleetRow> Rows { get; } = [];
    public ListCollectionView RowsView { get; }

    public const string HowToCollect =
        "各台電腦執行 PcMaintenance.exe --export-system \"\\\\伺服器\\共用\\電腦盤點\\\"，就會把電腦配備檔存到這個資料夾（可以用派送工具、登入指令碼或排程定期執行）；同一台電腦有多個檔案時只看最新的一份。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder))]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(OpenFolderCommand))]
    private string _folder = "";

    [ObservableProperty]
    private bool _onlyAttention;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(ExportCommand), nameof(ChooseFolderCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private FleetRow? _selectedRow;

    public bool HasFolder => Folder.Length > 0;

    public string DetailText => SelectedRow is not { } row ? "" :
        $"{row.Name}：{row.ConcernText}　（{Path.GetFileName(row.Computer.SourceFile)}）";

    partial void OnOnlyAttentionChanged(bool value) => RowsView.Refresh();

    public override void OnNavigatedTo()
    {
        if (_loaded || IsBusy) return;
        if (Folder.Length == 0) Folder = RememberedFolder() ?? _services.Settings.InventoryFolder;
        if (Folder.Length > 0) LoadCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "選擇放各台電腦配備檔的資料夾" };
        if (Folder.Length > 0 && Directory.Exists(Folder)) dialog.InitialDirectory = Folder;
        if (dialog.ShowDialog() != true) return;
        Folder = dialog.FolderName;
        Remember(Folder);
        LoadCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        IsBusy = true;
        SummaryText = $"正在讀取 {Folder} …";
        try
        {
            var folder = Folder;
            var now = DateTime.Now;
            var load = await Task.Run(() => FleetSummary.Load(folder, now));
            Rows.Clear();
            foreach (var computer in load.Computers) Rows.Add(new FleetRow(computer, now));
            _loaded = true;
            SummaryText = load.Problem ?? (load.Computers.Count == 0
                ? $"這個資料夾裡沒有電腦配備檔（讀了 {load.FilesSkipped} 個其他 CSV）。"
                : $"共 {load.Computers.Count} 台電腦，{load.Computers.Count(c => c.NeedsAttention)} 台需要注意（讀取 {load.FilesRead} 個電腦配備檔" +
                  (load.FilesSkipped > 0 ? $"，略過 {load.FilesSkipped} 個其他 CSV" : "") + $"；{now:HH:mm} 讀取）。");
            _services.Log.Info($"多台電腦：{SummaryText}");
        }
        finally
        {
            IsBusy = false;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (Dialogs.AskCsvPath(FleetSummary.Kind) is not { } path) return;
        try
        {
            var count = FleetSummary.WriteSummary(path, Rows.Select(r => r.Computer).ToList(), DateTime.Now);
            SummaryText = $"已匯出 {count} 台電腦的彙整表到 {path}";
            _services.Log.Info($"匯出電腦彙整：{count} 台，{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"無法匯出：{UserMessage.For(ex)}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasExistingFolder))]
    private void OpenFolder() => Process.Start("explorer.exe", $"\"{Folder}\"")?.Dispose();

    private bool NotBusy() => !IsBusy;
    private bool CanLoad() => !IsBusy && Folder.Length > 0;
    private bool CanExport() => !IsBusy && Rows.Count > 0;
    private bool HasExistingFolder() => Folder.Length > 0 && Directory.Exists(Folder);

    private static string? RememberedFolder()
    {
        try
        {
            return File.Exists(RememberedFolderFile) && File.ReadAllText(RememberedFolderFile).Trim() is { Length: > 0 } folder ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Remember(string folder)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserDataDir);
            File.WriteAllText(RememberedFolderFile, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
