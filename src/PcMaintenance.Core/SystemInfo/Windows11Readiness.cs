using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PcMaintenance.Core.SystemInfo;

/// <param name="Pass">True meets the requirement, false does not, null could not be told.</param>
public sealed record Windows11Check(string Item, bool? Pass, string Detail);

public enum Windows11State
{
    AlreadyWindows11,
    CanUpgrade,
    CannotUpgrade,
    Unknown,
}

/// <summary>
/// Whether a Windows 10 PC meets the Windows 11 hardware requirements: TPM 2.0, UEFI with Secure Boot, a supported
/// processor, 4 GB of memory and a 64 GB system drive. Windows' own compatibility appraiser decides the processor when it
/// has run; otherwise the processor family is judged by its model name, which may say "unknown".
/// </summary>
public sealed partial record Windows11Readiness(bool IsWindows11, IReadOnlyList<Windows11Check> Checks)
{
    public const int MinimumMemoryGB = 4;
    public const int MinimumSystemDriveGB = 64;

    public Windows11State State =>
        IsWindows11 ? Windows11State.AlreadyWindows11
        : Checks.Any(c => c.Pass == false) ? Windows11State.CannotUpgrade
        : Checks.Any(c => c.Pass is null) ? Windows11State.Unknown
        : Windows11State.CanUpgrade;

    /// <summary>One line for the CSV and the multi-computer page: "可以升級", "不能升級：TPM 2.0、處理器"…</summary>
    public string Summary => State switch
    {
        Windows11State.AlreadyWindows11 => "已是 Windows 11",
        Windows11State.CanUpgrade => "可以升級",
        Windows11State.CannotUpgrade => $"不能升級：{string.Join("、", Checks.Where(c => c.Pass == false).Select(c => c.Item))}",
        _ => $"無法判斷：{string.Join("、", Checks.Where(c => c.Pass is null).Select(c => c.Item))}",
    };

    /// <summary>What to tell about a Windows 10 PC; null on Windows 11.</summary>
    public string? Concern => State switch
    {
        Windows11State.AlreadyWindows11 => null,
        Windows11State.CanUpgrade => $"{EndOfSupport}，這台電腦可以升級到 Windows 11",
        Windows11State.CannotUpgrade => $"{EndOfSupport}，這台電腦的硬體不能升級到 Windows 11（{string.Join("、", Checks.Where(c => c.Pass == false).Select(c => c.Item))}），需要更換零件或換機",
        _ => $"{EndOfSupport}，請確認能否升級到 Windows 11（{string.Join("、", Checks.Where(c => c.Pass is null).Select(c => c.Item))}）",
    };

    public const string EndOfSupport = "Windows 10 已在 2025 年 10 月結束支援，不再有安全性更新";

    public static Windows11Readiness Read(ComputerInfo info)
    {
        if (IsWindows11Build(info.Os.Build)) return new Windows11Readiness(true, []);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', ':') ?? "C";
        var drive = info.Disks.Volumes.FirstOrDefault(v => v.IsSystem)
                    ?? info.Disks.Volumes.FirstOrDefault(v => string.Equals(v.Letter, systemDrive, StringComparison.OrdinalIgnoreCase));
        return Evaluate(new Windows11Inputs(
            TpmVersion(), FirmwareIsUefi(), info.Machine.SecureBoot, info.Processor.Name, AppraiserRedReasons(),
            Environment.Is64BitOperatingSystem, info.Memory.InstalledPhysical, drive?.TotalBytes));
    }

    public static bool IsWindows11Build(string build) =>
        int.TryParse(build.Split('.')[0], out var number) && number >= 22000;

