using Microsoft.Win32;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.Core.Tests;

public class InventoryTests
{
    [Theory]
    [InlineData(new string[0], CliAction.None, false, null)]
    [InlineData(new[] { "--clean" }, CliAction.Clean, false, null)]
    [InlineData(new[] { "--clean", "--dry-run" }, CliAction.Clean, true, null)]
    [InlineData(new[] { "--DRY-RUN", "--Auto-Clean" }, CliAction.AutoClean, true, null)]
    [InlineData(new[] { "--export-software", @"\\伺服器\共用\" }, CliAction.ExportSoftware, false, @"\\伺服器\共用\")]
    [InlineData(new[] { "--export-startup", "a.csv" }, CliAction.ExportStartup, false, "a.csv")]
    [InlineData(new[] { "--export-system", "spec.csv" }, CliAction.ExportSystem, false, "spec.csv")]
    public void Command_line_actions_are_recognised(string[] args, CliAction action, bool dryRun, string? path)
    {
        var command = CliCommand.Parse(args);
        Assert.Null(command.Error);
        Assert.Equal((action, dryRun, path), (command.Action, command.DryRun, command.Path));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--dry-run")]
    [InlineData("--clean", "--auto-clean")]
    [InlineData("--export-software")]
    [InlineData("--export-software", "--clean")]
    [InlineData("--export-startup", "a.csv", "--dry-run")]
    public void Invalid_command_lines_are_rejected_instead_of_opening_the_window(params string[] args)
    {
        var command = CliCommand.Parse(args);
        Assert.Equal(CliAction.None, command.Action);
        Assert.NotNull(command.Error);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("-cmd", "'-cmd")]
    public void Csv_fields_are_quoted_and_formulas_neutralised(string value, string expected) =>
        Assert.Equal(expected, CsvFile.Escape(value));

    [Fact]
    public void Software_list_is_excel_friendly_and_names_the_computer()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "list.csv");
        var app = new InstalledApp
        {
            DisplayName = "測試軟體, 專業版",
            Publisher = "測試公司",
            Version = "1.2",
            InstallDate = new DateOnly(2026, 10, 1),
            EstimatedSizeBytes = 3 * 1048576L / 2,
            UninstallString = "x",
            Scope = "所有使用者",
            Hive = RegistryHive.LocalMachine,
            View = RegistryView.Registry64,
            KeyPath = "k",
        };

        Assert.Equal(1, InventoryExport.WriteSoftware(file, [app], "PC-01"));

        var bytes = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = File.ReadAllText(file).Split("\r\n");
        Assert.Equal("電腦名稱,名稱,發行者,版本,安裝日期,大小（MB）,安裝範圍,安裝位置", lines[0]);
        Assert.Equal("PC-01,\"測試軟體, 專業版\",測試公司,1.2,2026-10-01,1.5,所有使用者,", lines[1]);
    }

    [Fact]
    public void Export_target_can_be_a_folder()
    {
        using var temp = new TempDir();
        var now = new DateTime(2026, 10, 25);
        Assert.Equal(Path.Combine(temp.Path, "軟體清單-PC-01-20261025.csv"),
            InventoryExport.ResolveTarget(temp.Path, InventoryExport.SoftwareKind, "PC-01", now));
        Assert.Equal(@"\\伺服器\共用\開機項目-PC-01-20261025.csv",
            InventoryExport.ResolveTarget(@"\\伺服器\共用\", InventoryExport.StartupKind, "PC-01", now));
        var file = Path.Combine(temp.Path, "自訂.csv");
        Assert.Equal(file, InventoryExport.ResolveTarget(file, InventoryExport.SoftwareKind, "PC-01", now));
    }
}
