using System.Management;
using System.Runtime.InteropServices;
using PcMaintenance.Core.Network;

namespace PcMaintenance.Core.SystemInfo;

/// <summary>A device Device Manager marks with a problem, why, and what usually fixes it.</summary>
public sealed record ProblemDevice(string Name, string Class, int Code, string Reason, string NextStep)
{
    /// <summary>Disabled on purpose by someone; listed, but not counted as a fault.</summary>
    public bool IsDisabled => Code == 22;
}

/// <summary>An update Windows Update failed to install recently and has not installed since.</summary>
public sealed record FailedUpdate(string Title, DateTime LastTriedUtc);

/// <param name="Readable">False when the Windows Update history could not be read at all.</param>
/// <param name="LastInstalledUtc">The newest successful install of a Windows update (see <see cref="UpdateHistory.IsWindowsUpdate"/>).</param>
public sealed record UpdateStatus(bool Readable, DateTime? LastInstalledUtc, IReadOnlyList<FailedUpdate> Failures)
{
    public static readonly UpdateStatus Unreadable = new(false, null, []);
}

/// <param name="DesignedMWh">Capacity when new; null when the battery does not report it or reading it was refused.</param>
public sealed record BatteryHealth(string Name, long? DesignedMWh, long? FullChargeMWh, int? CycleCount)
{
    /// <summary>Full charge as a share of the design capacity, 0–100.</summary>
    public int? HealthPercent => DesignedMWh is > 0 && FullChargeMWh is > 0
        ? (int)Math.Round(Math.Min(100.0, 100.0 * FullChargeMWh.Value / DesignedMWh.Value))
        : null;
}

public sealed record PrinterEntry(string Name, bool IsDefault, bool IsOffline, bool IsNetwork, int Jobs);

/// <summary>
/// Things worth a look that the hardware list does not show: updates, faulty devices, battery wear, printers, security
/// settings and what went wrong lately.
/// </summary>
public sealed record ComputerHealth(
    UpdateStatus Updates, IReadOnlyList<ProblemDevice> Devices, IReadOnlyList<BatteryHealth> Batteries, IReadOnlyList<PrinterEntry> Printers,
    IReadOnlyList<DiagnosisStep> Security, StabilityReport Stability)
{
    /// <summary>Reads everything; it takes a second or two, so call it on a background thread.</summary>
    /// <param name="isElevated">Disk encryption can only be read as an administrator.</param>
    public static ComputerHealth Read(bool isElevated)
    {
        var batteries = BatteryReader.Read();
        return new(UpdateHistory.Read(), DeviceProblems.Read(), batteries, PrinterList.Read(),
            SecurityChecks.Read(isElevated, isLaptop: batteries.Count > 0), StabilityReader.Read());
    }

    /// <summary>Security findings that call for a change.</summary>
    public IEnumerable<DiagnosisStep> SecurityConcerns => Security.Where(s => s.Result is DiagnosisResult.Warning or DiagnosisResult.Fail);
}

public static class DeviceProblems
{
    private const int NotConnected = 45;

