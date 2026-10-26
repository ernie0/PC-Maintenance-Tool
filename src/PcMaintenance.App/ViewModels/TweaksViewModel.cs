using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Tweaks;

namespace PcMaintenance.App.ViewModels;

public sealed record TweakSection(string Title, string Glyph, IReadOnlyList<TweakRow> Rows);

public sealed partial class TweakRow : ObservableObject
{
    private readonly Action<TweakRow, bool> _changed;
    private bool _loading;

    public TweakRow(Tweak tweak, bool isElevated, Action<TweakRow, bool> changed)
    {
        Tweak = tweak;
        _changed = changed;
        IsAvailable = TweakEngine.IsAvailable(tweak);
        CanChange = IsAvailable && (isElevated || !tweak.RequiresAdmin);
        Note = !IsAvailable ? "這個版本的 Windows 沒有這項設定" : !CanChange ? "需要系統管理員權限" : tweak.TakesEffect;
    }

    public Tweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public bool IsAvailable { get; }
    public bool CanChange { get; }
    public string Note { get; }

    [ObservableProperty]
    private bool _isOn;

    /// <summary>Shows the state read from Windows without treating it as the user switching it.</summary>
    public void Load(bool on)
    {
        _loading = true;
        IsOn = on;
        _loading = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_loading) _changed(this, value);
    }
}

/// <summary>
/// Wintoys-style tweaks: Windows settings for performance, File Explorer and privacy, each switched through the same
/// value Windows uses and recorded in the change journal, so every switch can be undone.
/// </summary>
public sealed partial class TweaksViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly TweakEngine _engine;
    private readonly PowerPlans _powerPlans;
    private bool _loadingPlans;

    public TweaksViewModel(AppServices services)
    {
        _services = services;
        _engine = new TweakEngine(services.Journal, services.Log);
        _powerPlans = new PowerPlans(services.Journal, services.Log);
        TweakRow Row(Tweak t) => new(t, services.IsElevated, OnTweakChanged);
        Sections =
        [
            new("效能", "", TweakCatalog.All.Where(t => t.Group == TweakGroup.Performance).Select(Row).ToList()),
            new("檔案總管與開始功能表", "", TweakCatalog.All.Where(t => t.Group == TweakGroup.Explorer).Select(Row).ToList()),
            new("隱私", "", TweakCatalog.All.Where(t => t.Group == TweakGroup.Privacy).Select(Row).ToList()),
        ];
    }

    public override string Title => "系統調校";

    public override string Subtitle =>
        "常用的 Windows 效能、檔案總管與隱私設定，一個開關就能切換；每次切換都記在「變更紀錄」，可以還原。標示需要系統管理員的項目，請先以系統管理員重新啟動。";

    public IReadOnlyList<TweakSection> Sections { get; }
    public ObservableCollection<PowerPlan> Plans { get; } = [];

    [ObservableProperty]
    private PowerPlan? _selectedPlan;

    [ObservableProperty]
    private bool _canAddUltimate;

    [ObservableProperty]
    private bool _needsExplorerRestart;

    [ObservableProperty]
    private string _statusText = "";

    public override void OnNavigatedTo()
    {
        foreach (var row in Sections.SelectMany(s => s.Rows)) row.Load(row.IsAvailable && TweakEngine.IsOn(row.Tweak));
        _ = LoadPlansAsync();
    }

    private void OnTweakChanged(TweakRow row, bool on)
    {
        try
        {
            _engine.Apply(row.Tweak, on);
            StatusText = $"已{(on ? "開啟" : "關閉")}：{row.Name}（{row.Tweak.TakesEffect}）。";
            if (row.Tweak.NeedsExplorerRestart) NeedsExplorerRestart = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            row.Load(!on);
            Dialogs.Error($"無法變更「{row.Name}」：{UserMessage.For(ex)}");
        }
    }

    partial void OnSelectedPlanChanged(PowerPlan? oldValue, PowerPlan? newValue)
    {
        if (_loadingPlans || newValue is null || newValue.IsActive) return;
        _ = ApplyPlanAsync(newValue, oldValue);
    }

    private async Task ApplyPlanAsync(PowerPlan plan, PowerPlan? previous)
    {
        if (await _powerPlans.SetActiveAsync(plan, previous)) StatusText = $"電源計畫已改為「{plan.Name}」。";
        else Dialogs.Error($"無法切換到「{plan.Name}」。這台電腦可能不支援這個電源計畫。");
        await LoadPlansAsync();
    }

    [RelayCommand]
    private async Task AddUltimateAsync()
    {
        StatusText = await PowerPlans.AddUltimateAsync()
            ? "已加入「終極效能」電源計畫，可以在上方選擇。"
            : "這台電腦的 Windows 無法加入「終極效能」電源計畫。";
        await LoadPlansAsync();
    }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        if (!Dialogs.Confirm("檔案總管會關閉再重新開啟，已開啟的資料夾視窗會關閉。確定要重新啟動嗎？", "重新啟動檔案總管")) return;
        await SystemActions.RestartExplorerAsync();
        NeedsExplorerRestart = false;
        StatusText = "已重新啟動檔案總管。";
    }

    private async Task LoadPlansAsync()
    {
        var plans = await PowerPlans.ListAsync();
        _loadingPlans = true;
        Plans.Clear();
        foreach (var plan in plans) Plans.Add(plan);
        SelectedPlan = Plans.FirstOrDefault(p => p.IsActive);
        _loadingPlans = false;
        // An added Ultimate plan gets a new GUID of its own, so only its (localized) name tells it apart.
        CanAddUltimate = plans.Count > 0 && !plans.Any(p => p.Name.Contains("終極") || p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
    }
}
