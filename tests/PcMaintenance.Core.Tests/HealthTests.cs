using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.Core.Tests;

public class HealthTests
{
    [Theory]
    [InlineData("2026-11 安全性修補程式 (KB5132061) (26300.9974)", true)]
    [InlineData("2026-10 .NET Framework 安全性更新 (KB5127694)", true)]
    [InlineData("KB890830：Windows 惡意軟體移除工具 x64 - v5.147", true)]
    [InlineData("Windows 11, version 26H2", true)]
    [InlineData("安全情報更新 Microsoft Defender Antivirus - KB2267602 (版本 1.463.290.0)", false)]
    [InlineData("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", false)]
    [InlineData("Intel Corporation - System - 10.1.19444.8378", false)]
    public void Windows_updates_are_told_apart_from_store_apps_drivers_and_signatures(string title, bool expected) =>
        Assert.Equal(expected, UpdateHistory.IsWindowsUpdate(title));

    [Fact]
    public void Update_history_gives_the_last_windows_update_and_failures_not_retried()
    {
        var now = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var status = UpdateHistory.Summarize(
        [
            new(now.AddDays(-1), "安全情報更新 Microsoft Defender Antivirus - KB2267602", 2, 1),
            new(now.AddDays(-2), "9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", 2, 1),
            new(now.AddDays(-3), "2026-11 安全性修補程式 (KB5132061)", 2, 1),
            new(now.AddDays(-4), "2026-11 安全性修補程式 (KB5132061)", 4, 1),       // failed, then installed the next day
            new(now.AddDays(-5), "2026-10 .NET Framework 安全性更新 (KB5127694)", 4, 1), // failed and not retried
            new(now.AddDays(-60), "2026-09 安全性修補程式 (KB5120000)", 4, 1),       // too old to matter
            new(now.AddDays(-6), "2026-10 預覽更新 (KB5127936)", 2, 2),             // an uninstall
        ], now);
        Assert.True(status.Readable);
        Assert.Equal(now.AddDays(-3), status.LastInstalledUtc);
        var failure = Assert.Single(status.Failures);
        Assert.Contains("KB5127694", failure.Title);
    }

    [Fact]
    public void Battery_health_is_full_charge_over_design_capacity()
    {
        Assert.Equal(78, new BatteryHealth("BAT0", 50_000, 39_000, 312).HealthPercent);
        Assert.Equal(100, new BatteryHealth("BAT0", 50_000, 51_000, 2).HealthPercent);
        Assert.Null(new BatteryHealth("BAT0", null, 39_000, null).HealthPercent);
    }

    [Theory]
    [InlineData(28, "沒有安裝驅動程式", "更新驅動程式")]
    [InlineData(43, "Windows 已停止", "重新開機")]
    [InlineData(22, "已停用", "啟用裝置")]
    [InlineData(99, "代碼 99", "裝置管理員")]
    public void Device_manager_codes_read_as_reason_and_remedy(int code, string reason, string remedy)
    {
        var (text, next) = DeviceProblems.Describe(code);
        Assert.Contains(reason, text);
        Assert.Contains(remedy, next);
    }

    [Fact]
    public void Reading_health_on_this_computer_works_without_elevation()
    {
        var health = ComputerHealth.Read(isElevated: false);
        Assert.True(health.Updates.Readable);
        Assert.All(health.Printers, p => Assert.NotEqual("", p.Name));
        Assert.True(health.Printers.Count(p => p.IsDefault) <= 1);
    }
}