    public static IReadOnlyList<ProblemDevice> Read()
    {
        var devices = new List<ProblemDevice>();
        Wmi.Query(@"root\cimv2", "SELECT Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0", item =>
        {
            var code = Convert.ToInt32(item["ConfigManagerErrorCode"] ?? 0);
            // Devices that are simply unplugged (a phone, a USB drive used last week) are not problems.
            if (code is 0 or NotConnected) return;
            var (reason, next) = Describe(code);
            devices.Add(new ProblemDevice(SystemInfoReader.Text(item["Name"]) is { Length: > 0 } name ? name : "未知的裝置",
                SystemInfoReader.Text(item["PNPClass"]), code, reason, next));
        });
        return devices.OrderBy(d => d.IsDisabled).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Device Manager's error codes, in the words of its own help, with the usual remedy.</summary>
    public static (string Reason, string NextStep) Describe(int code)
    {
        const string driver = "在裝置管理員對它按右鍵 →「更新驅動程式」，或到製造商網站下載驅動程式重新安裝";
        const string restart = "重新開機；仍然一樣時重新插拔裝置或更新驅動程式";
        return code switch
        {
            1 or 18 or 19 or 31 or 32 or 37 or 39 or 40 or 52 => (code switch
            {
                1 => "裝置設定不正確",
                18 => "需要重新安裝驅動程式",
                19 => "登錄中的設定資訊不完整或已損壞",
                31 => "Windows 無法載入驅動程式",
                32 => "驅動程式服務已停用",
                37 => "驅動程式初始化失敗",
                39 => "驅動程式可能已損壞或遺失",
                40 => "登錄中的驅動程式服務資訊遺失或錯誤",
                _ => "無法驗證驅動程式的數位簽章",
            }, driver),
            28 => ("沒有安裝驅動程式", driver),
            3 => ("驅動程式可能已損壞，或系統記憶體不足", restart),
            10 => ("裝置無法啟動", restart),
            12 => ("找不到足夠的可用資源", "停用沒用到的其他裝置，或請資訊人員檢查"),
            14 => ("需要重新開機才能正常運作", "重新開機"),
            21 or 47 => ("Windows 正在移除這個裝置", "重新開機"),
            22 => ("已停用", "需要使用時，在裝置管理員對它按右鍵 →「啟用裝置」"),
            24 => ("裝置不存在、無法正常運作，或沒有安裝全部的驅動程式", "確認裝置有接好；仍然一樣時更新驅動程式"),
            29 => ("裝置在 BIOS／UEFI 設定中被停用", "需要使用時，到 BIOS／UEFI 設定啟用"),
            41 => ("驅動程式已載入，但找不到硬體", "確認裝置有接好；仍然一樣時移除裝置後重新開機"),
            43 => ("裝置回報問題，Windows 已停止它", restart),
            48 => ("驅動程式有已知問題，Windows 已封鎖它", "到製造商網站下載新版驅動程式"),
            _ => ($"裝置有問題（代碼 {code}）", "在裝置管理員查看這個裝置的狀態說明"),
        };
    }
}

public sealed record UpdateEntry(DateTime DateUtc, string Title, int ResultCode, int Operation);

public static partial class UpdateHistory
{
    private const int Installation = 1;
    private const int Succeeded = 2, SucceededWithErrors = 3, Failed = 4, Aborted = 5;
    private const int HistoryToRead = 300;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromDays(30);

    // Microsoft Defender's signature and platform packages arrive several times a day and say nothing about patching.
    private static readonly string[] DefenderPackages = ["KB2267602", "KB4052623"];