    public static Windows11Readiness Evaluate(Windows11Inputs x)
    {
        var checks = new List<Windows11Check>
        {
            x.TpmVersion switch
            {
                >= 2 => new("TPM 2.0", true, "TPM 2.0"),
                1 => new("TPM 2.0", false, "只有 TPM 1.2，需要 2.0"),
                0 => new("TPM 2.0", false, "找不到 TPM；有些電腦要先到 BIOS／UEFI 設定開啟（Intel PTT 或 AMD fTPM）"),
                _ => new("TPM 2.0", null, "無法讀取 TPM"),
            },
            x.Uefi switch
            {
                true => new("UEFI 開機", true, "UEFI"),
                false => new("UEFI 開機", false, "傳統 BIOS 開機；要先把系統磁碟轉成 GPT（MBR2GPT）並改成 UEFI 開機"),
                null => new("UEFI 開機", null, "無法讀取開機模式"),
            },
            // Windows 11 needs a firmware able to do Secure Boot; it may stay switched off.
            (x.SecureBoot, x.Uefi) switch
            {
                (true, _) => new("安全開機", true, "已啟用"),
                (false, _) => new("安全開機", true, "支援，但目前沒有啟用"),
                (null, false) => new("安全開機", false, "傳統 BIOS 開機不支援安全開機"),
                _ => new("安全開機", null, "無法讀取安全開機狀態"),
            },
            Processor(x),
            x.InstalledMemoryBytes is { } memory
                ? new("記憶體", memory >= MinimumMemoryGB * 1024L * 1024 * 1024 * 0.95, $"{memory / 1024.0 / 1024 / 1024:0.#} GB（至少 {MinimumMemoryGB} GB）")
                : new("記憶體", null, "無法讀取"),
            x.SystemDriveBytes is { } size
                ? new("系統磁碟", size >= MinimumSystemDriveGB * 1000L * 1000 * 1000, $"{size / 1024.0 / 1024 / 1024:0} GB（至少 {MinimumSystemDriveGB} GB）")
                : new("系統磁碟", null, "無法讀取"),
        };
        return new Windows11Readiness(false, checks);
    }

    private static Windows11Check Processor(Windows11Inputs x)
    {
        if (!x.Is64BitWindows) return new("處理器", false, "32 位元的 Windows 不能直接升級，需要重新安裝 64 位元版本");
        // The appraiser lists every unmet requirement; "CpuFms" (family, model, stepping) and "CpuModel" are the processor's.
        if (x.AppraiserRedReasons is { } reasons)
        {
            return reasons.Any(r => r.StartsWith("Cpu", StringComparison.OrdinalIgnoreCase))
                ? new("處理器", false, $"{x.ProcessorName}不在 Windows 11 支援清單（Windows 相容性檢查的結果）")
                : new("處理器", true, $"{x.ProcessorName}（Windows 相容性檢查的結果）");
        }
        return ProcessorFamily(x.ProcessorName) switch
        {
            true => new("處理器", true, $"{x.ProcessorName}（依型號判斷）"),
            false => new("處理器", false, $"{x.ProcessorName}太舊，不在 Windows 11 支援清單（依型號判斷）"),
            null => new("處理器", null, $"無法從型號判斷「{x.ProcessorName}」，請用 Microsoft 的「電腦健康情況檢查」確認"),
        };
    }

    /// <summary>
    /// Windows 11 supports Intel Core from the 8th generation and AMD Ryzen from the 2000 series (Zen+). Ryzen 2000 parts
    /// with G, U or H are mostly first-generation Zen and left undecided, as are Pentium, Celeron, Xeon and Athlon.
    /// </summary>
    public static bool? ProcessorFamily(string name)
    {
        if (IntelCoreUltra().IsMatch(name)) return true;
        if (IntelCore().Match(name) is { Success: true } intel)
        {
            var digits = intel.Groups[1].Value;
            var generation = digits.Length switch
            {
                3 => 1,
                4 when digits[0] == '1' => int.Parse(digits[..2]),
                4 => digits[0] - '0',
                _ => int.Parse(digits[..2]),
            };
            return generation >= 8;
        }
        if (AmdRyzenAi().IsMatch(name)) return true;
        if (AmdRyzen().Match(name) is { Success: true } ryzen)
        {
            var series = ryzen.Groups[1].Value[0] - '0';
            var suffix = ryzen.Groups[2].Value.ToUpperInvariant();
            return series switch
            {
                >= 3 => true,
                2 when suffix.Length == 0 || suffix.StartsWith('X') || suffix.StartsWith('E') => true,
                2 => null,
                _ => false,
            };
        }
        if (OldIntelOrAmd().IsMatch(name)) return false;
        return null;
    }

