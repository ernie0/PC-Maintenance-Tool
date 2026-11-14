using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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

/// <summary>A program or extension on the software tab.</summary>
public sealed record FleetSoftwareRow(FleetSoftware Software)
{
    public string Kind => Software.Kind;
    public string Name => Software.Name;
    public string Publisher => Software.Publisher.Length > 0 ? Software.Publisher : "—";
    public int Computers => Software.Computers;
    public string Newest => Software.NewestVersion.Length > 0 ? Software.NewestVersion : "—";
    public int Outdated => Software.Outdated;
    public string OutdatedText => Software.Outdated > 0 ? $"{Software.Outdated} 台" : "";
    public bool IsExtension => Software.Kind != FleetSoftwareList.SoftwareKind;
    public string Tooltip => (Software.Publisher.Length > 0 ? $"{(IsExtension ? "來源" : "發行者")}：{Software.Publisher}\n" : "") + $"版本：{Software.VersionsText}";

    public bool Matches(string text) =>
        Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        Publisher.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
        Software.Installs.Any(i => i.Computer.Contains(text, StringComparison.CurrentCultureIgnoreCase));
}

public sealed record SoftwareKindOption(string Label, bool? Extensions);

/// <summary>
/// The multi-computer page: reads the exports many PCs wrote to one folder and shows one row per PC, those needing
/// attention first, and on a second tab which software and browser extensions each PC has. It reads only the chosen
/// folder (often a share on the local network).
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
        SoftwareView = new ListCollectionView(SoftwareRows) { Filter = item => item is FleetSoftwareRow row && SoftwareShown(row) };
        SoftwareKinds = [new("全部", null), new("軟體", false), new("瀏覽器擴充功能", true)];
        _selectedSoftwareKind = SoftwareKinds[0];
    }

    public override string Title => "多台電腦";

    public override string Subtitle =>
        "彙整各台電腦匯出的電腦配備與軟體清單，一張表看出哪幾台需要處理：太久沒更新、磁碟快滿或健康警告、Windows 10、沒有備份、安全性設定、最近當機等，也看得到哪些軟體裝在哪幾台。只讀取你選的資料夾。";

    public ObservableCollection<FleetRow> Rows { get; } = [];
    public ListCollectionView RowsView { get; }
    public ObservableCollection<FleetSoftwareRow> SoftwareRows { get; } = [];
    public ListCollectionView SoftwareView { get; }
    public ObservableCollection<FleetInstall> InstallRows { get; } = [];
    public IReadOnlyList<SoftwareKindOption> SoftwareKinds { get; }

    public const string HowToCollect =
        "安裝時勾選「定期匯出」並在公司設定檔設定 inventoryFolder，各台電腦登入時就會把電腦配備、軟體清單與瀏覽器擴充功能存到這個資料夾；" +
        "也可以用派送工具或排程執行 PcMaintenance.exe --export-system \"\\\\伺服器\\共用\\電腦盤點\\\"。同一台電腦有多個檔案時只看最新的一份。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder))]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(OpenFolderCommand))]
    private string _folder = "";

    [ObservableProperty]
    private bool _onlyAttention;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(ExportCommand), nameof(ChooseFolderCommand), nameof(ExportSoftwareCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string _softwareSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private FleetRow? _selectedRow;

    [ObservableProperty]
    private string _softwareSearch = "";

    [ObservableProperty]
    private SoftwareKindOption _selectedSoftwareKind;

    [ObservableProperty]
    private bool _onlyMixedVersions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallsTitle))]
    private FleetSoftwareRow? _selectedSoftware;

    public bool HasFolder => Folder.Length > 0;
    public bool HasComputers => Rows.Count > 0;

    public string DetailText => SelectedRow is not { } row ? "" :
        $"{row.Name}：{row.ConcernText}" + (row.Computer.Trend is { } trend ? $"　磁碟用量：{trend}" : "") + $"　（{Path.GetFileName(row.Computer.SourceFile)}）";

    public string InstallsTitle => SelectedSoftware is not { } s ? "選取左邊的軟體，這裡會列出裝在哪幾台電腦。" : $"{s.Name}\n{s.Software.VersionsText}";

    partial void OnOnlyAttentionChanged(bool value) => RowsView.Refresh();
    partial void OnSoftwareSearchChanged(string value) => SoftwareView.Refresh();
    partial void OnSelectedSoftwareKindChanged(SoftwareKindOption value) => SoftwareView.Refresh();
    partial void OnOnlyMixedVersionsChanged(bool value) => SoftwareView.Refresh();

    partial void OnSelectedSoftwareChanged(FleetSoftwareRow? value)
    {
        InstallRows.Clear();
        foreach (var install in value?.Software.Installs ?? []) InstallRows.Add(install);
    }

    private bool SoftwareShown(FleetSoftwareRow row) =>
        (SelectedSoftwareKind.Extensions is not { } extensions || row.IsExtension == extensions) &&
        (!OnlyMixedVersions || row.Outdated > 0) &&
        (string.IsNullOrWhiteSpace(SoftwareSearch) || row.Matches(SoftwareSearch.Trim()));

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
            OnPropertyChanged(nameof(HasComputers));
            SoftwareRows.Clear();
            foreach (var software in load.SoftwareList) SoftwareRows.Add(new FleetSoftwareRow(software));
            _loaded = true;
            SummaryText = load.Problem ?? (load.Computers.Count == 0
                ? $"這個資料夾裡沒有電腦配備檔（略過 {load.FilesSkipped} 個其他 CSV）。"
                : $"共 {load.Computers.Count} 台電腦，{load.Computers.Count(c => c.NeedsAttention)} 台需要注意（找到 {load.FilesRead} 個電腦配備檔，每台看最新的一份" +
                  (load.FilesSkipped > 0 ? $"；略過 {load.FilesSkipped} 個其他 CSV" : "") + $"；{now:HH:mm} 讀取）。");
            var programs = load.SoftwareList.Count(s => s.Kind == FleetSoftwareList.SoftwareKind);
            var extensions = load.SoftwareList.Count - programs;
            SoftwareSummary = load.SoftwareComputers == 0
                ? "資料夾裡沒有軟體清單。各台電腦勾選「定期匯出」，或執行 PcMaintenance.exe --export-software 與 --export-extensions 存到這個資料夾。"
                : $"{load.SoftwareComputers} 台電腦的軟體清單：{programs} 個軟體、{extensions} 個瀏覽器擴充功能；" +
                  $"{load.SoftwareList.Count(s => s.Outdated > 0)} 個有電腦還是舊版。選取一列看裝在哪幾台。";
            _services.Log.Info($"多台電腦：{SummaryText}");
        }
        finally
        {
            IsBusy = false;
            ExportCommand.NotifyCanExecuteChanged();
            ExportSoftwareCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (Dialogs.AskCsvPath(FleetSummary.Kind) is not { } path) return;
        Write(path, () => $"已匯出 {FleetSummary.WriteSummary(path, Rows.Select(r => r.Computer).ToList(), DateTime.Now)} 台電腦的彙整表到 {path}");
    }

    [RelayCommand(CanExecute = nameof(CanExportSoftware))]
    private void ExportSoftware()
    {
        if (Dialogs.AskCsvPath("軟體彙整") is not { } path) return;
        Write(path, () =>
        {
            CsvFile.Write(path, ["類型", "名稱", "發行者或來源", "電腦數", "最新版本", "版本分佈", "還是舊版的電腦"],
                SoftwareRows.Select(r => (IReadOnlyList<string>)[
                    r.Kind, r.Name, r.Software.Publisher, r.Computers.ToString(CultureInfo.InvariantCulture), r.Software.NewestVersion, r.Software.VersionsText,
                    string.Join("、", r.Software.Installs.Where(i => i.IsOutdated).Select(i => $"{i.Computer}（{i.Version}）"))]));
            return $"已匯出 {SoftwareRows.Count} 個軟體與擴充功能到 {path}";
        });
    }

    private void Write(string path, Func<string> write)
    {
        try
        {
            SummaryText = write();
            _services.Log.Info($"多台電腦匯出：{path}");
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
    private bool CanExportSoftware() => !IsBusy && SoftwareRows.Count > 0;
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
