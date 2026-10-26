using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Maintenance;

namespace PcMaintenance.App.ViewModels;

/// <summary>A repair or tool button: what it does, and the work, which returns a message to show (or null).</summary>
public sealed partial class ToolItem(string title, string description, string glyph, Func<Task<string?>> run) : ObservableObject
{
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Glyph { get; } = glyph;
    public bool NeedsAdmin { get; init; }
    public bool IsAvailable { get; init; } = true;
    /// <summary>Asked before running, for tools that close windows or change settings.</summary>
    public string? Confirm { get; init; }

    [ObservableProperty]
    private string? _resultText;

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Confirm is { } question && !Dialogs.Confirm(question, Title)) return;
        try
        {
            ResultText = await run();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ResultText = $"無法執行：{UserMessage.For(ex)}";
        }
    }
}

public sealed record ToolGroup(string Title, IReadOnlyList<ToolItem> Items);

/// <summary>
/// Wintoys- and Glary-style repairs plus quick access to Windows' own administration tools. Repairs that need elevation
/// run in an elevated console window (Windows asks for permission), so their progress and result stay visible.
/// </summary>
public sealed class ToolsViewModel : PageViewModel
{
    private const string GodMode = "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}";
    private readonly AppServices _services;

    public ToolsViewModel(AppServices services)
    {
        _services = services;
        Repairs =
        [
            new("系統檔案檢查（SFC）", "檢查並修復損壞的 Windows 系統檔案，約需 10 到 30 分鐘，進度顯示在開啟的命令視窗。", "",
                () => RunInConsole("系統檔案檢查", "sfc /scannow")) { NeedsAdmin = true },
            new("修復 Windows 映像（DISM）", "SFC 無法修復時使用：從 Windows Update 下載修復用的檔案（需要網路），完成後再執行一次 SFC。", "",
                () => RunInConsole("修復 Windows 映像", "DISM /Online /Cleanup-Image /RestoreHealth")) { NeedsAdmin = true },
            new("清理 Windows 更新元件", "刪除已被新版取代的舊更新檔，釋放系統磁碟空間；之後已安裝的更新就不能再解除安裝。", "",
                () => RunInConsole("清理 Windows 更新元件", "DISM /Online /Cleanup-Image /StartComponentCleanup"))
            {
                NeedsAdmin = true,
                Confirm = "清理後，已安裝的 Windows 更新將無法再解除安裝。確定要清理嗎？",
            },
            new("重設 Windows 更新", "Windows Update 一直失敗時使用：停止更新服務、把下載快取改名保留，再重新啟動服務。", "",
                ResetWindowsUpdate)
            {
                NeedsAdmin = true,
                Confirm = "將停止 Windows 更新相關服務，把下載快取資料夾改名保留，再重新啟動服務；正在進行的更新會中斷。確定要重設嗎？",
            },
            new("重設網路連線（Winsock）", "網路程式連不上、其他電腦卻正常時使用；不會改變 IP 設定，完成後需要重新開機。", "",
                () => RunInConsole("重設 Winsock", "netsh winsock reset"))
            {
                NeedsAdmin = true,
                Confirm = "重設後需要重新開機才會生效。確定要重設嗎？",
            },
            new("備份驅動程式", "把這台電腦安裝的第三方驅動程式匯出到資料夾，重灌 Windows 後可以用來安裝。", "", BackupDrivers) { NeedsAdmin = true },
        ];
        QuickFixes =
        [
            new("清除 DNS 快取", "網站改了位址、內部系統連不上時使用，讓電腦重新查詢網址；較新的 Windows 會先詢問系統管理員權限。", "", SystemActions.FlushDnsAsync),
            new("重建圖示與縮圖快取", "圖示顯示錯誤、縮圖不正確時使用；檔案總管會關閉再自動開啟。", "", RebuildIconCacheAsync)
            {
                Confirm = "檔案總管與工作列會短暫關閉再重新開啟，已開啟的資料夾視窗會關閉。確定要重建嗎？",
            },
            new("重新啟動檔案總管", "工作列或桌面沒有回應時使用；已開啟的資料夾視窗會關閉。", "", RestartExplorerAsync)
            {
                Confirm = "檔案總管與工作列會關閉再重新開啟，已開啟的資料夾視窗會關閉。確定要重新啟動嗎？",
            },
            new("重新啟動顯示卡驅動程式", "畫面閃爍、黑屏或卡住時使用（等同按 Win+Ctrl+Shift+B）；畫面會黑一下，程式不會關閉。", "",
                () => Task.FromResult<string?>(SystemActions.RestartGraphicsDriver() ? "已送出重新啟動顯示卡驅動程式的指令。" : "無法送出指令。"))
            {
                Confirm = "畫面會黑一下並發出提示音，這是正常的。確定要重新啟動顯示卡驅動程式嗎？",
            },
            new("重設 Microsoft Store", "商店打不開或下載卡住時使用；完成後商店會自動開啟。", "",
                () => Open(Path.Combine(Environment.SystemDirectory, "wsreset.exe"), "已開始重設 Microsoft Store，完成後商店會自動開啟。")),
        ];
        Backups =
        [
            new("建立還原點", "變更系統設定或安裝驅動程式前，先保存一個可以還原的時間點。", "", CreateRestorePointAsync) { NeedsAdmin = true },
            new("系統還原", "把系統設定與程式還原到之前的還原點，個人檔案不受影響。", "",
                () => Open(Path.Combine(Environment.SystemDirectory, "rstrui.exe"), null)) { NeedsAdmin = true },
            new("系統保護設定", "開啟或關閉各磁碟的系統保護（還原點），以及設定還原點可用的空間。", "",
                () => Open(Path.Combine(Environment.SystemDirectory, "SystemPropertiesProtection.exe"), null)) { NeedsAdmin = true },
        ];
        Launchers =
        [
            new("系統管理", Available(
            [
                Launcher("所有設定（上帝模式）", "", "explorer.exe", GodMode),
                Launcher("控制台", "", "control.exe"),
                Launcher("裝置管理員", "", "devmgmt.msc"),
                Launcher("電腦管理", "", "compmgmt.msc"),
                Launcher("服務", "", "services.msc"),
                Launcher("工作排程器", "", "taskschd.msc"),
                Launcher("事件檢視器", "", "eventvwr.msc"),
                Launcher("本機群組原則", "", "gpedit.msc"),
                Launcher("登錄編輯程式", "", "regedit.exe"),
                Launcher("系統設定（msconfig）", "", "msconfig.exe"),
                Launcher("進階系統設定", "", "SystemPropertiesAdvanced.exe"),
            ])),
            new("效能與診斷", Available(
            [
                Launcher("資源監視器", "", "resmon.exe"),
                Launcher("效能監視器", "", "perfmon.exe"),
                Launcher("可靠性監視器", "", "perfmon.exe", "/rel"),
                Launcher("系統資訊", "", "msinfo32.exe"),
                Launcher("DirectX 診斷工具", "", "dxdiag.exe"),
            ])),
            new("磁碟、網路與安全", Available(
            [
                Launcher("磁碟管理", "", "diskmgmt.msc"),
                Launcher("磁碟清理", "", "cleanmgr.exe"),
                Launcher("磁碟重組", "", "dfrgui.exe"),
                Launcher("網路連線", "", "control.exe", "ncpa.cpl"),
                Launcher("Windows 防火牆", "", "control.exe", "firewall.cpl"),
                Launcher("網際網路選項", "", "control.exe", "inetcpl.cpl"),
                Launcher("Windows 安全性", "", "windowsdefender:"),
                Launcher("Windows Update", "", "ms-settings:windowsupdate"),
                Launcher("電源選項", "", "control.exe", "powercfg.cpl"),
                Launcher("程式和功能", "", "control.exe", "appwiz.cpl"),
            ])),
        ];
    }

