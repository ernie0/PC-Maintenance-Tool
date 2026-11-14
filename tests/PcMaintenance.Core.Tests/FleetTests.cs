using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.Core.Tests;

public class FleetTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 9, 0, 0);

    [Fact]
    public void Csv_reads_back_quotes_commas_line_breaks_and_formula_guards()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "x.csv");
        CsvFile.Write(file, ["名稱", "說明"], [["a,b", "說\"明\"\n第二行"], ["=SUM(A1)", "-5"], ["", "空白名稱"]]);
        var rows = CsvFile.Read(file);
        Assert.Equal(4, rows.Count);
        Assert.Equal(["a,b", "說\"明\"\n第二行"], rows[1]);
        Assert.Equal(["=SUM(A1)", "-5"], rows[2]);
        Assert.Equal(["", "空白名稱"], rows[3]);
    }

    private static Dictionary<string, string> Export(string name, string exported, params (string Column, string Value)[] fields)
    {
        var row = new Dictionary<string, string> { ["電腦名稱"] = name, ["Windows"] = "Windows 11 專業版", ["版本"] = "25H2", ["組建"] = "26200.1", ["匯出時間"] = exported };
        foreach (var (column, value) in fields) row[column] = value;
        return row;
    }

    [Fact]
    public void Each_computer_appears_once_from_its_newest_export_with_what_needs_attention()
    {
        var computers = FleetSummary.Summarize(
        [
            (Export("會計-01", "2026-11-13 08:00", ("上次 Windows 更新", "2026-11-11"), ("磁碟空間", "C: 剩 242 GB／930 GB（26%）；D: 剩 10 GB／1.8 TB（1%）")), "a.csv"),
            (Export("會計-01", "2026-11-01 08:00", ("上次 Windows 更新", "2026-09-01")), "old.csv"),
            (Export("業務-02", "2026-11-14 08:00", ("上次 Windows 更新", "2026-09-20"), ("磁碟", "WDC 1 TB HDD（警告）"),
                ("安全性建議", "使用者帳戶控制；自動登入"), ("最近 30 天的問題", "藍色畫面 2；程式當機 5"), ("電池健康度", "48%")), "b.csv"),
            (Export("倉庫-03", "2026-10-20 17:00", ("上次 Windows 更新", "2026-10-15"), ("磁碟空間", "C: 剩 80 GB／238 GB（34%）")), "c.csv"),
            (Export("前台-04", "2026-11-14 07:30", ("上次 Windows 更新", "2026-11-10"), ("磁碟空間", "C: 剩 100 GB／238 GB（42%）"),
                ("最近 30 天的問題", "程式當機 3")), "d.csv"),
        ], Now);

        Assert.Equal(4, computers.Count);
        // Those needing attention come first.
        Assert.Equal("前台-04", computers[^1].Name);
        Assert.False(computers[^1].NeedsAttention);
        var accounting = Assert.Single(computers, c => c.Name == "會計-01");
        Assert.Equal("a.csv", accounting.SourceFile);
        Assert.Equal(1, accounting.LowestFreePercent);
        Assert.Equal(["D: 只剩 1% 空間"], accounting.Concerns);
        var sales = Assert.Single(computers, c => c.Name == "業務-02");
        Assert.Contains("55 天沒有安裝 Windows 更新", sales.Concerns);
        Assert.Contains("磁碟健康狀態警告", sales.Concerns);
        Assert.Contains("安全性：使用者帳戶控制、自動登入", sales.Concerns);
        Assert.Contains("最近 30 天：藍色畫面 2 次", sales.Concerns);
        Assert.Contains("電池健康度 48%", sales.Concerns);
        Assert.Contains("資料已經 24 天沒有更新", Assert.Single(computers, c => c.Name == "倉庫-03").Concerns);
        Assert.Equal("11 專業版 25H2", sales.Windows);
    }

    [Fact]
    public void A_folder_mixes_exports_old_and_new_with_other_csv_files()
    {
        using var temp = new TempDir();
        // An export from 0.4.0, without the health columns, and its software list with fewer columns.
        CsvFile.Write(Path.Combine(temp.Path, "電腦配備-OLD-20261101.csv"),
            ["電腦名稱", "網域／工作群組", "Windows", "版本", "組建", "匯出時間"], [["OLD", "WORKGROUP", "Windows 10 專業版", "22H2", "19045.1", "2026-11-01 08:00"]]);
        CsvFile.Write(Path.Combine(temp.Path, "軟體清單-OLD-20261101.csv"), ["電腦名稱", "名稱", "版本"], [["OLD", "7-Zip 26.05 (x64 edition)", "26.05"]]);
        CsvFile.Write(Path.Combine(temp.Path, "報價單.csv"), ["品名", "數量"], [["網路線", "3"]]);
        Directory.CreateDirectory(Path.Combine(temp.Path, "子資料夾"));
        // This computer's own exports, as the program writes them, under names the page does not know.
        var mine = Path.Combine(temp.Path, "子資料夾", "電腦配備-mine.csv");
        var info = SystemInfoReader.Read();
        InventoryExport.WriteSystem(mine, info, ComputerHealth.Read(isElevated: false, info), Now);
        InventoryExport.WriteSoftware(Path.Combine(temp.Path, "子資料夾", "my-software.csv"), [new Uninstall.InstalledApp { DisplayName = "7-Zip 26.06 (x64 edition)", Version = "26.06", UninstallString = "", Hive = Microsoft.Win32.RegistryHive.LocalMachine, View = Microsoft.Win32.RegistryView.Registry64, KeyPath = "7-Zip" }], Environment.MachineName);
        InventoryExport.WriteExtensions(Path.Combine(temp.Path, "子資料夾", "ext.csv"),
            [new Uninstall.BrowserExtension("Edge", "預設", "abc", "翻譯", "2.0", true, "Edge 附加元件商店", true)], Environment.MachineName);

        var load = FleetSummary.Load(temp.Path, Now);
        Assert.Null(load.Problem);
        Assert.Equal(2, load.FilesRead);
        Assert.Equal(1, load.FilesSkipped);
        Assert.Equal(["OLD", Environment.MachineName], load.Computers.Select(c => c.Name).OrderBy(n => n == Environment.MachineName));
        var me = Assert.Single(load.Computers, c => c.Name == Environment.MachineName);
        Assert.NotNull(me.LowestFreePercent);
        Assert.NotEqual("", me.Fields["Windows 11 升級"]);
        Assert.NotEqual("", me.Fields["備份"]);
        Assert.Contains("Windows 10 已結束支援", Assert.Single(load.Computers, c => c.Name == "OLD").Concerns);

        // The same program in two versions counts as one, with the older copy marked.
        Assert.Equal(2, load.SoftwareComputers);
        var zip = Assert.Single(load.SoftwareList, s => s.Name == "7-Zip (x64 edition)");
        Assert.Equal("26.06", zip.NewestVersion);
        Assert.Equal("OLD", Assert.Single(zip.Installs, i => i.IsOutdated).Computer);
        Assert.Equal("Edge 擴充功能", Assert.Single(load.SoftwareList, s => s.Name == "翻譯").Kind);

        var summary = Path.Combine(temp.Path, "彙整.csv");
        Assert.Equal(2, FleetSummary.WriteSummary(summary, load.Computers, Now));
        Assert.StartsWith("電腦名稱,需要注意,磁碟用量趨勢,", File.ReadAllLines(summary)[0]);
        Assert.Contains("找不到資料夾", FleetSummary.Load(Path.Combine(temp.Path, "不存在"), Now).Problem);
    }

    [Fact]
    public void Named_daily_exports_are_read_newest_first_with_a_disk_trend()
    {
        using var temp = new TempDir();
        string[] header = ["電腦名稱", "Windows", "版本", "組建", "磁碟空間", "上次開機", "Windows 11 升級", "備份", "匯出時間"];
        void Day(string name, DateTime day, string space, string boot = "", string windows = "Windows 11 專業版", string upgrade = "已是 Windows 11", string backup = "OneDrive 備份：文件") =>
            CsvFile.Write(Path.Combine(temp.Path, $"電腦配備-{name}-{day:yyyyMMdd}.csv"), header,
                [[name, windows, "25H2", "26200.1", space, boot, upgrade, backup, day.ToString("yyyy-MM-dd 08:00")]]);
        // 60 daily files; C: loses 2 GB a day, about 60 GB a month.
        for (var i = 0; i < 60; i++)
            Day("設計-01", Now.Date.AddDays(-i), $"C: 剩 {300 - 2 * (59 - i)} GB／930 GB（{(300 - 2 * (59 - i)) * 100 / 930}%）");
        Day("倉庫-01", Now.Date, "C: 剩 120 GB／931 GB（13%）", boot: "2026-10-20 07:30", windows: "Windows 10 專業版", upgrade: "不能升級：TPM 2.0、處理器", backup: "沒有偵測到");
        Day("會計-02", Now.Date, "C: 剩 200 GB／476 GB（42%）", windows: "Windows 10 專業版", upgrade: "可以升級");

        var load = FleetSummary.Load(temp.Path, Now);
        Assert.Equal(62, load.FilesRead);
        var design = Assert.Single(load.Computers, c => c.Name == "設計-01");
        Assert.Equal(Now.Date.AddHours(8), design.ExportedAt);
        Assert.Equal("C: 每月少 60.0 GB", design.Trend);
        // 182 GB left, 89 GB of it above the 10 % line, at 2 GB a day.
        Assert.Contains("C: 照目前速度約 44 天後空間不足", design.Concerns);

        var store = Assert.Single(load.Computers, c => c.Name == "倉庫-01");
        Assert.Contains("Windows 10 已結束支援，硬體不能升級（TPM 2.0、處理器）", store.Concerns);
        Assert.Contains("沒有偵測到檔案備份", store.Concerns);
        Assert.Contains("已經 25 天沒有重新開機", store.Concerns);
        Assert.Equal(["Windows 10 已結束支援，可以升級到 Windows 11"], Assert.Single(load.Computers, c => c.Name == "會計-02").Concerns);
    }

    [Theory]
    [InlineData("7-Zip 26.05 (x64 edition)", "7-Zip (x64 edition)")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Visual C++ 2015-2022 Redistributable (x64)")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++ (64-bit x64)")]
    [InlineData("Mozilla Firefox (x64 zh-TW)", "Mozilla Firefox (x64 zh-TW)")]
    [InlineData("Adobe Acrobat Reader DC v2024.003.20112", "Adobe Acrobat Reader DC")]
    [InlineData("Microsoft 365 Apps for business - zh-tw", "Microsoft 365 Apps for business - zh-tw")]
    public void Software_names_drop_their_version_numbers(string name, string expected) =>
        Assert.Equal(expected, FleetSoftwareList.Normalize(name));

    [Fact]
    public void Versions_compare_part_by_part()
    {
        Assert.True(FleetSoftwareList.CompareVersions("10.2", "9.11") > 0);
        Assert.True(FleetSoftwareList.CompareVersions("26.05.00.0", "26.06") < 0);
        Assert.Equal(0, FleetSoftwareList.CompareVersions("1.0", "1.0.0"));
        var list = FleetSoftwareList.Aggregate(
        [
            ("A", new FleetItem("軟體", "X", "X", "", "2.0")),
            ("B", new FleetItem("軟體", "X", "X", "", "1.5")),
            ("B", new FleetItem("軟體", "X", "X", "", "2.0")),
            ("C", new FleetItem("軟體", "X", "X", "", "")),
        ]);
        var x = Assert.Single(list);
        Assert.Equal(3, x.Computers);
        Assert.Equal(0, x.Outdated);
        Assert.Equal("2.0（2 台）、未知版本（1 台）", x.VersionsText);
    }

    [Fact]
    public void The_inventory_folder_comes_from_the_company_settings()
    {
        Assert.Equal("", AppSettings.Default.InventoryFolder);
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, "{ \"inventoryFolder\": \"\\\\\\\\server\\\\share\\\\電腦盤點\" }");
        Assert.Equal(@"\\server\share\電腦盤點", SettingsLoader.Load(file).Settings.InventoryFolder);
    }
}
