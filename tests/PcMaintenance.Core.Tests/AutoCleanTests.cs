using PcMaintenance.Core.Common;
using PcMaintenance.Core.Inventory;
using PcMaintenance.Core.Junk;

namespace PcMaintenance.Core.Tests;

public class AutoCleanTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-3);

    private static JunkCategory Category(string id, string root) => new()
    {
        Id = id,
        Name = id,
        Description = "",
        Locations = [new JunkLocation(root)],
    };

    [Fact]
    public void Only_default_categories_that_need_no_elevation_are_cleaned_and_a_dry_run_deletes_nothing()
    {
        using var temp = new TempDir();
        var plain = temp.File(@"plain\a.tmp", 100, Old);
        var optIn = temp.File(@"optin\b.tmp", 10, Old);
        var admin = temp.File(@"admin\c.tmp", 10, Old);
        var browser = temp.File(@"browser\d.tmp", 10, Old);
        JunkCategory[] categories =
        [
            Category("plain", temp.Dir("plain")),
            Category("optin", temp.Dir("optin")) with { DefaultSelected = false },
            Category("admin", temp.Dir("admin")) with { RequiresAdmin = true },
            Category("browser", temp.Dir("browser")) with { BlockingProcesses = ["fakebrowser"] },
        ];
        var cleaner = new AutoCleaner(AppSettings.Default, isProcessRunning: name => name == "fakebrowser");

        var dryRun = cleaner.Run(categories, isElevated: false, dryRun: true);
        Assert.Equal(["plain", "browser"], dryRun.Items.Select(i => i.Category));
        Assert.Equal(100, dryRun.Bytes);
        Assert.NotNull(dryRun.Items[1].Note);
        Assert.All(new[] { plain, optIn, admin, browser }, f => Assert.True(File.Exists(f)));

        var real = cleaner.Run(categories, isElevated: false, dryRun: false);
        Assert.Equal(1, real.Files);
        Assert.False(File.Exists(plain));
        Assert.All(new[] { optIn, admin, browser }, f => Assert.True(File.Exists(f)));
    }

    [Theory]
    [InlineData(null, 7, true)]
    [InlineData(3.0, 7, false)]
    [InlineData(8.0, 7, true)]
    [InlineData(1.0, 0, true)]
    [InlineData(-2.0, 7, true)]
    public void Auto_clean_is_due_after_the_interval_or_when_the_clock_went_back(double? daysAgo, int interval, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(8));
        var last = daysAgo is { } days ? new AutoCleanState(now.AddDays(-days), 0, 0) : null;
        Assert.Equal(expected, AutoCleanSchedule.IsDue(last, now, interval));
    }

    [Fact]
    public void State_round_trips_and_a_damaged_file_reads_as_never_run()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "auto-clean.json");
        var state = new AutoCleanState(DateTimeOffset.Now, 1234, 5);
        AutoCleanSchedule.Save(file, state);
        Assert.Equal(state, AutoCleanSchedule.Load(file));

        File.WriteAllText(file, "{ broken");
        Assert.Null(AutoCleanSchedule.Load(file));
    }

    [Fact]
    public void A_single_file_location_takes_only_that_file_and_leaves_the_folder_alone()
    {
        using var temp = new TempDir();
        var windows = temp.Dir("Windows");
        var dump = temp.File(@"Windows\MEMORY.DMP", 100, Old);
        var sibling = temp.File(@"Windows\setup.log", 10, Old);
        var emptyFolder = temp.Dir("Windows", "Empty");
        Directory.SetCreationTimeUtc(emptyFolder, Old);
        var category = Category("dump", windows) with { Locations = [new JunkLocation(windows, FileName: "MEMORY.DMP")] };

        var scan = new JunkScanner(AppSettings.Default, _ => false).Scan(category, isElevated: true);
        Assert.Equal(dump, Assert.Single(scan.Files).Path);

        new JunkCleaner(AppSettings.Default, null, _ => false).Clean(scan);
        Assert.False(File.Exists(dump));
        Assert.True(File.Exists(sibling));
        Assert.True(Directory.Exists(emptyFolder));
    }
    [Theory]
    [InlineData(null, 1, true)]
    [InlineData("2026-11-14 08:00", 1, false)]
    [InlineData("2026-11-13 23:50", 1, true)]
    [InlineData("2026-11-10 08:00", 7, false)]
    [InlineData("2026-11-14 08:00", 0, true)]
    public void The_logon_export_runs_once_per_calendar_day_interval(string? last, int interval, bool due)
    {
        var now = new DateTimeOffset(new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Local));
        var state = last is null ? null : new AutoExportState(new DateTimeOffset(DateTime.Parse(last)), @"\server\share");
        Assert.Equal(due, AutoExport.IsDue(state, now, interval, @"\server\share"));
        // A new folder in the company settings exports at once.
        Assert.True(AutoExport.IsDue(state ?? new AutoExportState(now, "x"), now, interval, @"\server\other"));
    }

    [Fact]
    public void The_logon_export_waits_for_the_folder_and_parses()
    {
        using var temp = new TempDir();
        var waits = new List<TimeSpan>();
        Assert.True(AutoExport.WaitForFolder(temp.Path, TimeSpan.FromMinutes(2), waits.Add));
        Assert.Empty(waits);
        Assert.False(AutoExport.WaitForFolder(Path.Combine(temp.Path, "x"), TimeSpan.FromSeconds(30), waits.Add));
        Assert.Equal(3, waits.Count);
        Assert.Equal(new CliCommand(CliAction.AutoExport), CliCommand.Parse(["--auto-export"]));
        Assert.Equal(new CliCommand(CliAction.ExportExtensions, Path: @"C:\x\"), CliCommand.Parse(["--export-extensions", @"C:\x\"]));
        Assert.Equal(1, AppSettings.Default.InventoryIntervalDays);
    }
}
