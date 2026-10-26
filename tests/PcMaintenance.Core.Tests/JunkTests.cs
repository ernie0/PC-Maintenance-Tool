using PcMaintenance.Core.Common;
using PcMaintenance.Core.Junk;

namespace PcMaintenance.Core.Tests;

public class JunkTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-3);

    private static JunkCategory Category(string root, bool applyAge = true, params string[] blocking) => new()
    {
        Id = "test",
        Name = "測試",
        Description = "",
        Locations = [new JunkLocation(root, applyAge)],
        BlockingProcesses = blocking,
    };

    private static JunkScanner Scanner(AppSettings? settings = null, Func<string, bool>? running = null) =>
        new(settings ?? AppSettings.Default, running ?? (_ => false));

    private static JunkCleaner Cleaner(AppSettings? settings = null, Func<string, bool>? running = null) =>
        new(settings ?? AppSettings.Default, null, running ?? (_ => false));

    [Fact]
    public void Only_files_older_than_the_age_limit_are_scanned_and_deleted()
    {
        using var temp = new TempDir();
        var root = temp.Dir("temp");
        var oldFile = temp.File(@"temp\old.tmp", 100, Old);
        var nested = temp.File(@"temp\sub\old2.tmp", 50, Old);
        var fresh = temp.File(@"temp\fresh.tmp", 30);

        var scan = Scanner().Scan(Category(root), isElevated: false);
        Assert.Equal(2, scan.ItemCount);
        Assert.Equal(150, scan.TotalBytes);

        var result = Cleaner().Clean(scan);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(150, result.FreedBytes);
        Assert.False(File.Exists(oldFile));
        Assert.False(File.Exists(nested));
        Assert.True(File.Exists(fresh));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void A_file_name_pattern_takes_only_matching_files_and_keeps_the_folder()
    {
        using var temp = new TempDir();
        var folder = temp.Dir("Explorer");
        var first = temp.File(@"Explorer\thumbcache_96.db", 20);
        var second = temp.File(@"Explorer\thumbcache_256.db", 30);
        var other = temp.File(@"Explorer\iconcache_32.db", 40);
        var nested = temp.File(@"Explorer\sub\thumbcache_1.db", 50);
        var category = Category(folder) with { Locations = [new JunkLocation(folder, ApplyAgeFilter: false, FileName: "thumbcache_*.db")] };

        var scan = Scanner().Scan(category, false);
        Assert.Equal(50, scan.TotalBytes);

        Assert.Equal(2, Cleaner().Clean(scan).Deleted);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void A_file_changed_after_the_scan_is_left_alone()
    {
        using var temp = new TempDir();
        var root = temp.Dir("temp");
        var file = temp.File(@"temp\a.log", 10, Old);
        var scan = Scanner().Scan(Category(root), false);

        File.AppendAllText(file, "more");
        var result = Cleaner().Clean(scan);

        Assert.Equal(1, result.SkippedChanged);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_file_in_use_is_skipped()
    {
        using var temp = new TempDir();
        var root = temp.Dir("temp");
        var file = temp.File(@"temp\locked.tmp", 10, Old);
        var scan = Scanner().Scan(Category(root), false);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = Cleaner().Clean(scan);
            Assert.Equal(1, result.SkippedInUse);
        }
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Junctions_are_never_followed_and_their_targets_survive()
    {
        using var temp = new TempDir();
        var root = temp.Dir("temp");
        var outside = temp.Dir("important");
        var victim = temp.File(@"important\keep.dat", 10, Old);
        temp.Junction(@"temp\link", outside);
        temp.File(@"temp\junk.tmp", 10, Old);

        var scan = Scanner().Scan(Category(root), false);
        Assert.DoesNotContain(scan.Files, f => f.Path.Contains("keep.dat"));
        Cleaner().Clean(scan);
        Assert.True(File.Exists(victim));

        // Even a forged scan result pointing through the junction must be refused.
        var forged = scan with { Files = [new JunkFile(Path.Combine(root, "link", "keep.dat"), 10, File.GetLastWriteTimeUtc(victim), root)] };
        var result = Cleaner().Clean(forged);
        Assert.Equal(1, result.SkippedUnsafe);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void Excluded_paths_are_not_touched()
    {
        using var temp = new TempDir();
        var root = temp.Dir("temp");
        var keep = temp.File(@"temp\keep\a.tmp", 10, Old);
        temp.File(@"temp\drop.tmp", 10, Old);
        var settings = AppSettings.Default with { ExcludedPaths = [Path.Combine(root, "keep")] };

        var scan = Scanner(settings).Scan(Category(root), false);
        Assert.Equal(1, scan.ItemCount);
        Cleaner(settings).Clean(scan);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void A_running_browser_blocks_cleaning_its_cache()
    {
        using var temp = new TempDir();
        var root = temp.Dir("cache");
        var file = temp.File(@"cache\data_0", 10);
        var category = Category(root, false, "fakebrowser");

        var scan = Scanner(running: name => name == "fakebrowser").Scan(category, false);
        Assert.False(scan.CanClean);
        Assert.Equal(1, scan.ItemCount);
        Assert.NotNull(Cleaner(running: name => name == "fakebrowser").Clean(scan).Note);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Admin_only_categories_are_not_scanned_without_elevation()
    {
        using var temp = new TempDir();
        var root = temp.Dir("wintemp");
        temp.File(@"wintemp\a.tmp", 10, Old);
        var category = Category(root) with { RequiresAdmin = true };

        var scan = Scanner().Scan(category, isElevated: false);
        Assert.False(scan.CanClean);
        Assert.Equal(0, scan.ItemCount);
    }

    [Fact]
    public void Catalog_lists_browser_caches_only_for_installed_browsers_and_honours_disabled_ids()
    {
        using var temp = new TempDir();
        var folders = temp.Folders();
        var edge = Path.Combine(folders.LocalAppData, "Microsoft", "Edge", "User Data");
        Directory.CreateDirectory(Path.Combine(edge, "Default"));
        File.WriteAllText(Path.Combine(edge, "Default", "Preferences"), "{}");
        var settings = AppSettings.Default with { DisabledJunkCategories = ["windows-update-cache"] };

        var catalog = JunkCatalog.Create(settings, folders);

        var edgeCategory = Assert.Single(catalog, c => c.Id == "edge-cache");
        Assert.Contains(edgeCategory.Locations, l => l.Path == Path.Combine(edge, "Default", "Cache"));
        Assert.DoesNotContain(catalog, c => c.Id == "chrome-cache");
        Assert.DoesNotContain(catalog, c => c.Id == "windows-update-cache");
        Assert.False(Assert.Single(catalog, c => c.IsRecycleBin).DefaultSelected);
    }
}