    public static UpdateStatus Read()
    {
        try
        {
            return Summarize(ReadEntries(), DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or MemberAccessException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return UpdateStatus.Unreadable;
        }
    }

    /// <summary>
    /// Windows' own updates: every monthly, preview, .NET and tool update carries a KB number in its title, and a feature
    /// update is titled "Windows 11, version 26H2". Store app updates ("9NRZT3Q9R3DL-Microsoft…"), drivers and Defender
    /// signatures, which make up most of the history, are left out.
    /// </summary>
    public static bool IsWindowsUpdate(string title) =>
        (KbNumber().IsMatch(title) && !DefenderPackages.Any(kb => title.Contains(kb, StringComparison.OrdinalIgnoreCase)))
        || FeatureUpdate().IsMatch(title);

    [System.Text.RegularExpressions.GeneratedRegex(@"\bKB\d{6,7}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex KbNumber();

    [System.Text.RegularExpressions.GeneratedRegex(@"^Windows \d+, version \w+")]
    private static partial System.Text.RegularExpressions.Regex FeatureUpdate();

    /// <summary>Reads the newest entries through the Windows Update Agent, which any user may query without elevation.</summary>
    private static List<UpdateEntry> ReadEntries()
    {
        var entries = new List<UpdateEntry>();
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new COMException("沒有 Windows Update 元件");
        dynamic session = Activator.CreateInstance(type)!;
        dynamic searcher = session.CreateUpdateSearcher();
        int total = searcher.GetTotalHistoryCount();
        if (total <= 0) return entries;
        dynamic history = searcher.QueryHistory(0, Math.Min(total, HistoryToRead));
        foreach (dynamic entry in history)
        {
            string? title = entry.Title;
            DateTime date = entry.Date;
            entries.Add(new UpdateEntry(DateTime.SpecifyKind(date, DateTimeKind.Utc), title ?? "", (int)entry.ResultCode, (int)entry.Operation));
        }
        return entries;
    }

    public static UpdateStatus Summarize(IEnumerable<UpdateEntry> entries, DateTime nowUtc)
    {
        var installs = entries.Where(e => e.Operation == Installation && IsWindowsUpdate(e.Title)).ToList();
        var succeeded = installs.Where(e => e.ResultCode is Succeeded or SucceededWithErrors).ToList();
        DateTime? last = succeeded.Count > 0 ? succeeded.Max(e => e.DateUtc) : null;
        var failures = installs
            .Where(e => e.ResultCode is Failed or Aborted && nowUtc - e.DateUtc <= FailureWindow)
            .GroupBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FailedUpdate(g.Key, g.Max(e => e.DateUtc)))
            // Windows often fails an update once and installs it on the next try.
            .Where(f => !succeeded.Any(s => string.Equals(s.Title, f.Title, StringComparison.OrdinalIgnoreCase) && s.DateUtc > f.LastTriedUtc))
            .OrderByDescending(f => f.LastTriedUtc)
            .ToList();
        return new UpdateStatus(true, last, failures);
    }
}

public static class BatteryReader
{
    public static IReadOnlyList<BatteryHealth> Read()
    {
        var names = new List<string>();
        Wmi.Query(@"root\cimv2", "SELECT Name FROM Win32_Battery", item => names.Add(SystemInfoReader.Text(item["Name"]) is { Length: > 0 } n ? n : "電池"));
        if (names.Count == 0) return [];

        // root\wmi has one instance per battery, in the same order; some laptops refuse these to standard users.
        var designed = new List<long>();
        var full = new List<long>();
        var cycles = new List<int>();
        Wmi.Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData", item => designed.Add(Convert.ToInt64(item["DesignedCapacity"] ?? 0L)));
        Wmi.Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", item => full.Add(Convert.ToInt64(item["FullChargedCapacity"] ?? 0L)));
        Wmi.Query(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount", item => cycles.Add(Convert.ToInt32(item["CycleCount"] ?? 0)));
        return names.Select((name, i) => new BatteryHealth(name,
            i < designed.Count && designed[i] > 0 ? designed[i] : null,
            i < full.Count && full[i] > 0 ? full[i] : null,
            i < cycles.Count && cycles[i] > 0 ? cycles[i] : null)).ToList();
    }
}

public static class PrinterList
{
    public static IReadOnlyList<PrinterEntry> Read()
    {
        var jobs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // A print job's name is "<printer name>, <job id>".
        Wmi.Query(@"root\cimv2", "SELECT Name FROM Win32_PrintJob", item =>
        {
            var name = item["Name"] as string ?? "";
            var comma = name.LastIndexOf(',');
            if (comma > 0) jobs[name[..comma]] = jobs.GetValueOrDefault(name[..comma]) + 1;
        });
        var printers = new List<PrinterEntry>();
        Wmi.Query(@"root\cimv2", "SELECT Name, Default, WorkOffline, Network FROM Win32_Printer", item =>
        {
            var name = item["Name"] as string ?? "";
            if (name.Length == 0) return;
            printers.Add(new PrinterEntry(name, item["Default"] is true, item["WorkOffline"] is true, item["Network"] is true, jobs.GetValueOrDefault(name)));
        });
        return printers.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
    }
}

internal static class Wmi
{
    public static void Query(string scope, string wql, Action<ManagementObject> read)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            using var results = searcher.Get();
            foreach (var item in results.Cast<ManagementObject>())
            {
                using (item) read(item);
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
        }
    }
}
