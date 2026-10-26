using System.Security.AccessControl;
using Microsoft.Win32;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.Core.Tests;

public class UninstallTests
{
    [Fact]
    public void Provider_hides_system_components_updates_and_entries_without_names()
    {
        using var test = new TestRegistryKey();
        using (var app = test.Create(@"Uninstall\App1"))
        {
            app.SetValue("DisplayName", "測試程式");
            app.SetValue("UninstallString", "\"C:\\Program Files\\Test\\uninst.exe\"");
            app.SetValue("Publisher", "測試公司");
            app.SetValue("DisplayVersion", "1.2.3");
            app.SetValue("InstallDate", "20250102");
            app.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
            app.SetValue("InstallLocation", "\"C:\\Program Files\\Test\"");
        }
        using (var sys = test.Create(@"Uninstall\Sys"))
        {
            sys.SetValue("DisplayName", "系統元件");
            sys.SetValue("UninstallString", "x.exe");
            sys.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
        }
        using (var update = test.Create(@"Uninstall\Upd"))
        {
            update.SetValue("DisplayName", "更新");
            update.SetValue("UninstallString", "x.exe");
            update.SetValue("ParentKeyName", "App1");
        }
        using (var hotfix = test.Create(@"Uninstall\Patch"))
        {
            hotfix.SetValue("DisplayName", "修補程式");
            hotfix.SetValue("UninstallString", "x.exe");
            hotfix.SetValue("ReleaseType", "Hotfix");
        }
        using (var nameless = test.Create(@"Uninstall\NoName")) nameless.SetValue("UninstallString", "x.exe");

        var provider = new InstalledAppProvider([new UninstallRoot(RegistryHive.CurrentUser, RegistryView.Default, $@"{test.KeyPath}\Uninstall", "測試")]);
        var app1 = Assert.Single(provider.GetApps());

        Assert.Equal("測試程式", app1.DisplayName);
        Assert.Equal("1.2.3", app1.Version);
        Assert.Equal(new DateOnly(2025, 1, 2), app1.InstallDate);
        Assert.Equal(2048L * 1024, app1.EstimatedSizeBytes);
        Assert.Equal(@"C:\Program Files\Test", app1.InstallLocation);
        Assert.Equal($@"{test.KeyPath}\Uninstall\App1", app1.KeyPath);
    }

    [Fact]
    public void Leftovers_are_exact_name_folders_outside_protected_and_shared_locations()
    {
        using var temp = new TempDir();
        var folders = temp.Folders();
        var installDir = temp.Dir("Program Files", "Foo App");
        temp.File(@"Program Files\Foo App\left.dll", 100);
        var userData = temp.Dir("Users", "tester", "AppData", "Roaming", "Foo App");
        temp.Dir("ProgramData", "Foo App", "Shared");

        var app = NewApp("Foo App 1.2.3", folders.ProgramFiles, publisher: "Foo Corp");
        var other = NewApp("Other", Path.Combine(folders.ProgramData, "Foo App", "Shared")) with { KeyPath = @"Uninstall\Other" };
        var existingKeys = new HashSet<string> { app.KeyPath, @"Software\Foo Corp\Foo App" };
        var scanner = new LeftoverScanner(folders, (hive, view, path) => hive == RegistryHive.CurrentUser && existingKeys.Contains(path));

        var leftovers = scanner.Scan(app, [app, other]);

        var program = Assert.Single(leftovers, l => l.FolderPath == installDir);
        Assert.True(program.DefaultSelected);
        Assert.Equal(100, program.SizeBytes);
        Assert.False(Assert.Single(leftovers, l => l.FolderPath == userData).DefaultSelected);
        Assert.DoesNotContain(leftovers, l => l.FolderPath == folders.ProgramFiles);
        Assert.DoesNotContain(leftovers, l => l.FolderPath?.StartsWith(folders.ProgramData) == true);
        Assert.Contains(leftovers, l => l.Kind == LeftoverKind.RegistryKey && l.KeyPath == app.KeyPath && l.DefaultSelected);
        Assert.Contains(leftovers, l => l.KeyPath == @"Software\Foo Corp\Foo App" && !l.DefaultSelected);
    }

    [Fact]
    public void Msi_entries_are_never_proposed_as_orphaned_registry_keys()
    {
        using var temp = new TempDir();
        var app = NewApp("Msi App", "") with { IsWindowsInstaller = true };
        var scanner = new LeftoverScanner(temp.Folders(), (_, _, _) => true);
        Assert.DoesNotContain(scanner.Scan(app, [app]), l => l.KeyPath == app.KeyPath);
    }

    [Fact]
    public void An_uninstaller_counts_as_missing_only_for_a_non_msi_entry_with_a_full_path()
    {
        using var temp = new TempDir();
        var existing = temp.File("uninst.exe");
        Assert.True(UninstallCommand.IsUninstallerMissing(NewApp("Gone", "")));
        Assert.False(UninstallCommand.IsUninstallerMissing(NewApp("Here", "") with { UninstallString = $"\"{existing}\" /S" }));
        Assert.False(UninstallCommand.IsUninstallerMissing(NewApp("Msi", "") with { IsWindowsInstaller = true }));
        Assert.False(UninstallCommand.IsUninstallerMissing(NewApp("Shell", "") with { UninstallString = "rundll32.exe foo.dll,Remove" }));
    }

    [Fact]
    public void An_uninstaller_in_a_folder_this_account_cannot_open_is_not_called_missing()
    {
        using var temp = new TempDir();
        var folder = new DirectoryInfo(temp.Dir("cache"));
        var setup = new FileInfo(temp.File(@"cache\setup.exe"));
        // Like Intel's package cache: the folder cannot be listed and the file's attributes cannot be read.
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var denyList = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var denyRead = new FileSystemAccessRule(me, FileSystemRights.ReadAttributes, AccessControlType.Deny);
        var folderAcl = folder.GetAccessControl();
        var fileAcl = setup.GetAccessControl();
        folderAcl.AddAccessRule(denyList);
        fileAcl.AddAccessRule(denyRead);
        setup.SetAccessControl(fileAcl);
        folder.SetAccessControl(folderAcl);
        try
        {
            Assert.Equal(PathState.Unknown, PathProbe.State(setup.FullName));
            Assert.False(UninstallCommand.IsUninstallerMissing(NewApp("Locked", "") with { UninstallString = $"\"{setup.FullName}\" -uninstall" }));
        }
        finally
        {
            folderAcl.RemoveAccessRule(denyList);
            folder.SetAccessControl(folderAcl);
            fileAcl.RemoveAccessRule(denyRead);
            setup.SetAccessControl(fileAcl);
        }
    }

    [Theory]
    [InlineData("Foo 1.2.3", new[] { "Foo 1.2.3", "Foo" })]
    [InlineData("Bar 2.0 (x64)", new[] { "Bar 2.0 (x64)", "Bar" })]
    [InlineData("Baz", new[] { "Baz" })]
    public void Candidate_names_drop_version_suffixes(string displayName, string[] expected) =>
        Assert.Equal(expected, LeftoverScanner.CandidateNames(NewApp(displayName, "")));

    private static InstalledApp NewApp(string name, string installLocation, string publisher = "") => new()
    {
        DisplayName = name,
        Publisher = publisher,
        InstallLocation = installLocation,
        UninstallString = "\"C:\\does-not-exist\\uninst.exe\"",
        Hive = RegistryHive.CurrentUser,
        View = RegistryView.Default,
        KeyPath = @"Uninstall\" + name,
    };
}
