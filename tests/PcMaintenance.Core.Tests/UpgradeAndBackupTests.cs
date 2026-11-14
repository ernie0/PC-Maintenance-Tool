using PcMaintenance.Core.Common;
using PcMaintenance.Core.Startup;
using PcMaintenance.Core.SystemInfo;

namespace PcMaintenance.Core.Tests;

public class UpgradeAndBackupTests
{
    private static Windows11Inputs Ready() => new(2, true, false, "Intel(R) Core(TM) i5-8500 CPU @ 3.00GHz", null, true, 8UL << 30, 256L * 1024 * 1024 * 1024);

    [Fact]
    public void A_windows_10_pc_with_supported_hardware_can_upgrade()
    {
        var result = Windows11Readiness.Evaluate(Ready());
        Assert.Equal(Windows11State.CanUpgrade, result.State);
        Assert.Equal("可以升級", result.Summary);
        Assert.Contains("可以升級到 Windows 11", result.Concern);
        // Secure Boot only has to be possible, not switched on.
        Assert.Equal("支援，但目前沒有啟用", Assert.Single(result.Checks, c => c.Item == "安全開機").Detail);
    }

    [Fact]
    public void Missing_requirements_are_named()
    {
        var old = Ready() with { TpmVersion = 1, Uefi = false, SecureBoot = null, ProcessorName = "Intel(R) Core(TM) i7-4790 CPU @ 3.60GHz", InstalledMemoryBytes = 4UL << 30 };
        var result = Windows11Readiness.Evaluate(old);
        Assert.Equal(Windows11State.CannotUpgrade, result.State);
        Assert.Equal("不能升級：TPM 2.0、UEFI 開機、安全開機、處理器", result.Summary);
        Assert.Contains("需要更換零件或換機", result.Concern);
    }