    public override string Title => "系統工具";

    public override string Subtitle =>
        "常用的 Windows 修復動作與內建管理工具。需要系統管理員的修復會開啟命令視窗並由 Windows 詢問權限，進度與結果都顯示在那個視窗。";

    public IReadOnlyList<ToolItem> Repairs { get; }
    public IReadOnlyList<ToolItem> QuickFixes { get; }
    public IReadOnlyList<ToolItem> Backups { get; }
    public IReadOnlyList<ToolGroup> Launchers { get; }

    private static Task<string?> RunInConsole(string title, string commands) =>
        Task.FromResult<string?>(SystemActions.RunInElevatedConsole(title, commands) ? "已開啟命令視窗，請在那裡查看進度。" : "已取消（沒有同意系統管理員權限）。");

    private static Task<string?> Open(string target, string? done) =>
        Task.FromResult(SystemActions.Open(target) ? done : "已取消（沒有同意系統管理員權限）。");

    // Cryptographic Services is left running: stopping it also stops AppLocker's filter and Application Identity, and
    // "net stop" would wait for a Y/N answer. The result line says whether the folder was really renamed.
    private static Task<string?> ResetWindowsUpdate()
    {
        var backup = $"SoftwareDistribution.bak-{DateTime.Now:yyyyMMdd-HHmm}";
        var commands = string.Join("&",
            "net stop wuauserv /y", "net stop bits /y",
            $"ren \"%windir%\\SoftwareDistribution\" {backup} && echo 下載快取已改名為 {backup}，確認更新正常後可以刪除。" +
            " || echo 無法改名下載快取資料夾（可能仍在使用中），請重新開機後再試一次。",
            "net start bits", "net start wuauserv",
            "echo.", "echo 完成，可以關閉這個視窗。");
        return RunInConsole("重設 Windows 更新", commands);
    }