    [GeneratedRegex(@"Core\(TM\) Ultra|Core\(TM\) [357] \d{3}|Core Ultra", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCoreUltra();

    [GeneratedRegex(@"Core\(TM\) i[3579]-(\d{3,5})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCore();

    [GeneratedRegex(@"Ryzen AI", RegexOptions.IgnoreCase)]
    private static partial Regex AmdRyzenAi();

    [GeneratedRegex(@"Ryzen (?:Threadripper )?(?:\d )?(?:PRO )?(\d)\d{3}([A-Z]*)", RegexOptions.IgnoreCase)]
    private static partial Regex AmdRyzen();

    [GeneratedRegex(@"Core\(TM\)2|Core 2 |Pentium\(R\) (?:4|D|Dual)|Athlon(?:\(tm\))? (?:II|64)|Phenom|AMD (?:A\d+|E\d?|FX(?:\(tm\))?)-", RegexOptions.IgnoreCase)]
    private static partial Regex OldIntelOrAmd();

    /// <summary>TPM spec version through the TPM Base Services, which any user may ask: 2, 1, 0 for none, -1 unknown.</summary>
    private static int TpmVersion()
    {
        try
        {
            var info = new TPM_DEVICE_INFO { structVersion = 1 };
            var result = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TPM_DEVICE_INFO>(), ref info);
            if (result == 0) return info.tpmVersion == 2 ? 2 : 1;
            return result == TBS_E_TPM_NOT_FOUND ? 0 : -1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    private static bool? FirmwareIsUefi()
    {
        try
        {
            return GetFirmwareType(out var type) ? type == FirmwareTypeUefi : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The reasons Windows' compatibility appraiser recorded against the newest Windows 11 release it checked, such as
    /// "TPM UEFISecureBoot CpuFms", or empty when nothing blocks. Null when the appraiser has not run.
    /// </summary>
    private static IReadOnlyList<string>? AppraiserRedReasons()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\TargetVersionUpgradeExperienceIndicators");
            if (root is null) return null;
            // Release codes: CO (21H2), NI (22H2/23H2), GE (24H2 and later); the newest one is the one that matters.
            var target = root.GetSubKeyNames()
                .Where(n => n.Length >= 4 && (n.StartsWith("GE", StringComparison.OrdinalIgnoreCase) || n.StartsWith("NI", StringComparison.OrdinalIgnoreCase) || n.StartsWith("CO", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(n => n[..2].ToUpperInvariant() switch { "GE" => 3, "NI" => 2, _ => 1 })
                .ThenByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (target is null) return null;
            using var key = root.OpenSubKey(target);
            // A multi-string value ("TPM", "UEFISecureBoot", "CpuFms"), or "None".
            var reasons = key?.GetValue("RedReason") switch
            {
                string[] list => list,
                string text => text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries),
                _ => null,
            };
            return reasons?.Where(r => r.Length > 0 && !r.Equals("None", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private const uint TBS_E_TPM_NOT_FOUND = 0x8028400F;
    private const int FirmwareTypeUefi = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct TPM_DEVICE_INFO
    {
        public uint structVersion;
        public uint tpmVersion;
        public uint tpmInterfaceType;
        public uint tpmImpRevision;
    }

    [DllImport("tbs.dll", ExactSpelling = true)]
    private static extern uint Tbsi_GetDeviceInfo(uint size, ref TPM_DEVICE_INFO info);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);
}

/// <param name="TpmVersion">2, 1, 0 when there is no TPM, -1 when it could not be read.</param>
/// <param name="AppraiserRedReasons">What Windows' own compatibility check found missing; null when it has not run.</param>
public sealed record Windows11Inputs(
    int TpmVersion, bool? Uefi, bool? SecureBoot, string ProcessorName, IReadOnlyList<string>? AppraiserRedReasons,
    bool Is64BitWindows, ulong? InstalledMemoryBytes, long? SystemDriveBytes);
