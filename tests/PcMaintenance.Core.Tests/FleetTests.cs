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
        // An export from 0.4.0, without the health columns.
        CsvFile.Write(Path.Combine(temp.Path, "電腦配備-OLD-20261101.csv"),
            ["電腦名稱", "網域／工作群組", "Windows", "版本", "組建", "匯出時間"], [["OLD", "WORKGROUP", "Windows 10 專業版", "22H2", "19045.1", "2026-11-01 08:00"]]);
        CsvFile.Write(Path.Combine(temp.Path, "軟體清單-OLD-20261101.csv"), ["電腦名稱", "名稱", "版本"], [["OLD", "7-Zip", "26.06"]]);
        Directory.CreateDirectory(Path.Combine(temp.Path, "子資料夾"));
        // This computer's own export, as the program writes it.
        var mine = Path.Combine(temp.Path, "子資料夾", "電腦配備-mine.csv");
        InventoryExport.WriteSystem(mine, SystemInfoReader.Read(), ComputerHealth.Read(isElevated: false), Now);

        var load = FleetSummary.Load(temp.Path, Now);
        Assert.Null(load.Problem);
        Assert.Equal(2, load.FilesRead);
        Assert.Equal(1, load.FilesSkipped);
        Assert.Equal(["OLD", Environment.MachineName], load.Computers.Select(c => c.Name).OrderBy(n => n == Environment.MachineName));
        Assert.NotNull(Assert.Single(load.Computers, c => c.Name == Environment.MachineName).LowestFreePercent);

        var summary = Path.Combine(temp.Path, "彙整.csv");
        Assert.Equal(2, FleetSummary.WriteSummary(summary, load.Computers, Now));
        Assert.StartsWith("電腦名稱,需要注意,", File.ReadAllLines(summary)[0]);
        Assert.Contains("找不到資料夾", FleetSummary.Load(Path.Combine(temp.Path, "不存在"), Now).Problem);
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
