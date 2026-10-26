using PcMaintenance.Core.Common;
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
}
