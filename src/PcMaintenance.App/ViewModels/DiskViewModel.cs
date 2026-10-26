using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.DiskAnalysis;

namespace PcMaintenance.App.ViewModels;

public sealed partial class DiskViewModel : PageViewModel
{
    private readonly AppServices _services;
    private CancellationTokenSource? _analyzeCts;
    private CancellationTokenSource? _duplicateCts;
    private CancellationTokenSource? _emptyCts;
    private IReadOnlyList<DuplicateGroup> _groups = [];

    public DiskViewModel(AppServices services)
    {
        _services = services;
        Drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
        _analyzePath = Drives.FirstOrDefault() ?? @"C:\";
        _duplicatePath = services.Folders.UserProfile;
        _minSizeText = services.Settings.DuplicateMinSizeMB.ToString(CultureInfo.InvariantCulture);
        // Documents rather than the whole profile: program folders there are full of empty folders their programs expect.
        _emptyFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public override string Title => "磁碟分析";
    public override string Subtitle => "找出佔用空間的資料夾、大檔案、重複檔案與空資料夾。要移除的項目都會移到資源回收筒，重複檔每組至少保留一個。";

    public IReadOnlyList<string> Drives { get; }
    public ObservableCollection<FolderNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<LargeFileRow> LargestFiles { get; } = [];
    public ObservableCollection<FileKindRow> KindTotals { get; } = [];
    public ObservableCollection<DuplicateGroupRow> DuplicateGroups { get; } = [];
    public ObservableCollection<EmptyFolderRow> EmptyFolders { get; } = [];

    [ObservableProperty]
    private string _emptyFolderPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindEmptyFoldersCommand), nameof(RecycleEmptyFoldersCommand))]
    private bool _isFindingEmptyFolders;

    [ObservableProperty]
    private string _emptyFolderStatus = "選擇資料夾後按「搜尋空資料夾」。隱藏、系統與程式設定用的資料夾不會列出。";

    [ObservableProperty]
    private string _emptySelectionText = "";

    [ObservableProperty]
    private string _analyzePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _analyzeStatus = "選擇磁碟或資料夾後按「開始分析」。";

    [ObservableProperty]
    private LargeFileRow? _selectedLargeFile;

    [ObservableProperty]
    private string _duplicatePath;

    [ObservableProperty]
    private string _minSizeText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindDuplicatesCommand), nameof(RecycleDuplicatesCommand))]
    private bool _isFindingDuplicates;

    [ObservableProperty]
    private string _duplicateStatus = "選擇資料夾後按「搜尋重複檔」。";

    [ObservableProperty]
    private string _selectionText = "";

    [RelayCommand]
    private void BrowseAnalyzePath()
    {
        if (PickFolder("選擇要分析的資料夾", AnalyzePath) is { } folder) AnalyzePath = folder;
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        var path = AnalyzePath.Trim();
        if (!Directory.Exists(path))
        {
            Dialogs.Error($"找不到資料夾：{path}");
            return;
        }
        _analyzeCts = new CancellationTokenSource();
        IsAnalyzing = true;
        RootNodes.Clear();
        LargestFiles.Clear();
        KindTotals.Clear();
        var progress = new Progress<ScanProgress>(p =>
            AnalyzeStatus = $"已掃描 {p.Files:N0} 個檔案（{ByteSize.Format(p.Bytes)}）：{p.CurrentPath}");
        try
        {
            var token = _analyzeCts.Token;
            var result = await Task.Run(() => new FolderSizeScanner().Scan(path, progress, token), token);
            RootNodes.Add(new FolderNodeViewModel(result.Root, result.Root.Size) { IsExpanded = true });
            foreach (var file in result.LargestFiles) LargestFiles.Add(new LargeFileRow(file));
            foreach (var kind in result.Kinds.Where(k => k.Bytes > 0)) KindTotals.Add(new FileKindRow(kind, result.Root.Size));
            AnalyzeStatus = $"完成：共 {ByteSize.Format(result.Root.Size)}、{result.TotalFiles:N0} 個檔案，耗時 {result.Elapsed:mm\\:ss}" +
                            (result.AccessDeniedFolders > 0 ? $"；{result.AccessDeniedFolders:N0} 個資料夾沒有讀取權限，未計入" : "");
        }
        catch (OperationCanceledException)
        {
            AnalyzeStatus = "已取消分析。";
        }
        finally
        {
            _analyzeCts.Dispose();
            _analyzeCts = null;
            IsAnalyzing = false;
        }
    }

    private bool CanAnalyze() => !IsAnalyzing;

    [RelayCommand]
    private void CancelAnalyze() => _analyzeCts?.Cancel();

    [RelayCommand]
    private void ShowLargeFile(LargeFileRow? row)
    {
        if ((row ?? SelectedLargeFile) is { } file) Shell.ShowInExplorer(file.Path);
    }

    /// <summary>Opens the folder selected in the tree, or the analysed folder when nothing is selected.</summary>
    [RelayCommand]
    private void ShowFolder(FolderNodeViewModel? node)
    {
        if ((node?.Path ?? RootNodes.FirstOrDefault()?.Path) is { } path) Shell.ShowInExplorer(path);
    }

    [RelayCommand]
    private void ShowDuplicate(DuplicateFileRow? row)
    {
        if (row is not null) Shell.ShowInExplorer(row.File.Path);
    }

    [RelayCommand]
    private void BrowseDuplicatePath()
    {
        if (PickFolder("選擇要搜尋重複檔的資料夾", DuplicatePath) is { } folder) DuplicatePath = folder;
    }

    [RelayCommand(CanExecute = nameof(CanFindDuplicates))]
    private async Task FindDuplicatesAsync()
    {
        var path = DuplicatePath.Trim();
        if (!Directory.Exists(path))
        {
            Dialogs.Error($"找不到資料夾：{path}");
            return;
        }
        if (!double.TryParse(MinSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minMb) || minMb < 0)
        {
            Dialogs.Error("最小檔案大小請輸入 0 以上的數字（MB）。");
            return;
        }

        _duplicateCts = new CancellationTokenSource();
        IsFindingDuplicates = true;
        DuplicateGroups.Clear();
        _groups = [];
        SelectionText = "";
        var progress = new Progress<DuplicateProgress>(p =>
            DuplicateStatus = p.Total > 0 ? $"{p.Stage}：{p.Done:N0}／{p.Total:N0}" : $"{p.Stage}：已找到 {p.Done:N0} 個檔案");
        try
        {
            var token = _duplicateCts.Token;
            var minBytes = (long)(minMb * 1024 * 1024);
            _groups = await Task.Run(() => new DuplicateFinder(_services.Settings).Find([path], minBytes, progress, token), token);
            var index = 0;
            foreach (var group in _groups)
            {
                var groupRow = new DuplicateGroupRow(group, ++index, path);
                foreach (var row in groupRow.Files)
                {
                    row.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(DuplicateFileRow.IsSelected)) UpdateSelection();
                    };
                }
                DuplicateGroups.Add(groupRow);
            }
            DuplicateStatus = _groups.Count == 0
                ? "沒有找到重複檔案。"
                : $"找到 {_groups.Count:N0} 組重複檔案，共可節省 {ByteSize.Format(_groups.Sum(g => g.WastedBytes))}。";
        }
        catch (OperationCanceledException)
        {
            DuplicateStatus = "已取消搜尋。";
        }
        finally
        {
            _duplicateCts.Dispose();
            _duplicateCts = null;
            IsFindingDuplicates = false;
        }
        UpdateSelection();
    }

    private bool CanFindDuplicates() => !IsFindingDuplicates;

    [RelayCommand]
    private void CancelDuplicates() => _duplicateCts?.Cancel();

    [RelayCommand]
    private void SelectAllButNewest()
    {
        foreach (var group in DuplicateGroups)
        {
            var newest = group.Files.MaxBy(r => r.File.LastWriteUtc);
            foreach (var row in group.Files) row.IsSelected = !ReferenceEquals(row, newest);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRecycleDuplicates))]
    private async Task RecycleDuplicatesAsync()
    {
        var selected = SelectedDuplicates().ToList();
        var paths = new HashSet<string>(selected.Select(r => r.File.Path), StringComparer.OrdinalIgnoreCase);
        try
        {
            DuplicateRemoval.EnsureOneKept(_groups, paths);
        }
        catch (InvalidOperationException ex)
        {
            Dialogs.Error(ex.Message);
            return;
        }
        var bytes = selected.Sum(r => r.File.Size);
        if (!Dialogs.Confirm($"將把 {selected.Count:N0} 個重複檔案（約 {ByteSize.Format(bytes)}）移到資源回收筒。每組至少會保留一個檔案。確定嗎？", "確認移除重複檔"))
            return;

        IsFindingDuplicates = true;
        try
        {
            var result = await Task.Run(() => DuplicateRemoval.Recycle(_groups, paths, _services.Settings, _services.Log));
            var failed = new HashSet<string>(result.Failed, StringComparer.OrdinalIgnoreCase);
            foreach (var group in DuplicateGroups.ToList())
            {
                foreach (var row in group.Files.Where(r => r.IsSelected && !failed.Contains(r.File.Path)).ToList()) group.Files.Remove(row);
                // A single remaining copy is no longer a duplicate.
                if (group.Files.Count < 2) DuplicateGroups.Remove(group);
            }
            DuplicateStatus = $"已移到資源回收筒 {result.Recycled:N0} 個檔案，釋放 {ByteSize.Format(result.FreedBytes)}" +
                              (failed.Count > 0 ? $"；{failed.Count:N0} 個無法移除（使用中、已變更或在排除路徑中）。" : "。");
        }
        finally
        {
            IsFindingDuplicates = false;
        }
        UpdateSelection();
    }

    private bool CanRecycleDuplicates() => !IsFindingDuplicates && SelectedDuplicates().Any();

    private IEnumerable<DuplicateFileRow> SelectedDuplicates() => DuplicateGroups.SelectMany(g => g.Files).Where(r => r.IsSelected);

    private void UpdateSelection()
    {
        var selected = SelectedDuplicates().ToList();
        SelectionText = selected.Count == 0 ? "" : $"已勾選 {selected.Count:N0} 個，可釋放 {ByteSize.Format(selected.Sum(r => r.File.Size))}";
        RecycleDuplicatesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void BrowseEmptyFolderPath()
    {
        if (PickFolder("選擇要搜尋空資料夾的位置", EmptyFolderPath) is { } folder) EmptyFolderPath = folder;
    }

    [RelayCommand(CanExecute = nameof(CanFindEmptyFolders))]
    private async Task FindEmptyFoldersAsync()
    {
        var path = EmptyFolderPath.Trim();
        if (!Directory.Exists(path))
        {
            Dialogs.Error($"找不到資料夾：{path}");
            return;
        }
        _emptyCts = new CancellationTokenSource();
        IsFindingEmptyFolders = true;
        EmptyFolders.Clear();
        EmptyFolderStatus = "搜尋中…";
        try
        {
            var token = _emptyCts.Token;
            var found = await Task.Run(() => EmptyFolderFinder.Find(path, _services.Folders, token), token);
            foreach (var folder in found)
            {
                var row = new EmptyFolderRow(folder, path);
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(EmptyFolderRow.IsSelected)) UpdateEmptySelection();
                };
                EmptyFolders.Add(row);
            }
            EmptyFolderStatus = found.Count == 0 ? "沒有找到空資料夾。" : $"找到 {found.Count:N0} 個空資料夾，請勾選要移除的項目。";
        }
        catch (OperationCanceledException)
        {
            EmptyFolderStatus = "已取消搜尋。";
        }
        finally
        {
            _emptyCts.Dispose();
            _emptyCts = null;
            IsFindingEmptyFolders = false;
        }
        UpdateEmptySelection();
    }

    private bool CanFindEmptyFolders() => !IsFindingEmptyFolders;

    [RelayCommand]
    private void CancelEmptyFolders() => _emptyCts?.Cancel();

    [RelayCommand]
    private void SelectAllEmptyFolders()
    {
        var select = EmptyFolders.Any(r => !r.IsSelected);
        foreach (var row in EmptyFolders) row.IsSelected = select;
    }

    [RelayCommand]
    private void ShowEmptyFolder(EmptyFolderRow? row)
    {
        if (row is not null) Shell.Select(row.Folder.Path);
    }

    [RelayCommand(CanExecute = nameof(CanRecycleEmptyFolders))]
    private async Task RecycleEmptyFoldersAsync()
    {
        var selected = EmptyFolders.Where(r => r.IsSelected).ToList();
        if (!Dialogs.Confirm($"將把 {selected.Count:N0} 個空資料夾移到資源回收筒（移除前會再確認裡面沒有檔案）。確定嗎？", "確認移除空資料夾"))
            return;
        IsFindingEmptyFolders = true;
        try
        {
            var remover = new EmptyFolderRemover(_services.Journal, _services.Folders, _services.Log);
            var results = await Task.Run(() => selected.Select(r => remover.Remove(r.Folder)).ToList());
            foreach (var row in selected.Where(r => results.First(x => ReferenceEquals(x.Item, r.Folder)).Success)) EmptyFolders.Remove(row);
            var failed = results.Count(r => !r.Success);
            EmptyFolderStatus = $"已移到資源回收筒 {results.Count - failed:N0} 個資料夾" +
                                (failed > 0 ? $"；{failed:N0} 個沒有移除（裡面已有檔案或無法移動）。" : "。");
        }
        finally
        {
            IsFindingEmptyFolders = false;
        }
        UpdateEmptySelection();
    }

    private bool CanRecycleEmptyFolders() => !IsFindingEmptyFolders && EmptyFolders.Any(r => r.IsSelected);

    private void UpdateEmptySelection()
    {
        var count = EmptyFolders.Count(r => r.IsSelected);
        EmptySelectionText = count == 0 ? "" : $"已勾選 {count:N0} 個";
        RecycleEmptyFoldersCommand.NotifyCanExecuteChanged();
    }

    private static string? PickFolder(string title, string initial)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public sealed partial class FolderNodeViewModel : ObservableObject
{
    private const int MaxChildren = 300;
    private static readonly FolderNodeViewModel Placeholder = new("…");
    private readonly FolderNode? _node;
    private readonly long _parentSize;

    public FolderNodeViewModel(FolderNode node, long parentSize)
    {
        _node = node;
        _parentSize = parentSize;
        Name = node.Name;
        if (node.Children.Count > 0) Children.Add(Placeholder);
    }

    private FolderNodeViewModel(string label) => Name = label;

    public string Name { get; }
    public string? Path => _node?.Path;
    public string SizeText => _node is null ? "" : ByteSize.Format(_node.Size);
    public double Percent => _node is null || _parentSize <= 0 ? 0 : 100.0 * _node.Size / _parentSize;
    public string PercentText => _node is null ? "" : $"{Percent:0.#}%";

    public string Tooltip => _node is null
        ? Name
        : $"{_node.Path}\n{SizeText}，{_node.FileCount:N0} 個檔案" + (_node.AccessDenied ? "\n部分內容沒有讀取權限，未計入" : "");

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _node is null || Children.Count != 1 || !ReferenceEquals(Children[0], Placeholder)) return;
        Children.Clear();
        foreach (var child in _node.Children.Take(MaxChildren)) Children.Add(new FolderNodeViewModel(child, _node.Size));
        var rest = _node.Children.Count - MaxChildren;
        if (rest > 0) Children.Add(new FolderNodeViewModel($"其餘 {rest:N0} 個較小的資料夾"));
    }
}

