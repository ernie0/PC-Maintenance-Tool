using Microsoft.Win32;

namespace PcMaintenance.Core.Tweaks;

/// <summary>
/// The Windows settings offered on the tweaks page: each one is a setting Windows itself exposes (in Settings, the
/// Control Panel or Group Policy), switched through the same registry values, and can be switched back.
/// </summary>
public static class TweakCatalog
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ClassicMenuClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const int Windows11 = 22000;
    private const int Windows11Version23H2 = 22631;

    public static IReadOnlyList<Tweak> All { get; } =
    [
        new()
        {
            Id = "game-mode",
            Group = TweakGroup.Performance,
            Name = "遊戲模式",
            Description = "玩遊戲時讓 Windows 把資源優先給遊戲，並暫緩背景更新；一般辦公不受影響。",
            Changes = [new(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0, OnWhenMissing: true)],
        },
        new()
        {
            Id = "gpu-scheduling",
            Group = TweakGroup.Performance,
            Name = "硬體加速 GPU 排程",
            Description = "讓顯示卡自行安排工作，可降低遊戲與影片的延遲；需要支援的顯示卡與驅動程式。",
            Changes = [new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1)],
            TakesEffect = "重新開機後生效",
        },
        new()
        {
            Id = "fast-startup",
            Group = TweakGroup.Performance,
            Name = "快速啟動",
            Description = "關機時保存系統狀態，下次開機較快。關閉後每次關機都是完整關機，較少遇到更新或驅動程式卡住的問題。",
            Changes = [new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1, 0, OnWhenMissing: true)],
            TakesEffect = "下次關機時生效",
        },
        new()
        {
            Id = "transparency",
            Group = TweakGroup.Performance,
            Name = "透明效果",
            Description = "視窗、開始功能表與工作列的半透明效果；較舊的電腦關閉後畫面較順。",
            Changes = [new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1, 0, OnWhenMissing: true)],
        },
        new()
        {
            Id = "storage-sense",
            Group = TweakGroup.Performance,
            Name = "儲存空間感知",
            Description = "Windows 定期自動清除暫存檔；預設也會刪除放在資源回收筒超過 30 天的檔案。",
            Changes = [new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1, 0)],
        },
        new()
        {
            Id = "show-extensions",
            Group = TweakGroup.Explorer,
            Name = "顯示副檔名",
            Description = "檔名後面顯示 .exe、.pdf 等副檔名，較容易認出偽裝成文件的執行檔。",
            Changes = [new(RegistryHive.CurrentUser, Advanced, "HideFileExt", 0, 1)],
            TakesEffect = "重新整理檔案總管視窗後生效",
        },
        new()
        {
            Id = "show-hidden",
            Group = TweakGroup.Explorer,
            Name = "顯示隱藏的檔案",
            Description = "在檔案總管顯示標示為隱藏的檔案與資料夾（例如 AppData）。",
            Changes = [new(RegistryHive.CurrentUser, Advanced, "Hidden", 1, 2)],
            TakesEffect = "重新整理檔案總管視窗後生效",
        },
        new()
        {
            Id = "explorer-this-pc",
            Group = TweakGroup.Explorer,
            Name = "檔案總管開啟「本機」",
            Description = "開啟檔案總管時直接顯示各個磁碟，而不是「首頁」或「快速存取」。",
            Changes = [new(RegistryHive.CurrentUser, Advanced, "LaunchTo", 1)],
            TakesEffect = "下次開啟檔案總管時生效",
        },
        new()
        {
            Id = "classic-context-menu",
            Group = TweakGroup.Explorer,
            Name = "完整的右鍵選單",
            Description = "Windows 11 按右鍵時直接顯示完整選項，不必再按「顯示其他選項」。",
            Changes = [new(RegistryHive.CurrentUser, ClassicMenuClsid + @"\InprocServer32", "", "", OffDeletesKey: ClassicMenuClsid)],
            TakesEffect = "重新啟動檔案總管後生效",
            NeedsExplorerRestart = true,
            MinBuild = Windows11,
        },
        new()
        {
            Id = "taskbar-left",
            Group = TweakGroup.Explorer,
            Name = "工作列圖示靠左",
            Description = "Windows 11 的工作列圖示與開始按鈕靠左，和 Windows 10 相同的位置。",
            Changes = [new(RegistryHive.CurrentUser, Advanced, "TaskbarAl", 0, 1)],
            MinBuild = Windows11,
        },
        new()
        {
            Id = "taskbar-end-task",
            Group = TweakGroup.Explorer,
            Name = "工作列右鍵「結束工作」",
            Description = "在工作列程式圖示的右鍵選單加上「結束工作」，不用開工作管理員就能關掉沒有回應的程式。",
            Changes = [new(RegistryHive.CurrentUser, Advanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, 0)],
            MinBuild = Windows11Version23H2,
        },
        new()
        {
            Id = "start-web-search",
            Group = TweakGroup.Explorer,
            Name = "開始功能表不搜尋網路",
            Description = "在開始功能表搜尋時只找電腦裡的程式、設定與檔案，不顯示 Bing 網頁結果。",
            Changes = [new(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1)],
            TakesEffect = "重新啟動檔案總管後生效",
            NeedsExplorerRestart = true,
        },
        new()
        {
            Id = "advertising-id",
            Group = TweakGroup.Privacy,
            Name = "關閉廣告識別碼",
            Description = "App 不能再用這台電腦的廣告識別碼追蹤使用習慣、顯示個人化廣告。",
            Changes = [new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)],
        },
        new()
        {
            Id = "suggestions",
            Group = TweakGroup.Privacy,
            Name = "關閉提示與推薦內容",
            Description = "不在開始功能表、設定與鎖定畫面顯示推薦與提示，也不自動安裝推薦的 App。",
            Changes =
            [
                new(RegistryHive.CurrentUser, ContentDelivery, "SubscribedContent-338388Enabled", 0, 1),
                new(RegistryHive.CurrentUser, ContentDelivery, "SubscribedContent-338389Enabled", 0, 1),
                new(RegistryHive.CurrentUser, ContentDelivery, "SubscribedContent-353694Enabled", 0, 1),
                new(RegistryHive.CurrentUser, ContentDelivery, "SubscribedContent-353696Enabled", 0, 1),
                new(RegistryHive.CurrentUser, ContentDelivery, "SystemPaneSuggestionsEnabled", 0, 1),
                new(RegistryHive.CurrentUser, ContentDelivery, "SilentInstalledAppsEnabled", 0, 1),
            ],
        },
        new()
        {
            Id = "tailored-experiences",
            Group = TweakGroup.Privacy,
            Name = "關閉量身打造的體驗",
            Description = "Microsoft 不再依診斷資料提供個人化的提示、廣告與建議。",
            Changes = [new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)],
        },
        new()
        {
            Id = "recent-files-tracking",
            Group = TweakGroup.Privacy,
            Name = "不記錄最近開啟的檔案",
            Description = "開始功能表與跳躍清單不再列出最近開啟的檔案。",
            Changes = [new(RegistryHive.CurrentUser, Advanced, "Start_TrackDocs", 0, 1)],
            TakesEffect = "重新啟動檔案總管後生效",
            NeedsExplorerRestart = true,
        },
        new()
        {
            Id = "activity-history",
            Group = TweakGroup.Privacy,
            Name = "關閉活動記錄",
            Description = "Windows 不再記錄、上傳使用過的 App 與檔案（時間軸與活動記錄）。",
            Changes =
            [
                new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
                new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0),
            ],
            TakesEffect = "登出或重新開機後生效",
        },
        new()
        {
            Id = "telemetry-required",
            Group = TweakGroup.Privacy,
            Name = "診斷資料只傳送必要項目",
            Description = "限制 Windows 只傳送安全性與更新需要的「必要」診斷資料，不傳送選用的使用資料。",
            Changes = [new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1)],
            TakesEffect = "重新開機後生效",
        },
    ];
}
