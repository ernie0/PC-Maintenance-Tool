using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;

namespace PcMaintenance.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public abstract string Subtitle { get; }

    public virtual void OnNavigatedTo()
    {
    }

    public virtual void OnNavigatedFrom()
    {
    }
}

/// <summary>A page in the side bar, with its own colour and the motion its icon plays on hover and when the page opens.</summary>
public sealed record NavItem(string Title, string Glyph, MotionKind Motion, Color Tint, PageViewModel Page)
{
    public Brush TintBrush { get; } = Frozen(new SolidColorBrush(Tint));
    public Brush SoftBrush { get; } = Frozen(new SolidColorBrush(Color.FromArgb(0x1F, Tint.R, Tint.G, Tint.B)));
    public Brush TileBrush { get; } = Frozen(new LinearGradientBrush(Lighter(Tint), Tint, 45));

    private static Color Lighter(Color color) =>
        Color.FromRgb((byte)(color.R + (255 - color.R) * 0.3), (byte)(color.G + (255 - color.G) * 0.3), (byte)(color.B + (255 - color.B) * 0.3));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services)
    {
        Services = services;
        var maintenance = new MaintenanceViewModel(services);
        Pages =
        [
            new NavItem("一鍵維護", "", MotionKind.Swing, Color.FromRgb(0x25, 0x63, 0xEB), maintenance),
            new NavItem("垃圾檔清理", "", MotionKind.Wiggle, Color.FromRgb(0x05, 0x96, 0x69), new JunkViewModel(services)),
            new NavItem("開機與背景", "", MotionKind.Pulse, Color.FromRgb(0xB4, 0x53, 0x09), new StartupViewModel(services)),
            new NavItem("工作管理員", "", MotionKind.Grow, Color.FromRgb(0x03, 0x69, 0xA1), new TaskManagerViewModel(services)),
            new NavItem("軟體解除安裝", "", MotionKind.Bounce, Color.FromRgb(0x7C, 0x3A, 0xED), new UninstallViewModel(services)),
            new NavItem("磁碟分析", "", MotionKind.Turn, Color.FromRgb(0x08, 0x91, 0xB2), new DiskViewModel(services)),
            new NavItem("磁碟工具", "", MotionKind.Twist, Color.FromRgb(0xEA, 0x58, 0x0C), new DiskToolsViewModel(services)),
            new NavItem("記憶體", "", MotionKind.Squeeze, Color.FromRgb(0xDB, 0x27, 0x77), new MemoryViewModel(services)),
            new NavItem("系統調校", "", MotionKind.Nudge, Color.FromRgb(0x4D, 0x7C, 0x0F), new TweaksViewModel(services)),
            new NavItem("系統工具", "", MotionKind.Bounce, Color.FromRgb(0x47, 0x55, 0x69), new ToolsViewModel(services)),
            new NavItem("電腦資訊", "", MotionKind.Pop, Color.FromRgb(0x4F, 0x46, 0xE5), new SystemInfoViewModel(services)),
            new NavItem("變更紀錄", "", MotionKind.Rewind, Color.FromRgb(0x78, 0x71, 0x6C), new JournalViewModel(services)),
        ];
        maintenance.NavigateRequested += pageType => SelectedPage = Pages.First(p => p.Page.GetType() == pageType);
        _selectedPage = Pages[0];
    }

    public AppServices Services { get; }
    public IReadOnlyList<NavItem> Pages { get; }

    [ObservableProperty]
    private NavItem _selectedPage;

    public bool ShowElevationBanner => !Services.IsElevated;
    public string ElevationText => Services.IsElevated ? "系統管理員權限：已啟用" : "系統管理員權限：未啟用";
    public string ElevationGlyph => Services.IsElevated ? "" : "";
    public string? SettingsWarning => Services.SettingsWarning;
    public string SettingsText => Services.SettingsSource is null ? "設定：預設值" : "設定：公司設定檔";
    public string VersionText => $"版本 {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";

    partial void OnSelectedPageChanged(NavItem? oldValue, NavItem newValue)
    {
        oldValue?.Page.OnNavigatedFrom();
        newValue.Page.OnNavigatedTo();
    }

    [RelayCommand]
    private void RestartElevated()
    {
        if (Elevation.RestartElevated()) Application.Current.Shutdown();
    }
}
