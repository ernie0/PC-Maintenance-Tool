using Microsoft.Win32;
using PcMaintenance.Core.Journal;
using PcMaintenance.Core.Startup;

namespace PcMaintenance.Core.Tests;

public class StartupTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    public void StartupApproved_reads_the_task_manager_flag(byte[]? value, bool enabled) =>
        Assert.Equal(enabled, StartupApproved.IsEnabled(value));

    [Fact]
    public void StartupApproved_encodes_disabled_with_a_timestamp()
    {
        var now = new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);
        var disabled = StartupApproved.Encode(false, now);
        Assert.Equal(12, disabled.Length);
        Assert.Equal(0x03, disabled[0]);
        Assert.Equal(now.ToFileTimeUtc(), BitConverter.ToInt64(disabled, 4));
        Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupApproved.Encode(true, now));
    }

    [Fact]
    public void RunKey_entries_can_be_disabled_and_restored_without_touching_the_run_value()
    {
        using var test = new TestRegistryKey();
        using (var run = test.Create("Run")) run.SetValue("TestApp", "\"C:\\Windows\\notepad.exe\" /min");
        var location = new RunKeyLocation(RegistryHive.CurrentUser, RegistryView.Default, $@"{test.KeyPath}\Run",
            RegistryView.Default, $@"{test.KeyPath}\Approved", "測試", false);
        var provider = new RunKeyProvider([location]);

        var item = Assert.Single(provider.Enumerate());
        Assert.True(item.Enabled);
        Assert.Equal(@"C:\Windows\notepad.exe", item.ExecutablePath);

        var restore = provider.SetEnabled(item, false);
        Assert.False(Assert.Single(provider.Enumerate()).Enabled);
        using (var run = Registry.CurrentUser.OpenSubKey($@"{test.KeyPath}\Run"))
            Assert.Equal("\"C:\\Windows\\notepad.exe\" /min", run!.GetValue("TestApp"));

        provider.Restore(restore);
        Assert.True(Assert.Single(provider.Enumerate()).Enabled);
        using var approved = Registry.CurrentUser.OpenSubKey($@"{test.KeyPath}\Approved");
        Assert.Null(approved!.GetValue("TestApp"));
    }

    [Fact]
    public void Manager_journals_changes_and_restores_them()
    {
        using var temp = new TempDir();
        using var test = new TestRegistryKey();
        using (var run = test.Create("Run")) run.SetValue("TestApp", @"C:\Tools\tool.exe");
        var provider = new RunKeyProvider([new RunKeyLocation(RegistryHive.CurrentUser, RegistryView.Default,
            $@"{test.KeyPath}\Run", RegistryView.Default, $@"{test.KeyPath}\Approved", "測試", false)]);
        var journal = new ChangeJournal(Path.Combine(temp.Path, "journal.json"));
        var manager = new StartupManager(journal, [provider]);

        var item = Assert.Single(manager.Load().Items);
        var record = manager.SetEnabled(item, false);
        Assert.Equal(ChangeKind.StartupRegistry, Assert.Single(journal.Records).Kind);
        Assert.False(Assert.Single(manager.Load().Items).Enabled);

        manager.Restore(record);
        Assert.True(Assert.Single(manager.Load().Items).Enabled);
        Assert.True(Assert.Single(journal.Records).Restored);
    }

    [Fact]
    public void Items_that_may_not_change_are_refused()
    {
        using var temp = new TempDir();
        var manager = new StartupManager(new ChangeJournal(Path.Combine(temp.Path, "j.json")), [new RunKeyProvider([])]);
        var item = new StartupItem { Key = "x", Source = StartupSource.RegistryRun, Name = "x", CanChange = false };
        Assert.Throws<InvalidOperationException>(() => manager.SetEnabled(item, false));
    }

    [Theory]
    [InlineData(@"\Foo", @"\", "Foo")]
    [InlineData(@"\Vendor\Updater\Daily", @"\Vendor\Updater", "Daily")]
    public void Task_paths_split_into_folder_and_name(string path, string folder, string name)
    {
        Assert.Equal(folder, ScheduledTaskProvider.FolderOf(path));
        Assert.Equal(name, ScheduledTaskProvider.NameOf(path));
    }

    [Theory]
    [InlineData(2, false, ServiceStartType.Automatic)]
    [InlineData(2, true, ServiceStartType.AutomaticDelayed)]
    [InlineData(3, false, ServiceStartType.Manual)]
    [InlineData(4, false, ServiceStartType.Disabled)]
    [InlineData(0, false, ServiceStartType.Boot)]
    public void Service_start_values_map_to_types(int start, bool delayed, ServiceStartType expected) =>
        Assert.Equal(expected, ServiceProvider.ToStartType(start, delayed));
}