    private static Task<string?> BackupDrivers()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇驅動程式備份要存放的資料夾",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true) return Task.FromResult<string?>(null);
        var target = Path.Combine(dialog.FolderName, $"驅動程式備份-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}");
        Directory.CreateDirectory(target);
        return RunInConsole("備份驅動程式", $"pnputil /export-driver * \"{target}\"&echo.&echo 備份位置：{target}");
    }

    private async Task<string?> RebuildIconCacheAsync()
    {
        var deleted = await SystemActions.RebuildIconCacheAsync();
        _services.Log.Info($"重建圖示與縮圖快取：刪除 {deleted} 個快取檔");
        return $"已重建，刪除 {deleted} 個快取檔；圖示會在使用時重新產生。";
    }

    private static async Task<string?> RestartExplorerAsync()
    {
        await SystemActions.RestartExplorerAsync();
        return "已重新啟動檔案總管。";
    }

    private async Task<string?> CreateRestorePointAsync()
    {
        var result = await RestorePointLauncher.CreateAsync(_services.IsElevated);
        _services.Log.Info($"建立還原點：{result.Message}");
        return result.Message;
    }

    /// <summary>A Windows tool (from System32, or a settings link such as "ms-settings:") opened as Explorer would open it.</summary>
    private static ToolItem Launcher(string title, string glyph, string file, string arguments = "")
    {
        var isLink = file.Contains(':') && !Path.IsPathRooted(file);
        var path = isLink ? file
            : file == "explorer.exe" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), file)
            : Path.Combine(Environment.SystemDirectory, file);
        return new ToolItem(title, "", glyph, () => Task.FromResult(SystemActions.Open(path, arguments) ? null : "已取消（沒有同意系統管理員權限）。"))
        {
            IsAvailable = isLink || File.Exists(path),
        };
    }

    // Some tools are missing on some editions, e.g. the Group Policy editor on Windows Home.
    private static IReadOnlyList<ToolItem> Available(IEnumerable<ToolItem> items) => items.Where(i => i.IsAvailable).ToList();
}