    [Fact]
    public void Windows_own_appraisal_decides_the_processor_and_unknown_parts_are_said_so()
    {
        Assert.Equal(false, Windows11Readiness.Evaluate(Ready() with { AppraiserRedReasons = ["CpuFms", "TPM"] }).Checks.Single(c => c.Item == "處理器").Pass);
        Assert.Equal(true, Windows11Readiness.Evaluate(Ready() with { ProcessorName = "Intel(R) Xeon(R) CPU E5-2620", AppraiserRedReasons = [] })
            .Checks.Single(c => c.Item == "處理器").Pass);
        var unknown = Windows11Readiness.Evaluate(Ready() with { ProcessorName = "Intel(R) Pentium(R) Gold G5400", TpmVersion = -1 });
        Assert.Equal(Windows11State.Unknown, unknown.State);
        Assert.Equal("無法判斷：TPM 2.0、處理器", unknown.Summary);
        Assert.Equal(Windows11State.CannotUpgrade, Windows11Readiness.Evaluate(Ready() with { Is64BitWindows = false }).State);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz", true)]
    [InlineData("Intel(R) Core(TM) i5-1135G7 @ 2.40GHz", true)]
    [InlineData("Intel(R) Core(TM) i3-1005G1 CPU @ 1.20GHz", true)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400", true)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", true)]
    [InlineData("Intel(R) Core(TM) 5 120U", true)]
    [InlineData("Intel(R) Core(TM) i7-7700HQ CPU @ 2.80GHz", false)]
    [InlineData("Intel(R) Core(TM) i7-920 CPU @ 2.67GHz", false)]
    [InlineData("Intel(R) Core(TM)2 Duo CPU E8400", false)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", true)]
    [InlineData("AMD Ryzen 7 PRO 4750U with Radeon Graphics", true)]
    [InlineData("AMD Ryzen Threadripper 2950X 16-Core Processor", true)]
    [InlineData("AMD Ryzen 7 2700X Eight-Core Processor", true)]
    [InlineData("AMD Ryzen 5 2400G with Radeon Vega Graphics", null)]
    [InlineData("AMD Ryzen 5 1600 Six-Core Processor", false)]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", true)]
    [InlineData("AMD FX(tm)-8350 Eight-Core Processor", false)]
    [InlineData("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", null)]
    public void Processor_families_are_judged_by_model(string name, bool? supported) =>
        Assert.Equal(supported, Windows11Readiness.ProcessorFamily(name));

    [Fact]
    public void Windows_11_is_told_by_build_and_this_pc_reads()
    {
        Assert.True(Windows11Readiness.IsWindows11Build("26200.7462"));
        Assert.False(Windows11Readiness.IsWindows11Build("19045.6456"));
        var info = SystemInfoReader.Read();
        var result = Windows11Readiness.Read(info);
        Assert.Equal(Windows11Readiness.IsWindows11Build(info.Os.Build), result.IsWindows11);
    }

    [Fact]
    public void Backups_are_found_from_onedrive_folders_file_history_and_software()
    {
        var folders = new List<(string, string)> { ("桌面", @"C:\Users\a\OneDrive\Desktop"), ("文件", @"C:\Users\a\Documents"), ("圖片", @"C:\Users\a\OneDrive - 公司\Pictures") };
        var status = BackupStatus.Evaluate([@"C:\Users\a\OneDrive", @"C:\Users\a\OneDrive - 公司"], folders, false,
            ["Synology Drive Client", "7-Zip 26.06", "Google Chrome", "Synology Active Backup for Business Agent"]);
        Assert.Equal(["桌面", "圖片"], status.OneDriveFolders);
        Assert.Equal("OneDrive 備份：桌面、圖片；備份或同步軟體：Synology Active Backup for Business Agent、Synology Drive Client", status.Summary);

        var none = BackupStatus.Evaluate([], folders, false, ["7-Zip"]);
        Assert.False(none.AnyFound);
        Assert.Equal(BackupStatus.NothingFound, none.Summary);
        Assert.Equal("檔案歷程記錄", BackupStatus.Evaluate([], folders, true, []).Summary);
        Assert.Equal("", BackupStatus.Unknown.Summary);
        Assert.True(ComputerHealth.Read(isElevated: false).Backup.Read);
    }

    [Fact]
    public void Signatures_tell_signed_catalog_signed_unsigned_and_tampered_files()
    {
        // The test host is signed by Microsoft in the file itself; cmd.exe only through the system catalogs.
        var host = FileSignatures.Check(Environment.ProcessPath!);
        Assert.Equal(SignatureState.Valid, host.State);
        Assert.True(host.IsMicrosoft, host.Signer);
        var cmd = FileSignatures.Check(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        Assert.Equal(SignatureState.Valid, cmd.State);
        Assert.True(cmd.IsMicrosoft, cmd.Signer);

        using var temp = new TempDir();
        Assert.Equal(SignatureState.Unsigned, FileSignatures.Check(temp.File("tool.exe", 4096)).State);
        var copy = Path.Combine(temp.Path, "host-copy.exe");
        File.Copy(Environment.ProcessPath!, copy);
        using (var stream = File.Open(copy, FileMode.Open))
        {
            stream.Position = 0x600;
            var b = stream.ReadByte();
            stream.Position = 0x600;
            stream.WriteByte((byte)(b ^ 0xFF));
        }
        Assert.Equal(SignatureState.Invalid, FileSignatures.Check(copy).State);
        Assert.Equal(SignatureState.Unknown, FileSignatures.Check(Path.Combine(temp.Path, "missing.exe")).State);
    }

    [Fact]
    public void Startup_entries_are_marked_for_risky_folders_and_signatures()
    {
        var risky = new List<(string, string)> { (@"C:\Users\a\AppData\Local\Temp", "暫存資料夾"), (@"C:\Users\a\Downloads", "下載資料夾") };
        StartupItem Item(string path, bool microsoft = false) => new() { Key = path, Source = StartupSource.RegistryRun, Name = "x", ExecutablePath = path, IsMicrosoft = microsoft };
        var valid = new FileSignature(SignatureState.Valid, "Google LLC");
        Assert.Null(StartupRisk.Describe(Item(@"C:\Program Files\Google\update.exe"), valid, risky));
        Assert.Equal("從暫存資料夾執行；沒有數位簽章",
            StartupRisk.Describe(Item(@"C:\Users\a\AppData\Local\Temp\x1\run.exe"), new FileSignature(SignatureState.Unsigned, ""), risky));
        Assert.Equal("標示為 Microsoft，但數位簽章是「Google LLC」", StartupRisk.Describe(Item(@"C:\x\svc.exe", microsoft: true), valid, risky));
        Assert.Equal("標示為 Microsoft，但沒有有效的 Microsoft 數位簽章",
            StartupRisk.Describe(Item(@"C:\x\svc.exe", microsoft: true), new FileSignature(SignatureState.Invalid, ""), risky));
        Assert.Equal("數位簽章無效", StartupRisk.Describe(Item(@"C:\x\a.exe"), new FileSignature(SignatureState.Invalid, "A"), risky));
        Assert.Null(StartupRisk.Describe(Item(@"C:\x\a.exe"), FileSignature.Unknown, risky));
        Assert.NotEmpty(StartupRisk.RiskyFolders());
    }
}
