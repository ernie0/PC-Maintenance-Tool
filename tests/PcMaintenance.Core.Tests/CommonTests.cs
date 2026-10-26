using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.Core.Tests;

public class CommonTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(150L * 1024 * 1024, "150 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void ByteSize_formats(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Theory]
    [InlineData(@"C:\Temp", @"C:\Temp\a.txt", true)]
    [InlineData(@"C:\Temp", @"c:\temp", true)]
    [InlineData(@"C:\Temp", @"C:\Temp2\a.txt", false)]
    [InlineData(@"C:\", @"C:\Windows", true)]
    [InlineData(@"C:\Temp\Sub", @"C:\Temp", false)]
    public void IsSameOrUnder_respects_folder_boundaries(string root, string path, bool expected) =>
        Assert.Equal(expected, PathSafety.IsSameOrUnder(root, path));

    [Fact]
    public void HasReparsePointOnPath_detects_a_junction_between_root_and_file()
    {
        using var temp = new TempDir();
        var root = temp.Dir("root");
        temp.Dir("root", "plain");
        var outside = temp.Dir("outside");
        temp.Junction(@"root\link", outside);
        Assert.True(PathSafety.HasReparsePointOnPath(root, Path.Combine(root, "link", "victim.txt")));
        Assert.False(PathSafety.HasReparsePointOnPath(root, Path.Combine(root, "plain", "file.txt")));
        Assert.True(PathSafety.HasReparsePointOnPath(root, Path.Combine(outside, "file.txt")));
    }

    [Fact]
    public void Split_handles_quoted_paths() =>
        Assert.Equal((@"C:\Program Files\Foo\foo.exe", "/S"), CommandLine.Split("\"C:\\Program Files\\Foo\\foo.exe\" /S", _ => false));

    [Fact]
    public void Split_finds_the_existing_executable_in_an_unquoted_path_with_spaces()
    {
        const string exe = @"C:\Program Files\Foo Bar\uninst.exe";
        Assert.Equal((exe, "/quiet"), CommandLine.Split(exe + " /quiet", p => p == exe));
    }

    [Fact]
    public void Split_keeps_bare_program_names() =>
        Assert.Equal(("rundll32.exe", "shell32.dll,Control_RunDLL"), CommandLine.Split("rundll32.exe shell32.dll,Control_RunDLL", _ => false));

    [Fact]
    public void ExtractExecutable_maps_service_style_prefixes()
    {
        var folders = SystemFolders.Current with { WindowsDir = @"C:\Windows" };
        Assert.Equal(@"C:\Windows\System32\drivers\foo.sys", CommandLine.ExtractExecutable(@"\SystemRoot\System32\drivers\foo.sys", folders, _ => false));
        Assert.Equal(@"C:\Tools\svc.exe", CommandLine.ExtractExecutable(@"\??\C:\Tools\svc.exe -run", folders, _ => false));
    }

    [Theory]
    [InlineData("MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}", "msiexec.exe", "/x {12345678-1234-1234-1234-123456789ABC}")]
    [InlineData("MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC} /qn", "msiexec.exe", "/x {12345678-1234-1234-1234-123456789ABC}")]
    [InlineData("\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT", @"C:\Program Files\Foo\unins000.exe", "/SILENT")]
    public void UninstallCommand_rewrites_msi_repair_to_remove(string input, string file, string args) =>
        Assert.Equal((file, args), UninstallCommand.Parse(input, _ => false));

    [Fact]
    public void Settings_accept_comments_trailing_commas_and_null_lists()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, """
            {
              // 公司共用設定
              "minFileAgeHours": 48,
              "excludedPaths": ["D:\\公司資料"],
              "disabledJunkCategories": null,
            }
            """);
        var result = SettingsLoader.Load(Path.Combine(temp.Path, "missing.json"), file);
        Assert.Equal(file, result.SourcePath);
        Assert.Null(result.Warning);
        Assert.Equal(48, result.Settings.MinFileAgeHours);
        Assert.Empty(result.Settings.DisabledJunkCategories);
        Assert.True(result.Settings.IsExcluded(@"D:\公司資料\報表.xlsx"));
        Assert.False(result.Settings.IsExcluded(@"D:\公司資料2\報表.xlsx"));
    }

    [Fact]
    public void Shipped_settings_example_parses_to_the_defaults()
    {
        var result = SettingsLoader.Load(Path.Combine(AppContext.BaseDirectory, "settings.example.json"));
        Assert.Null(result.Warning);
        Assert.NotNull(result.SourcePath);
        var loaded = result.Settings;
        Assert.Empty(loaded.DisabledJunkCategories);
        Assert.Empty(loaded.ExcludedPaths);
        Assert.Equal(AppSettings.Default with { DisabledJunkCategories = loaded.DisabledJunkCategories, ExcludedPaths = loaded.ExcludedPaths }, loaded);
    }

    [Fact]
    public void Settings_fall_back_to_defaults_on_invalid_json()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, "{ not json");
        var result = SettingsLoader.Load(file);
        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.NotNull(result.Warning);
    }

    [Theory]
    [InlineData("@oem114.inf,%PlatformLicenseManagerServiceName%;Intel(R) Platform License Manager Service", "Intel(R) Platform License Manager Service")]
    [InlineData("Plain Service Name", "Plain Service Name")]
    public void IndirectString_falls_back_to_the_inf_default_name(string raw, string expected) =>
        Assert.Equal(expected, IndirectString.Resolve(raw));

    [Fact]
    public void UserMessage_replaces_english_runtime_text_but_keeps_chinese()
    {
        Assert.Equal("權限不足", UserMessage.For(new System.Security.SecurityException("Requested registry access is not allowed.")));
        Assert.Equal("檔案或資料夾正在使用中，或無法存取", UserMessage.For(new IOException("The process cannot access the file")));
        Assert.Equal("此服務不允許變更", UserMessage.For(new InvalidOperationException("此服務不允許變更")));
        var json = Assert.ThrowsAny<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\n  \"minFileAgeHours\": ,\n}"));
        Assert.Equal("設定檔格式錯誤（第 2 行）", UserMessage.For(json));
    }

    [Fact]
    public void Journal_round_trips_and_marks_restored()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "journal.json");
        var record = new ChangeRecord
        {
            Kind = ChangeKind.StartupRegistry,
            Target = "測試",
            Description = "停用開機啟動",
            RestoreData = new() { ["value"] = "Foo" },
        };
        new ChangeJournal(file).Add(record);

        var reloaded = new ChangeJournal(file);
        Assert.Equal(record.Id, Assert.Single(reloaded.Records).Id);
        reloaded.MarkRestored(record.Id);

        var restored = Assert.Single(new ChangeJournal(file).Records);
        Assert.True(restored.Restored);
        Assert.Equal("Foo", restored.RestoreData["value"]);
    }

    [Fact]
    public void Journal_keeps_an_unreadable_file_instead_of_overwriting_it()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "journal.json");
        File.WriteAllText(file, "corrupted");
        var journal = new ChangeJournal(file);
        Assert.Empty(journal.Records);
        Assert.Single(Directory.GetFiles(temp.Path, "journal.json.broken-*"));
    }
}
