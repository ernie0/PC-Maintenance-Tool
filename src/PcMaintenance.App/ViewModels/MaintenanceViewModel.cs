using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.DiskTools;
using PcMaintenance.Core.Junk;
using PcMaintenance.Core.Maintenance;
using PcMaintenance.Core.Shortcuts;
using PcMaintenance.Core.Startup;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.App.ViewModels;

public enum CheckState
{
    NotChecked,
    Checking,
    Good,
    Found,
    Advice,
    Fixing,
    Fixed,
    Partial,
}

public sealed record CheckOutcome(CheckState State, string Result, string? Detail = null);

public enum StepState
{
    Waiting,
    Running,
    Done,
    Found,
    Attention,
    Cancelled,
}

/// <summary>A line in the progress window: one check being scanned or fixed, or the restore point made before fixing.</summary>
public sealed partial class RunStep(string title, string glyph, Brush tint) : ObservableObject
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public Brush TintBrush { get; } = tint;

    [ObservableProperty]
    private StepState _state;

    [ObservableProperty]
    private string _text = "等待中";
}

/// <summary>One line of one-click maintenance: something to check, and for some of them a fix that can be undone.</summary>
public sealed partial class MaintenanceCheck : ObservableObject
{
    public MaintenanceCheck(string title, string description, string glyph, Color tint, bool canFix = false, bool selected = false)
    {
        Title = title;
        Description = description;
        Glyph = glyph;
        CanFix = canFix;
        _isSelected = canFix && selected;
        TintBrush = Frozen(new SolidColorBrush(tint));
        SoftBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x1F, tint.R, tint.G, tint.B)));
    }

    public string Title { get; }
    public string Description { get; }
    public string Glyph { get; }
    public bool CanFix { get; }
    public Brush TintBrush { get; }
    public Brush SoftBrush { get; }

    /// <summary>Where an advice item can be dealt with by hand: another page, or a Windows settings page.</summary>
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    public bool HasAction => Action is not null;

    public required Func<CancellationToken, Task<CheckOutcome>> Scan { get; init; }
    public Func<CancellationToken, Task<CheckOutcome>>? Fix { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private CheckState _state;

    [ObservableProperty]
    private string _resultText = "尚未檢查";

    [ObservableProperty]
    private string? _detailText;

    public void Show(CheckOutcome outcome)
    {
        State = outcome.State;
        ResultText = outcome.Result;
        DetailText = outcome.Detail;
    }

    [RelayCommand]
    private void RunAction() => Action?.Invoke();

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Glary-style one-click maintenance: one scan over the usual problems, one confirmation, one fix. Only things that can
/// be undone or that Windows rebuilds by itself are fixed; startup programs, disks, antivirus and restarts get advice.
/// </summary>
public sealed partial class MaintenanceViewModel : PageViewModel
{
    private const long WorthCleaningBytes = 1024 * 1024;
    private const int ManyStartupPrograms = 8;
    private const int LongUptimeDays = 14;
    private const string MakingRestorePoint = "Windows 正在建立還原點，第一次可能需要一兩分鐘";

    private readonly AppServices _services;
    private readonly Brush _restoreTint;
    private CancellationTokenSource? _cts;
    private IProgress<Activity>? _activity;
    private int _done;
    private int _current = -1;
    private List<JunkScanResult> _junk = [];
    private JunkScanResult? _traces;
    private IReadOnlyList<BrokenShortcut> _shortcuts = [];
    private IReadOnlyList<InstalledApp> _orphans = [];
    private long _freed;

    public MaintenanceViewModel(AppServices services)
    {
        _services = services;
        _restoreTint = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        _restoreTint.Freeze();
        Checks =
        [
            new("暫存檔與快取", "使用者暫存檔、瀏覽器快取、當機傾印檔與錯誤報告（與「垃圾檔清理」預設勾選的項目相同）",
                "", Color.FromRgb(0x05, 0x96, 0x69), canFix: true, selected: true)
            {
                Scan = ScanJunkAsync,
                Fix = CleanJunkAsync,
            },
            new("無效捷徑", "桌面與開始功能表中，目標檔案已經不存在的捷徑（移到資源回收筒）",
                "", Color.FromRgb(0x08, 0x91, 0xB2), canFix: true, selected: true)
            {
                Scan = ScanShortcutsAsync,
                Fix = RemoveShortcutsAsync,
            },
            new("無效的解除安裝項目", "程式已被手動刪除、只剩下「已安裝的應用程式」清單中的項目（刪除前會備份登錄檔）",
                "", Color.FromRgb(0x7C, 0x3A, 0xED), canFix: true, selected: true)
            {
                Scan = ScanOrphansAsync,
                Fix = RemoveOrphansAsync,
            },
            new("使用痕跡", "檔案總管與跳躍清單的「最近開啟的檔案」紀錄（只刪紀錄，不刪檔案）",
                "", Color.FromRgb(0x78, 0x71, 0x6C), canFix: true, selected: false)
            {
                Scan = ScanTracesAsync,
                Fix = CleanTracesAsync,
            },
            new("開機啟動程式", "會隨 Windows 一起啟動的程式；太多會讓開機變慢",
                "", Color.FromRgb(0xB4, 0x53, 0x09))
            {
                Scan = ScanStartupAsync,
                ActionText = "前往開機與背景",
                Action = () => NavigateRequested?.Invoke(typeof(StartupViewModel)),
            },
            new("磁碟空間與健康", "各磁碟的剩餘空間，以及磁碟本身回報的健康狀態",
                "", Color.FromRgb(0xEA, 0x58, 0x0C))
            {
                Scan = ScanDisksAsync,
                ActionText = "前往磁碟工具",
                Action = () => NavigateRequested?.Invoke(typeof(DiskToolsViewModel)),
            },
            new("防毒保護", "Windows 資訊安全中心回報的防毒軟體、即時保護與病毒碼狀態",
                "", Color.FromRgb(0x25, 0x63, 0xEB))
            {
                Scan = ScanSecurityAsync,
                ActionText = "開啟 Windows 安全性",
                Action = () => SystemActions.Open("windowsdefender:"),
            },
            new("重新開機", "Windows 更新是否在等待重新開機，以及電腦連續開機的天數",
                "", Color.FromRgb(0x47, 0x55, 0x69))
            {
                Scan = ScanRestartAsync,
                ActionText = "開啟 Windows Update",
                Action = () => SystemActions.Open("ms-settings:windowsupdate"),
            },
        ];
        foreach (var check in Checks)
        {
            check.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MaintenanceCheck.IsSelected) or nameof(MaintenanceCheck.State)) FixCommand.NotifyCanExecuteChanged();
            };
        }
        _detail = LastRunText();
    }

    /// <summary>Asks the main window to open another page, for advice items.</summary>
    public event Action<Type>? NavigateRequested;

    public override string Title => "一鍵維護";

    public override string Subtitle =>
        "一次檢查暫存檔、無效捷徑、開機程式、磁碟、防毒與更新狀態，確認清單後再一鍵處理。只會處理可以還原或 Windows 會自動重建的項目，其餘只提供建議。";

    public ObservableCollection<MaintenanceCheck> Checks { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(FixCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _headline = "按「一鍵掃描」檢查這台電腦";

    [ObservableProperty]
    private string _detail;

    /// <summary>Scan progress while scanning; afterwards the share of checks that came out fine.</summary>
    [ObservableProperty]
    private double _score;

    [ObservableProperty]
    private string _scoreText = "";

    [ObservableProperty]
    private bool _isHealthy;

    [ObservableProperty]
    private bool _createRestorePoint;

    [ObservableProperty]
    private string _progressText = "";

    /// <summary>What the progress window lists: the checks of the current scan or fix, and the restore point if one is made.</summary>
    public ObservableCollection<RunStep> RunSteps { get; } = [];

    [ObservableProperty]
    private RunStep? _currentStep;

    [ObservableProperty]
    private string _runTitle = "";

    [ObservableProperty]
    private string _runDetail = "";

    /// <summary>What the current step is doing right now, e.g. which junk category is being read and how much was found so far.</summary>
    [ObservableProperty]
    private string _activityText = "";

    [ObservableProperty]
    private double _runProgress;

    [ObservableProperty]
    private bool _isRunFinished;

    [ObservableProperty]
    private bool _isRunCancelled;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        var cts = Begin("正在檢查這台電腦", Checks.Select(StepFor));
        IsHealthy = false;
        try
        {
            foreach (var check in Checks) check.Show(new CheckOutcome(CheckState.NotChecked, "等待檢查"));
            foreach (var check in Checks)
            {
                StartStep($"第 {_done + 1}／{Checks.Count} 項：{check.Title}");
                check.Show(new CheckOutcome(CheckState.Checking, "檢查中…"));
                var outcome = await Guarded(check.Scan, cts.Token);
                check.Show(outcome);
                FinishStep(outcome);
            }
            Summarize();
            Finish("檢查完成", Headline);
        }
        catch (OperationCanceledException)
        {
            foreach (var check in Checks.Where(c => c.State is CheckState.Checking or CheckState.NotChecked))
                check.Show(new CheckOutcome(CheckState.NotChecked, "尚未檢查"));
            Headline = "已取消掃描";
            Score = 0;
            ScoreText = "";
            Finish("已取消", "已檢查完的項目保留結果，其餘沒有檢查。", cancelled: true);
        }
        finally
        {
            End(cts);
        }
    }

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFix))]
    private async Task FixAsync()
    {
        var targets = Checks.Where(IsFixTarget).ToList();
        if (targets.Count == 0) return;
        var lines = string.Join("\n", targets.Select(c => $"・{c.Title}：{c.ResultText}"));
        if (!Dialogs.Confirm($"即將處理：\n{lines}\n\n暫存檔與紀錄刪除後無法復原；捷徑會移到資源回收筒，登錄項目刪除前會先備份，可在「變更紀錄」還原。確定要處理嗎？",
                "確認一鍵修復"))
            return;

        var steps = targets.Select(StepFor).ToList();
        if (CreateRestorePoint) steps.Insert(0, new RunStep("建立系統還原點", "", _restoreTint));
        var cts = Begin("正在處理勾選的項目", steps);
        _freed = 0;
        try
        {
            if (CreateRestorePoint && !await MakeRestorePointAsync())
            {
                Summarize();
                Finish("已取消", "沒有建立還原點，也沒有處理任何項目。", cancelled: true);
                return;
            }
            var first = _done;
            foreach (var check in targets)
            {
                StartStep($"第 {_done - first + 1}／{targets.Count} 項：{check.Title}");
                check.Show(new CheckOutcome(CheckState.Fixing, "處理中…"));
                var outcome = await Guarded(check.Fix!, cts.Token);
                check.Show(outcome);
                FinishStep(outcome);
            }
            var fixedCount = targets.Count(c => c.State == CheckState.Fixed);
            MaintenanceHistory.Save(AppPaths.MaintenanceStateFile, new MaintenanceRun(DateTimeOffset.Now, fixedCount, _freed));
            _services.Log.Info($"一鍵維護完成：處理 {fixedCount}／{targets.Count} 項，釋放 {ByteSize.Format(_freed)}");
            Summarize();
            Finish("處理完成", $"處理 {fixedCount}／{targets.Count} 項，釋放 {ByteSize.Format(_freed)}。");
        }
        catch (OperationCanceledException)
        {
            foreach (var check in targets.Where(c => c.State == CheckState.Fixing)) check.Show(new CheckOutcome(CheckState.Partial, "已取消"));
            Summarize();
            Headline = "已取消，其餘項目未處理";
            Finish("已取消", "已處理完的項目保留結果，其餘沒有處理。", cancelled: true);
        }
        finally
        {
            End(cts);
        }
    }

    private bool CanFix() => !IsBusy && Checks.Any(IsFixTarget);

    private static bool IsFixTarget(MaintenanceCheck check) => check is { CanFix: true, IsSelected: true, State: CheckState.Found };

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task<bool> MakeRestorePointAsync()
    {
        StartStep("準備：建立系統還原點");
        // Without elevation Windows first asks for permission, and only then does the work start.
        ActivityText = _services.IsElevated ? MakingRestorePoint : "等待 Windows 詢問系統管理員權限，請按「是」";
        var result = await RestorePointLauncher.CreateAsync(_services.IsElevated, () => ActivityText = MakingRestorePoint);
        _services.Log.Info($"一鍵維護建立還原點：{result.Message}");
        switch (result.Outcome)
        {
            case RestorePointOutcome.Created:
                FinishStep(StepState.Done, "已建立");
                return true;
            case RestorePointOutcome.SkippedRecent:
                FinishStep(StepState.Done, "沿用 24 小時內的還原點");
                return true;
            default:
                FinishStep(StepState.Attention, "沒有建立");
                return Dialogs.Confirm($"{result.Message}\n\n要在沒有新還原點的情況下繼續處理嗎？", "沒有建立還原點");
        }
    }

    /// <summary>A failing check reports its error on its own line instead of stopping the others.</summary>
    private async Task<CheckOutcome> Guarded(Func<CancellationToken, Task<CheckOutcome>> step, CancellationToken ct)
    {
        try
        {
            return await step(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or COMException
                                       or Win32Exception or InvalidOperationException)
        {
            _services.Log.Error("一鍵維護項目失敗", ex);
            return new CheckOutcome(CheckState.Partial, $"無法完成：{UserMessage.For(ex)}");
        }
    }

    private void Summarize()
    {
        var fixable = Checks.Count(c => c.State == CheckState.Found);
        var advice = Checks.Count(c => c.State is CheckState.Advice or CheckState.Partial);
        var good = Checks.Count(c => c.State is CheckState.Good or CheckState.Fixed);
        Score = 100.0 * good / Checks.Count;
        IsHealthy = fixable + advice == 0;
        ScoreText = IsHealthy ? "良好" : $"{fixable + advice} 項";
        Headline = IsHealthy
            ? "這台電腦狀態良好"
            : fixable > 0
                ? $"發現 {fixable} 項可以一鍵處理" + (advice > 0 ? $"，另有 {advice} 項建議" : "")
                : $"有 {advice} 項建議，請看下方說明";
        Detail = LastRunText();
    }

    private static string LastRunText() => MaintenanceHistory.Load(AppPaths.MaintenanceStateFile) is { } last
        ? $"上次一鍵維護：{last.Time.LocalDateTime:yyyy/MM/dd HH:mm}，處理 {last.Fixed} 項，釋放 {ByteSize.Format(last.FreedBytes)}。"
        : "還沒有執行過一鍵維護。";

    private CancellationTokenSource Begin(string title, IEnumerable<RunStep> steps)
    {
        _cts = new CancellationTokenSource();
        RunSteps.Clear();
        foreach (var step in steps) RunSteps.Add(step);
        _done = 0;
        _current = -1;
        CurrentStep = null;
        RunTitle = title;
        RunDetail = "";
        ActivityText = "";
        IsRunFinished = false;
        IsRunCancelled = false;
        // Reports from the background work arrive on this thread a little later; a late one from a finished step is dropped.
        _activity = new Progress<Activity>(activity =>
        {
            if (activity.Step != _current) return;
            ActivityText = activity.Text;
            ShowProgress(activity.Fraction);
        });
        IsBusy = true;
        ShowProgress(0);
        Dialogs.ShowMaintenanceProgress(this);
        return _cts;
    }

    private void End(CancellationTokenSource cts)
    {
        // Only an unexpected error ends a run without a result; the window must still stop showing work in progress.
        if (!IsRunFinished) Finish("已停止", "發生未預期的錯誤，其餘項目沒有處理。", cancelled: true);
        _cts = null;
        _activity = null;
        cts.Dispose();
        IsBusy = false;
        ProgressText = "";
        FixCommand.NotifyCanExecuteChanged();
    }

    private RunStep StepFor(MaintenanceCheck check) => new(check.Title, check.Glyph, check.TintBrush);

    private void StartStep(string detail)
    {
        _current = _done;
        CurrentStep = RunSteps[_current];
        CurrentStep.State = StepState.Running;
        CurrentStep.Text = "進行中…";
        RunDetail = detail;
        ProgressText = detail;
        ActivityText = "";
        ShowProgress(0);
    }

    private void FinishStep(CheckOutcome outcome) => FinishStep(outcome.State switch
    {
        CheckState.Good or CheckState.Fixed => StepState.Done,
        CheckState.Found => StepState.Found,
        _ => StepState.Attention,
    }, outcome.Result);

    private void FinishStep(StepState state, string text)
    {
        RunSteps[_current].State = state;
        RunSteps[_current].Text = text;
        _done++;
        _current = -1;
        ShowProgress(0);
    }

    /// <summary>Finished steps plus the measured share of the current one (categories, folders, items); never a guess from time.</summary>
    private void ShowProgress(double fraction)
    {
        RunProgress = RunSteps.Count == 0 ? 0 : 100.0 * (_done + Math.Clamp(fraction, 0, 1)) / RunSteps.Count;
        Score = RunProgress;
        ScoreText = $"{_done}/{RunSteps.Count}";
    }

    private void Finish(string title, string detail, bool cancelled = false)
    {
        foreach (var step in RunSteps.Where(s => s.State is StepState.Waiting or StepState.Running))
        {
            step.Text = step.State == StepState.Running ? "已取消" : "未執行";
            step.State = StepState.Cancelled;
        }
        _current = -1;
        RunTitle = title;
        RunDetail = detail;
        ActivityText = "";
        if (!cancelled) RunProgress = 100;
        IsRunCancelled = cancelled;
        IsRunFinished = true;
    }

    /// <summary>Called from the background work of the current step.</summary>
    private void Report(string text, double fraction = 0) => _activity?.Report(new Activity(_current, text, fraction));

    private sealed record Activity(int Step, string Text, double Fraction);

    private Task<CheckOutcome> ScanJunkAsync(CancellationToken ct) => Task.Run(() =>
    {
        var scanner = new JunkScanner(_services.Settings);
        // The same choice as unattended cleaning: categories ticked by default, never the recycle bin.
        var categories = JunkCatalog.Create(_services.Settings, _services.Folders)
            .Where(c => c.DefaultSelected && !c.IsRecycleBin && (_services.IsElevated || !c.RequiresAdmin))
            .ToList();
        var results = new List<JunkScanResult>();
        long found = 0;
        foreach (var category in categories)
        {
            // Done categories are the measured share; inside one, only the files read so far are known, not how many remain.
            var share = (double)results.Count / categories.Count;
            var title = $"掃描 {category.Name}（第 {results.Count + 1}／{categories.Count} 類）";
            Report($"{title}\n目前可釋放 {ByteSize.Format(found)}", share);
            // A running browser blocks its cache only after the scan, so files are not called freeable before then.
            var result = scanner.Scan(category, _services.IsElevated, ct, progress =>
                Report($"{title}\n已讀取 {progress.FilesRead:N0} 個檔案，{progress.FilesFound:N0} 個符合清理條件", share));
            if (result.CanClean) found += result.TotalBytes;
            results.Add(result);
        }
        _junk = results;
        var cleanable = _junk.Where(s => s.CanClean).ToList();
        var bytes = cleanable.Sum(s => s.TotalBytes);
        var blocked = string.Join("；", _junk.Where(s => !s.CanClean && s.Warning is not null).Select(s => s.Warning));
        if (bytes < WorthCleaningBytes)
            return new CheckOutcome(CheckState.Good, "暫存檔很少，不需要清理", blocked.Length > 0 ? blocked : null);
        return new CheckOutcome(CheckState.Found, $"可釋放 {ByteSize.Format(bytes)}（{cleanable.Sum(s => s.ItemCount):N0} 個檔案）",
            blocked.Length > 0 ? blocked : null);
    }, ct);

    private Task<CheckOutcome> CleanJunkAsync(CancellationToken ct) => Task.Run(() =>
    {
        var cleaner = new JunkCleaner(_services.Settings, _services.Log);
        int deleted = 0, skipped = 0;
        long freed = 0;
        var scans = _junk.Where(s => s.CanClean && s.ItemCount > 0).ToList();
        for (var i = 0; i < scans.Count; i++)
        {
            var scan = scans[i];
            Report($"清理 {scan.Category.Name}（第 {i + 1}／{scans.Count} 類）\n已釋放 {ByteSize.Format(freed)}", (double)i / scans.Count);
            var result = cleaner.Clean(scan, ct);
            deleted += result.Deleted;
            skipped += result.TotalSkipped;
            freed += result.FreedBytes;
        }
        _freed += freed;
        return new CheckOutcome(CheckState.Fixed, $"已釋放 {ByteSize.Format(freed)}（{deleted:N0} 個檔案）",
            skipped > 0 ? $"略過 {skipped:N0} 個使用中、已變更或沒有權限的檔案" : null);
    }, ct);

    private Task<CheckOutcome> ScanShortcutsAsync(CancellationToken ct) => Task.Run(() =>
    {
        var folders = ShortcutScanner.DefaultFolders();
        var broken = new List<BrokenShortcut>();
        for (var i = 0; i < folders.Count; i++)
        {
            Report($"檢查「{folders[i].Name}」的捷徑（第 {i + 1}／{folders.Count} 個位置）", (double)i / folders.Count);
            broken.AddRange(ShortcutScanner.Scan([folders[i]], ct));
        }
        _shortcuts = broken;
        return _shortcuts.Count == 0
            ? new CheckOutcome(CheckState.Good, "沒有無效捷徑")
            : new CheckOutcome(CheckState.Found, $"{_shortcuts.Count} 個無效捷徑", Names(_shortcuts.Select(s => $"{s.Name}（{s.Folder.Name}）")));
    }, ct);

    private Task<CheckOutcome> RemoveShortcutsAsync(CancellationToken ct) => Task.Run(() =>
    {
        var remover = new ShortcutRemover(_services.Journal, _services.Log);
        var allowed = _shortcuts.Where(s => _services.IsElevated || !s.Folder.AllUsers).ToList();
        int done = 0, failed = 0;
        for (var i = 0; i < allowed.Count; i++)
        {
            Report($"移到資源回收筒：{allowed[i].Name}（{i + 1}／{allowed.Count}）", (double)i / allowed.Count);
            if (remover.Remove(allowed[i]).Success) done++;
            else failed++;
        }
        return Outcome(done, failed, _shortcuts.Count - allowed.Count, "個捷徑移到資源回收筒");
    }, ct);

    private Task<CheckOutcome> ScanOrphansAsync(CancellationToken ct) => Task.Run(() =>
    {
        Report("讀取已安裝的軟體清單");
        var apps = new InstalledAppProvider().GetApps();
        Report($"檢查 {apps.Count} 個軟體的解除安裝程式與程式資料夾", 0.5);
        // Fixed without a second look, so only entries whose program folder is gone too (or was never recorded).
        _orphans = apps
            .Where(a => UninstallCommand.IsUninstallerMissing(a) &&
                        (string.IsNullOrWhiteSpace(a.InstallLocation) || PathProbe.State(a.InstallLocation) == PathState.Missing))
            .ToList();
        return _orphans.Count == 0
            ? new CheckOutcome(CheckState.Good, "沒有無效的項目")
            : new CheckOutcome(CheckState.Found, $"{_orphans.Count} 個軟體只剩清單項目", Names(_orphans.Select(a => a.DisplayName)));
    }, ct);

    private Task<CheckOutcome> RemoveOrphansAsync(CancellationToken ct) => Task.Run(() =>
    {
        var remover = new LeftoverRemover(_services.Journal, AppPaths.BackupDir, _services.Log, _services.Folders);
        var allowed = _orphans.Where(a => _services.IsElevated || a.Hive != Microsoft.Win32.RegistryHive.LocalMachine).ToList();
        int done = 0, failed = 0;
        for (var i = 0; i < allowed.Count; i++)
        {
            var app = allowed[i];
            Report($"備份並移除清單項目：{app.DisplayName}（{i + 1}／{allowed.Count}）", (double)i / allowed.Count);
            var entry = new Leftover
            {
                Kind = LeftoverKind.RegistryKey,
                DisplayPath = app.RegistryDisplayPath,
                Hive = app.Hive,
                View = app.View,
                KeyPath = app.KeyPath,
                Reason = "解除安裝程式已不存在的登錄項目",
                DefaultSelected = true,
            };
            foreach (var outcome in remover.Remove(app, [entry]))
            {
                if (outcome.Success) done++;
                else failed++;
            }
        }
        return Outcome(done, failed, _orphans.Count - allowed.Count, "個項目已移除（已備份）");
    }, ct);

    private Task<CheckOutcome> ScanTracesAsync(CancellationToken ct) => Task.Run(() =>
    {
        var category = JunkCatalog.Create(_services.Settings, _services.Folders).FirstOrDefault(c => c.Id == "recent-items");
        if (category is null) return new CheckOutcome(CheckState.Good, "公司設定未開放清除");
        Report("讀取「最近開啟的檔案」與跳躍清單紀錄");
        _traces = new JunkScanner(_services.Settings).Scan(category, _services.IsElevated, ct);
        return _traces.ItemCount == 0
            ? new CheckOutcome(CheckState.Good, "沒有使用紀錄")
            : new CheckOutcome(CheckState.Found, $"{_traces.ItemCount:N0} 筆最近開啟的紀錄", "清除後，跳躍清單與「最近使用的檔案」會是空的");
    }, ct);

    private Task<CheckOutcome> CleanTracesAsync(CancellationToken ct) => Task.Run(() =>
    {
        if (_traces is null) return new CheckOutcome(CheckState.Good, "沒有使用紀錄");
        Report($"刪除 {_traces.ItemCount:N0} 筆最近開啟的紀錄（檔案本身不動）");
        var result = new JunkCleaner(_services.Settings, _services.Log).Clean(_traces, ct);
        _freed += result.FreedBytes;
        return new CheckOutcome(result.TotalSkipped == 0 ? CheckState.Fixed : CheckState.Partial, $"已清除 {result.Deleted:N0} 筆紀錄",
            result.TotalSkipped > 0 ? $"略過 {result.TotalSkipped:N0} 筆正在使用的紀錄" : null);
    }, ct);

    private Task<CheckOutcome> ScanStartupAsync(CancellationToken ct) => Task.Run(() =>
    {
        Report("讀取登錄 Run 與啟動資料夾中的程式");
        // Logon programs only: services and scheduled tasks are not what makes "too many things start with Windows".
        var manager = new StartupManager(_services.Journal, [new RunKeyProvider(), new StartupFolderProvider()], _services.Log);
        var enabled = manager.Load().Items.Where(i => i.Enabled && !i.IsMicrosoft).ToList();
        var names = Names(enabled.Select(i => i.Name));
        return enabled.Count >= ManyStartupPrograms
            ? new CheckOutcome(CheckState.Advice, $"有 {enabled.Count} 個程式隨開機啟動，可檢查是否都需要", names)
            : new CheckOutcome(CheckState.Good, enabled.Count == 0 ? "沒有其他程式隨開機啟動" : $"{enabled.Count} 個程式隨開機啟動，數量正常", names);
    }, ct);

    private Task<CheckOutcome> ScanDisksAsync(CancellationToken ct) => Task.Run(() =>
    {
        Report("向 Windows 讀取各磁碟的剩餘空間與健康狀態");
        var inventory = DiskInventory.Read(includeReliability: false);
        var unhealthy = inventory.Disks.Where(d => d.Health is "警告" or "不良").Select(d => $"{d.Model}：{d.Health}").ToList();
        var low = inventory.Volumes.Where(v => !v.IsRemovable && v.FreePercent < 10).Select(v => $"{v.Letter}: 只剩 {v.FreePercent:0}%").ToList();
        if (unhealthy.Count > 0) return new CheckOutcome(CheckState.Advice, "有磁碟回報健康狀態異常，請盡快備份資料", string.Join("；", unhealthy.Concat(low)));
        if (low.Count > 0) return new CheckOutcome(CheckState.Advice, "磁碟空間不足", string.Join("；", low) + "；可用「磁碟分析」找出佔空間的檔案");
        return new CheckOutcome(CheckState.Good, "空間充足、健康狀態正常");
    }, ct);

    private Task<CheckOutcome> ScanSecurityAsync(CancellationToken ct) => Task.Run(() =>
    {
        Report("向 Windows 資訊安全中心查詢防毒軟體");
        var products = SecurityStatus.ReadAntivirus();
        if (products is null) return new CheckOutcome(CheckState.Advice, "無法讀取防毒軟體狀態");
        if (products.Count == 0) return new CheckOutcome(CheckState.Advice, "找不到防毒軟體");
        var protecting = products.Where(p => p.Enabled && p.UpToDate).Select(p => p.Name).Distinct().ToList();
        if (protecting.Count > 0) return new CheckOutcome(CheckState.Good, $"{string.Join("、", protecting)} 正在保護");
        return products.Any(p => p.Enabled)
            ? new CheckOutcome(CheckState.Advice, "防毒軟體的病毒碼不是最新")
            : new CheckOutcome(CheckState.Advice, "防毒軟體的即時保護沒有開啟");
    }, ct);

    private Task<CheckOutcome> ScanRestartAsync(CancellationToken ct) => Task.Run(() =>
    {
        Report("檢查 Windows 更新是否等待重新開機，以及連續開機的時間");
        if (RestartStatus.IsUpdateRestartPending()) return new CheckOutcome(CheckState.Advice, "Windows 更新需要重新開機才能完成");
        var days = (int)RestartStatus.Uptime.TotalDays;
        return days >= LongUptimeDays
            ? new CheckOutcome(CheckState.Advice, $"已連續開機 {days} 天，建議重新開機", "「關機」在快速啟動開啟時不會重新開始 Windows，請使用「重新啟動」")
            : new CheckOutcome(CheckState.Good, "不需要重新開機");
    }, ct);

    private static CheckOutcome Outcome(int done, int failed, int needAdmin, string what)
    {
        var notes = new List<string>();
        if (failed > 0) notes.Add($"{failed} 個無法處理（詳細原因在記錄檔）");
        if (needAdmin > 0) notes.Add($"{needAdmin} 個屬於所有使用者，需要系統管理員權限");
        return new CheckOutcome(failed + needAdmin == 0 ? CheckState.Fixed : CheckState.Partial, $"{done} {what}", notes.Count > 0 ? string.Join("；", notes) : null);
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 4 ? string.Join("、", list) : $"{string.Join("、", list.Take(4))} 等 {list.Count} 個";
    }
}
