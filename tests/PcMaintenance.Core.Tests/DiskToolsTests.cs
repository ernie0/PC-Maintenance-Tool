using PcMaintenance.Core.Common;
using PcMaintenance.Core.DiskTools;

namespace PcMaintenance.Core.Tests;

public class DiskToolsTests
{
    [Fact]
    public void Inventory_maps_the_system_drive_to_a_physical_disk()
    {
        var inventory = DiskInventory.Read(includeReliability: false);

        Assert.Null(inventory.Error);
        var system = Assert.Single(inventory.Volumes, v => v.IsSystem);
        Assert.True(system.TotalBytes > 0 && system.FreeBytes <= system.TotalBytes);
        var disk = Assert.Single(inventory.Disks, d => d.Number == system.DiskNumber);
        Assert.NotEqual("", disk.Model);
        Assert.Null(disk.Reliability);
    }

    [Theory]
    [InlineData(0, DiskJobOutcome.Ok)]
    [InlineData(2, DiskJobOutcome.Ok)]
    [InlineData(1, DiskJobOutcome.Fixed)]
    [InlineData(3, DiskJobOutcome.NeedsRepair)]
    [InlineData(5, DiskJobOutcome.Failed)]
    public void Check_exit_codes_are_explained(int exitCode, DiskJobOutcome outcome) =>
        Assert.Equal(outcome, DiskMaintenance.DescribeCheck(exitCode, "").Outcome);

    [Theory]
    [InlineData(0, "最佳化完成。", DiskJobOutcome.Ok)]
    [InlineData(0, "因為您的權限不足以執行此作業，所以儲存空間最佳化工具無法啟動。 (0x89000024)", DiskJobOutcome.Failed)]
    [InlineData(1, "", DiskJobOutcome.Failed)]
    public void Optimize_failures_are_caught_even_with_exit_code_zero(int exitCode, string output, DiskJobOutcome outcome) =>
        Assert.Equal(outcome, DiskMaintenance.DescribeOptimize(exitCode, output).Outcome);

    [Fact]
    public async Task Tool_output_is_decoded_from_the_console_code_page()
    {
        var (_, output) = await ConsoleTool.RunAsync("fsutil.exe", "dirty query C:", CancellationToken.None);
        Assert.NotEqual("", output.Trim());
        Assert.DoesNotContain('�', output);
    }

    [Fact]
    public void Tail_keeps_the_last_non_empty_lines() =>
        Assert.Equal("c\nd", DiskMaintenance.Tail("a\n\n b \nc\n\nd\n", 2));
}
