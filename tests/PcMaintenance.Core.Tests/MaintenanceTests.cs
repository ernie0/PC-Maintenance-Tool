using Microsoft.Win32;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.DiskAnalysis;
using PcMaintenance.Core.Journal;
using PcMaintenance.Core.Maintenance;
using PcMaintenance.Core.Shortcuts;
using PcMaintenance.Core.Tweaks;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.Core.Tests;

public class MaintenanceTests
{
    [Fact]
    public void A_shortcut_whose_target_was_deleted_is_found_and_working_ones_are_not()
    {
        using var temp = new TempDir();
        var links = temp.Dir("links");
        var kept = temp.File(@"targets\kept.exe");
        var gone = temp.File(@"targets\gone.exe");
        ShellLink.Create(Path.Combine(links, "kept.lnk"), kept);
        ShellLink.Create(Path.Combine(links, "gone.lnk"), gone);
        ShellLink.Create(Path.Combine(links, "folder.lnk"), temp.Dir("targets"));
        File.Delete(gone);

        var broken = ShortcutScanner.Scan([new ShortcutFolder(links, "測試", Recursive: false, AllUsers: false)]);

        var item = Assert.Single(broken);
        Assert.Equal("gone", item.Name);
        Assert.Equal(gone, item.Target, ignoreCase: true);
    }

    [Fact]
    public void Shortcuts_that_cannot_be_judged_are_never_called_broken()
    {
        Assert.Null(ShortcutScanner.MissingTarget(new ShellLinkInfo(@"\\server\share\app.exe", "", false), _ => PathState.Missing));
        Assert.Null(ShortcutScanner.MissingTarget(new ShellLinkInfo(@"C:\Windows\Installer\{1}\app.ico", "", IsAdvertised: true), _ => PathState.Missing));
        Assert.Null(ShortcutScanner.MissingTarget(new ShellLinkInfo(@"C:\Locked\app.exe", "", false), _ => PathState.Unknown));
    }

    [Fact]
    public void A_target_found_in_the_other_program_files_folder_counts_as_present()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var link = new ShellLinkInfo(Path.Combine(programFiles, "Vendor", "app.exe"), "", false);

        Assert.Null(ShortcutScanner.MissingTarget(link,
            path => path.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase) ? PathState.Exists : PathState.Missing));
    }

    [Fact]
    public void Only_the_outermost_empty_folders_are_reported()
    {
        using var temp = new TempDir();
        var root = temp.Dir("data");
        temp.Dir(@"data\a");
        temp.Dir(@"data\b\c");
        temp.File(@"data\d\file.txt");
        temp.Dir(@"data\d\e");
        File.SetAttributes(temp.Dir(@"data\h\inner"), FileAttributes.Directory | FileAttributes.Hidden);
        File.SetAttributes(temp.File(@"data\i\desktop.ini"), FileAttributes.Hidden);
        temp.Junction(@"data\j", temp.Dir("elsewhere"));
        temp.Dir(@"data\.cache\empty");

        var found = EmptyFolderFinder.Find(root, temp.Folders());

        Assert.Equal(
            [new EmptyFolder(Path.Combine(root, "a"), 0), new EmptyFolder(Path.Combine(root, "b"), 1), new EmptyFolder(Path.Combine(root, "d", "e"), 0)],
            found);
    }

    [Fact]
    public void A_tweak_is_switched_and_restored_exactly_including_keys_it_created()
    {
        using var key = new TestRegistryKey();
        using var temp = new TempDir();
        using (var existing = key.Create("existing")) existing.SetValue("Mode", 5, RegistryValueKind.DWord);
        var tweak = Tweak(
            new RegistryChange(RegistryHive.CurrentUser, $@"{key.KeyPath}\existing", "Mode", 0, 1),
            new RegistryChange(RegistryHive.CurrentUser, $@"{key.KeyPath}\created\deeper", "Flag", 1));
        var journal = new ChangeJournal(Path.Combine(temp.Path, "journal.json"));
        var engine = new TweakEngine(journal);
        Assert.False(TweakEngine.IsOn(tweak));

        var record = engine.Apply(tweak, on: true);
        Assert.True(TweakEngine.IsOn(tweak));

        engine.Restore(record);
        using var root = Registry.CurrentUser.OpenSubKey(key.KeyPath)!;
        using (var existing = root.OpenSubKey("existing")!) Assert.Equal(5, existing.GetValue("Mode"));
        Assert.Null(root.OpenSubKey("created"));
        Assert.True(Assert.Single(journal.Records).Restored);
    }

    [Fact]
    public void Turning_a_tweak_off_can_remove_a_key_and_restoring_brings_it_back()
    {
        using var key = new TestRegistryKey();
        using var temp = new TempDir();
        var clsid = $@"{key.KeyPath}\clsid";
        var tweak = Tweak(new RegistryChange(RegistryHive.CurrentUser, $@"{clsid}\inproc", "", "", OffDeletesKey: clsid));
        var engine = new TweakEngine(new ChangeJournal(Path.Combine(temp.Path, "journal.json")));

        engine.Apply(tweak, on: true);
        var off = engine.Apply(tweak, on: false);
        Assert.False(TweakEngine.IsOn(tweak));
        using (var gone = Registry.CurrentUser.OpenSubKey(clsid)) Assert.Null(gone);

        engine.Restore(off);
        Assert.True(TweakEngine.IsOn(tweak));
    }

    [Theory]
    [InlineData(397568u, true, true)]
    [InlineData(393472u, false, true)]
    [InlineData(397584u, true, false)]
    public void Security_center_states_are_decoded(uint state, bool enabled, bool upToDate)
    {
        var info = SecurityStatus.Decode("防毒", state);
        Assert.Equal(enabled, info.Enabled);
        Assert.Equal(upToDate, info.UpToDate);
    }

    [Fact]
    public void Power_plans_are_read_from_powercfg_output_in_any_language()
    {
        var plans = PowerPlans.Parse("""
            現有電源配置 (* 使用中)
            -----------------------------------
            電源配置 GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (平衡) *
            電源配置 GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (高效能)
            """);

        Assert.Equal(["平衡", "高效能"], plans.Select(p => p.Name));
        Assert.Equal([true, false], plans.Select(p => p.IsActive));
    }

    [Fact]
    public async Task This_computer_has_an_active_power_plan()
    {
        var plans = await PowerPlans.ListAsync();
        Assert.Single(plans, p => p.IsActive);
    }

    [Fact]
    public void Store_app_list_reads_a_one_app_array_and_publisher_names()
    {
        var app = Assert.Single(StoreApps.Parse("""
            [{"Name":"Microsoft.BingNews","Publisher":"CN=Microsoft Corporation, O=Microsoft Corporation","Version":"4.1.0.0",
              "PackageFullName":"Microsoft.BingNews_4.1.0.0_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.BingNews_8wekyb3d8bbwe","InstallLocation":null}]
            """));
        Assert.Equal("Microsoft.BingNews_8wekyb3d8bbwe", app.PackageFamilyName);
        Assert.Equal("Microsoft Corporation", StoreApps.PublisherName(app.Publisher));
    }

    private static Tweak Tweak(params RegistryChange[] changes) => new()
    {
        Id = "test",
        Group = TweakGroup.Privacy,
        Name = "測試設定",
        Description = "",
        Changes = changes,
    };
}
