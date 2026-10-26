using PcMaintenance.Core.Common;
using PcMaintenance.Core.DiskAnalysis;

namespace PcMaintenance.Core.Tests;

public class DiskAnalysisTests
{
    [Fact]
    public void Folder_sizes_roll_up_and_junctions_are_not_counted()
    {
        using var temp = new TempDir();
        var root = temp.Dir("scan");
        temp.File(@"scan\file0", 10);
        temp.File(@"scan\a\file1", 100);
        temp.File(@"scan\a\b\file2", 200);
        temp.File(@"scan\c\file3", 50);
        var elsewhere = temp.Dir("elsewhere");
        temp.File(@"elsewhere\big", 1000);
        temp.Junction(@"scan\link", elsewhere);

        var result = new FolderSizeScanner().Scan(root);

        Assert.Equal(360, result.Root.Size);
        Assert.Equal(4, result.TotalFiles);
        Assert.Equal(["a", "c"], result.Root.Children.Select(c => c.Name));
        Assert.Equal(300, result.Root.Children[0].Size);
        Assert.Equal(["file2", "file1", "file3", "file0"], result.LargestFiles.Select(f => Path.GetFileName(f.Path)));
    }

    [Theory]
    [InlineData("旅遊.MP4", FileKind.Video)]
    [InlineData("配置圖.dwg", FileKind.Document)]
    [InlineData("backup.tar.gz", FileKind.Archive)]
    [InlineData("README", FileKind.Other)]
    public void Files_are_grouped_by_extension(string name, FileKind kind) => Assert.Equal(kind, FileKinds.Classify(name));

    [Fact]
    public void Folder_scan_totals_space_by_file_kind_largest_first()
    {
        using var temp = new TempDir();
        var root = temp.Dir("scan");
        temp.File(@"scan\a.jpg", 50);
        temp.File(@"scan\sub\b.mp4", 100);
        temp.File(@"scan\sub\c.png", 20);
        temp.File(@"scan\d.bin", 10);

        var kinds = new FolderSizeScanner().Scan(root).Kinds;

        Assert.Equal([FileKind.Video, FileKind.Picture, FileKind.Other], kinds.Select(k => k.Kind));
        Assert.Equal((70L, 2L), (kinds[1].Bytes, kinds[1].Files));
    }

    [Fact]
    public void Duplicates_require_identical_content_not_just_size_or_prefix()
    {
        using var temp = new TempDir();
        var random = new Random(42);
        var content = new byte[100 * 1024];
        random.NextBytes(content);
        var x1 = Path.Combine(temp.Dir("one"), "x.bin");
        var x2 = Path.Combine(temp.Dir("two"), "x-copy.bin");
        File.WriteAllBytes(x1, content);
        File.WriteAllBytes(x2, content);
        var differentTail = (byte[])content.Clone();
        differentTail[^1] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(temp.Path, "y.bin"), differentTail);
        temp.File(@"one\z.txt", 10);
        temp.File(@"two\z.txt", 10);
        temp.File(@"one\empty1", 0);
        temp.File(@"two\empty2", 0);

        var groups = new DuplicateFinder(AppSettings.Default).Find([temp.Path], minSize: 1);

        Assert.Equal(2, groups.Count);
        var big = groups[0];
        Assert.Equal(content.Length, big.Size);
        Assert.Equal([x1, x2], big.Files.Select(f => f.Path).Order());
        Assert.Equal(content.Length, big.WastedBytes);
        Assert.All(groups[1].Files, f => Assert.Equal("z.txt", Path.GetFileName(f.Path)));
    }

    [Fact]
    public void Removing_every_remaining_copy_of_a_group_is_refused()
    {
        using var temp = new TempDir();
        var files = new[] { "a", "b", "c" }.Select(name => temp.File(name)).ToArray();
        var group = new DuplicateGroup(10, "h", files.Select(f => new DuplicateFile(f, 10, File.GetLastWriteTimeUtc(f))).ToList());
        HashSet<string> Pick(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);

        DuplicateRemoval.EnsureOneKept([group], Pick(files[0], files[1]));
        Assert.Throws<InvalidOperationException>(() => DuplicateRemoval.EnsureOneKept([group], Pick(files)));

        // The copy removed in an earlier round must not count as the one that is kept.
        File.Delete(files[2]);
        Assert.Throws<InvalidOperationException>(() => DuplicateRemoval.EnsureOneKept([group], Pick(files[0], files[1])));
    }
}
