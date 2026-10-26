using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Junk;

/// <summary>
/// Fixed list of locations known to hold disposable files. Browser entries cover caches only,
/// never history, cookies or saved passwords.
/// </summary>
public static class JunkCatalog
{
    private const string CacheDescription = "網頁快取檔，不含瀏覽紀錄、Cookie 與密碼";

    public static IReadOnlyList<JunkCategory> Create(AppSettings settings, SystemFolders folders)
    {
        var categories = new List<JunkCategory>
        {
            new()
            {
                Id = "user-temp",
                Name = "使用者暫存檔",
                Description = "目前使用者暫存資料夾（%TEMP%）中的檔案",
                Locations = [new(folders.UserTemp)],
            },
            new()
            {
                Id = "windows-temp",
                Name = "Windows 暫存檔",
                Description = @"系統暫存資料夾（Windows\Temp）中的檔案",
                RequiresAdmin = true,
                Locations = [new(Path.Combine(folders.WindowsDir, "Temp"))],
            },
        };

        AddChromium(categories, "edge-cache", "Microsoft Edge 快取", "Microsoft Edge", "msedge",
            Path.Combine(folders.LocalAppData, "Microsoft", "Edge", "User Data"));
        AddChromium(categories, "chrome-cache", "Google Chrome 快取", "Google Chrome", "chrome",
            Path.Combine(folders.LocalAppData, "Google", "Chrome", "User Data"));
        AddFirefox(categories, Path.Combine(folders.LocalAppData, "Mozilla", "Firefox", "Profiles"));
        categories.Add(new()
        {
            Id = "shader-cache",
            Name = "顯示卡著色器快取",
            Description = "DirectX 與顯示卡驅動程式的著色器快取，會自動重建；3D 與繪圖軟體下次開啟可能稍慢",
            // Rebuilding costs 3D and CAD users a slower first start, so it is opt-in.
            DefaultSelected = false,
            Locations =
            [
                new(Path.Combine(folders.LocalAppData, "D3DSCache"), ApplyAgeFilter: false),
                new(Path.Combine(folders.LocalAppData, "NVIDIA", "DXCache"), ApplyAgeFilter: false),
                new(Path.Combine(folders.LocalAppData, "NVIDIA", "GLCache"), ApplyAgeFilter: false),
                new(Path.Combine(folders.LocalAppData, "AMD", "DxCache"), ApplyAgeFilter: false),
                new(Path.Combine(folders.LocalAppData, "AMD", "GLCache"), ApplyAgeFilter: false),
            ],
        });
        var explorer = Path.Combine(folders.LocalAppData, "Microsoft", "Windows", "Explorer");
        var recent = Path.Combine(folders.AppData, "Microsoft", "Windows", "Recent");
        categories.AddRange(
        [
            new()
            {
                Id = "thumbnail-cache",
                Name = "縮圖快取",
                Description = "檔案總管的相片與影片縮圖，會自動重建；檔案總管正在使用的部分會略過",
                DefaultSelected = false,
                Locations = [new(explorer, ApplyAgeFilter: false, FileName: "thumbcache_*.db")],
            },
            new()
            {
                Id = "recent-items",
                Name = "最近使用的項目",
                Description = "檔案總管與工作列跳躍清單裡「最近開啟的檔案」紀錄，只刪紀錄，不會刪除檔案本身",
                // Privacy rather than space: people rely on these lists, so it is opt-in.
                DefaultSelected = false,
                Locations =
                [
                    new(recent, ApplyAgeFilter: false, FileName: "*.lnk"),
                    new(Path.Combine(recent, "AutomaticDestinations"), ApplyAgeFilter: false, FileName: "*.automaticDestinations-ms"),
                    new(Path.Combine(recent, "CustomDestinations"), ApplyAgeFilter: false, FileName: "*.customDestinations-ms"),
                ],
            },
        ]);

        var wer = Path.Combine(folders.ProgramData, "Microsoft", "Windows", "WER");
        categories.AddRange(
        [
            new()
            {
                Id = "crash-dumps",
                Name = "程式當機傾印檔",
                Description = "程式異常結束時留下的記憶體傾印（CrashDumps）",
                Locations = [new(Path.Combine(folders.LocalAppData, "CrashDumps"))],
            },
            new()
            {
                Id = "system-crash-dumps",
                Name = "系統當機傾印檔",
                Description = "藍色畫面或驅動程式錯誤留下的記憶體傾印（MEMORY.DMP 等），不分析當機原因就可以刪除",
                RequiresAdmin = true,
                // Kept by default so a recent crash can still be analysed.
                DefaultSelected = false,
                Locations =
                [
                    new(folders.WindowsDir, FileName: "MEMORY.DMP"),
                    new(Path.Combine(folders.WindowsDir, "Minidump")),
                    new(Path.Combine(folders.WindowsDir, "LiveKernelReports")),
                ],
            },
            new()
            {
                Id = "user-error-reports",
                Name = "錯誤報告（使用者）",
                Description = "Windows 錯誤報告留下的記錄檔",
                Locations = [new(Path.Combine(folders.LocalAppData, "Microsoft", "Windows", "WER"))],
            },
            new()
            {
                Id = "system-error-reports",
                Name = "錯誤報告（系統）",
                Description = "系統層級的 Windows 錯誤報告",
                RequiresAdmin = true,
                Locations = [new(Path.Combine(wer, "ReportArchive")), new(Path.Combine(wer, "ReportQueue"))],
            },
            new()
            {
                Id = "windows-update-cache",
                Name = "Windows 更新下載快取",
                Description = "已下載的更新安裝檔，安裝完成後即可刪除",
                RequiresAdmin = true,
                Locations = [new(Path.Combine(folders.WindowsDir, "SoftwareDistribution", "Download"))],
            },
            new()
            {
                Id = "recycle-bin",
                Name = "資源回收筒",
                Description = "清空所有磁碟的資源回收筒，清空後無法復原",
                DefaultSelected = false,
                IsRecycleBin = true,
            },
        ]);

        return categories
            .Where(c => !settings.DisabledJunkCategories.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static void AddChromium(List<JunkCategory> categories, string id, string name, string appName, string process, string userDataDir)
    {
        if (!Directory.Exists(userDataDir)) return;
        var locations = new List<JunkLocation>();
        foreach (var shared in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
            locations.Add(new(Path.Combine(userDataDir, shared), ApplyAgeFilter: false));
        foreach (var profile in SafeDirectories(userDataDir))
        {
            // Profile folders (Default, Profile 1, ...) are the ones holding a Preferences file.
            if (!File.Exists(Path.Combine(profile, "Preferences"))) continue;
            foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
                locations.Add(new(Path.Combine(profile, cache), ApplyAgeFilter: false));
        }
        categories.Add(new()
        {
            Id = id,
            Name = name,
            Description = CacheDescription,
            Locations = locations,
            BlockingProcesses = [process],
            BlockingAppName = appName,
        });
    }

    private static void AddFirefox(List<JunkCategory> categories, string profilesDir)
    {
        if (!Directory.Exists(profilesDir)) return;
        categories.Add(new()
        {
            Id = "firefox-cache",
            Name = "Firefox 快取",
            Description = CacheDescription,
            Locations = SafeDirectories(profilesDir).Select(p => new JunkLocation(Path.Combine(p, "cache2"), ApplyAgeFilter: false)).ToList(),
            BlockingProcesses = ["firefox"],
            BlockingAppName = "Firefox",
        });
    }

    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
