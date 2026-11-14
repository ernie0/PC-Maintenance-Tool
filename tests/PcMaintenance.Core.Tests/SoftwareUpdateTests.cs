using PcMaintenance.Core.Common;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.Core.Tests;

public class SoftwareUpdateTests
{
    // Laid out the way winget 1.29 prints it: columns padded in console cells, where each Chinese character takes two.
    private const string Output =
        "   - \r   \\ \r   █████▒▒▒▒  1.00 MB / 2.00 MB\r" +
        "名稱                                 識別碼                       版本          可用\r\n" +
        "-----------------------------------------------------------------------------------------\r\n" +
        "NVIDIA PhysX 系統軟體 9.23.1019      Nvidia.PhysX                 9.23.1019     9.26.0703\r\n" +
        "Adobe Acrobat (64-bit)               Adobe.Acrobat.Pro            22.003.20263  26.002.22031\r\n" +
        "AnyDesk                              AnyDesk.AnyDesk              ad 9.0.14     9.8.3\r\n" +
        "很長很長的軟體名稱會被winget截斷…     Vendor.VeryLongProductName…  1.0           2.0\r\n" +
        "4 個升級可供使用。\r\n" +
        "2 套件具有無法確認的版本號碼。使用--include-unknown (包含未知項目) 來看到所有結果。\r\n";

    [Fact]
    public void Wingets_table_is_read_by_console_cells_so_chinese_names_line_up()
    {
        var updates = Winget.Parse(Output);
        Assert.Equal(4, updates.Count);
        Assert.Equal(new SoftwareUpdate("NVIDIA PhysX 系統軟體 9.23.1019", "Nvidia.PhysX", "9.23.1019", "9.26.0703"), updates[0]);
        Assert.Equal(new SoftwareUpdate("Adobe Acrobat (64-bit)", "Adobe.Acrobat.Pro", "22.003.20263", "26.002.22031"), updates[1]);
        Assert.Equal("ad 9.0.14", updates[2].Version);
        Assert.Equal(("Vendor.VeryLongProductName…", "1.0", "2.0"), (updates[3].Id, updates[3].Version, updates[3].Available));
        Assert.False(updates[3].IsIdComplete);
        Assert.True(updates[0].IsIdComplete);
    }

    [Fact]
    public void Output_without_a_table_lists_nothing()
    {
        Assert.Empty(Winget.Parse("找不到可用的升級。\r\n"));
        Assert.Empty(Winget.Parse(""));
    }

    [Fact]
    public void Updates_run_by_exact_id_or_by_the_cut_ids_beginning()
    {
        Assert.Equal("winget upgrade --id \"Nvidia.PhysX\" --exact --source winget",
            Winget.UpgradeCommand(new SoftwareUpdate("NVIDIA PhysX", "Nvidia.PhysX", "1", "2")));
        Assert.Equal("winget upgrade \"Vendor.VeryLongProductName\" --source winget",
            Winget.UpgradeCommand(new SoftwareUpdate("x", "Vendor.VeryLongProductName…", "1", "2")));
        // A quote cannot break out of the argument in cmd.exe.
        Assert.Equal("winget upgrade --id \"Evil.App& del x\" --exact --source winget",
            Winget.UpgradeCommand(new SoftwareUpdate("x", "Evil.App\"& del x", "1", "2")));
    }

    [Theory]
    [InlineData('名', 2)]
    [InlineData('Ａ', 2)]
    [InlineData('A', 1)]
    [InlineData('…', 1)]
    public void Chinese_and_fullwidth_characters_take_two_cells(char c, int cells) => Assert.Equal(cells, Winget.CellWidth(c));

    [Fact]
    public void Software_updates_are_allowed_unless_the_company_settings_say_otherwise()
    {
        Assert.True(AppSettings.Default.AllowSoftwareUpdates);
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, "{ \"allowSoftwareUpdates\": false }");
        Assert.False(SettingsLoader.Load(file).Settings.AllowSoftwareUpdates);
    }
}