public sealed class FileKindRow(FileKindTotal total, long allBytes)
{
    public string Name => total.Name;
    public string SizeText => ByteSize.Format(total.Bytes);
    public string PercentText => allBytes > 0 ? $"{100.0 * total.Bytes / allBytes:0.#}%" : "";
    public string Tooltip => $"{total.Files:N0} 個檔案";
}

public sealed class LargeFileRow(LargeFile file)
{
    public string Path => file.Path;
    public string Name => System.IO.Path.GetFileName(file.Path);
    public string Folder => System.IO.Path.GetDirectoryName(file.Path) ?? "";
    public long Size => file.Size;
    public string SizeText => ByteSize.Format(file.Size);
    public string ModifiedText => file.LastWriteUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}

public sealed class DuplicateGroupRow : ObservableObject
{
    private readonly DuplicateGroup _group;
    private readonly int _index;

    public DuplicateGroupRow(DuplicateGroup group, int index, string searchRoot)
    {
        _group = group;
        _index = index;
        Files = new(group.Files.OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase).Select(f => new DuplicateFileRow(f, searchRoot)));
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ObservableCollection<DuplicateFileRow> Files { get; }

    public string Title =>
        $"第 {_index} 組｜{Files.Count} 個相同檔案，每個 {ByteSize.Format(_group.Size)}，可節省 {ByteSize.Format(_group.Size * (Files.Count - 1))}";
}

public sealed partial class EmptyFolderRow(EmptyFolder folder, string searchRoot) : ObservableObject
{
    public EmptyFolder Folder { get; } = folder;
    public string RelativePath { get; } = Path.GetRelativePath(searchRoot, folder.Path);
    public string SubfoldersText => Folder.Subfolders == 0 ? "" : $"內含 {Folder.Subfolders} 個空資料夾";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class DuplicateFileRow(DuplicateFile file, string searchRoot) : ObservableObject
{
    public DuplicateFile File { get; } = file;
    public string Name => Path.GetFileName(File.Path);

    // Relative to the searched folder: copies differ at the end of the path, which a trimmed full path hides.
    public string Folder { get; } = RelativeFolder(searchRoot, file.Path);
    public string ModifiedText => File.LastWriteUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string SizeText => ByteSize.Format(File.Size);

    [ObservableProperty]
    private bool _isSelected;

    private static string RelativeFolder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root);
        return relative == "." ? "（搜尋位置）" : relative;
    }
}
