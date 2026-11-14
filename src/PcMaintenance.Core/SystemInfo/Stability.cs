using System.Diagnostics.Eventing.Reader;

namespace PcMaintenance.Core.SystemInfo;

/// <summary>One entry from the event log, reduced to what the summary needs.</summary>
public sealed record LoggedEvent(string Provider, int Id, DateTime TimeUtc, string FirstValue);

public sealed record StabilityEvent(DateTime TimeUtc, string Text);

public sealed record ProgramCrash(string Program, int Count, DateTime LastUtc);

/// <summary>What went wrong in the last <see cref="Days"/> days, from the System and Application logs.</summary>
public sealed record StabilityReport(
    bool Readable,
    IReadOnlyList<StabilityEvent> BlueScreens,
    IReadOnlyList<StabilityEvent> UnexpectedShutdowns,
    IReadOnlyList<StabilityEvent> DiskErrors,
    IReadOnlyList<StabilityEvent> HardwareErrors,
    IReadOnlyList<ProgramCrash> ProgramCrashes,
    int CorrectedHardwareErrors = 0)
{
    public const int Days = 30;

    public static readonly StabilityReport Unreadable = new(false, [], [], [], [], []);

    /// <summary>Blue screens, sudden shutdowns, disk errors and uncorrected hardware errors: signs of a problem with the PC itself.</summary>
    public bool HasSystemProblems => BlueScreens.Count + UnexpectedShutdowns.Count + DiskErrors.Count + HardwareErrors.Count > 0;
}

/// <summary>
/// Reads the System and Application logs, which every signed-in user may read. Windows logs a blue screen as
/// WER-SystemErrorReporting 1001 after the restart, any shutdown it did not see coming (power cut, hang, holding the
/// power button) as Kernel-Power 41, a crashed or hung program as Application Error 1000 / Application Hang 1002.
/// </summary>
public static class StabilityReader
{
    private const int MaxEvents = 3000;
    private static readonly long WindowMs = (long)TimeSpan.FromDays(StabilityReport.Days).TotalMilliseconds;

    private static string SystemQuery => $"*[System[TimeCreated[timediff(@SystemTime) <= {WindowMs}] and (" +
        "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)" +
        " or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41)" +
        " or (Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153))" +
        " or ((Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs']) and EventID=55)" +
        " or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=18 or EventID=19 or EventID=20 or EventID=46 or EventID=47))" +
        ")]]";

    private static string ApplicationQuery => $"*[System[TimeCreated[timediff(@SystemTime) <= {WindowMs}] and (" +
        "(Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002))]]";

    public static StabilityReport Read()
    {
        try
        {
            return Summarize(Query("System", SystemQuery).Concat(Query("Application", ApplicationQuery)));
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return StabilityReport.Unreadable;
        }
    }

    private static List<LoggedEvent> Query(string log, string xpath)
    {
        var events = new List<LoggedEvent>();
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
        for (var record = reader.ReadEvent(); record is not null && events.Count < MaxEvents; record = reader.ReadEvent())
        {
            using (record)
            {
                string first;
                try
                {
                    first = record.Properties.Count > 0 ? record.Properties[0].Value?.ToString() ?? "" : "";
                }
                catch (EventLogException)
                {
                    first = "";
                }
                events.Add(new LoggedEvent(record.ProviderName ?? "", record.Id, (record.TimeCreated ?? DateTime.Now).ToUniversalTime(), first.Trim()));
            }
        }
        return events;
    }

    public static StabilityReport Summarize(IEnumerable<LoggedEvent> events)
    {
        List<StabilityEvent> blue = [], shutdowns = [], disks = [], hardware = [];
        var crashes = new List<LoggedEvent>();
        var corrected = 0;
        foreach (var e in events)
        {
            switch (e.Provider, e.Id)
            {
                case ("Microsoft-Windows-WER-SystemErrorReporting", 1001):
                    // The first value reads like "0x00000133 (0x0000000000000001, …)": the stop code comes first.
                    var code = e.FirstValue.Split(' ', 2)[0];
                    blue.Add(new(e.TimeUtc, code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? $"藍色畫面（停止代碼 {code}）" : "藍色畫面"));
                    break;
                case ("Microsoft-Windows-Kernel-Power", 41):
                    shutdowns.Add(new(e.TimeUtc, "沒有正常關機就重新啟動（斷電、當機或按住電源鍵強制關機）"));
                    break;
                case ("disk", var id):
                    disks.Add(new(e.TimeUtc, (id switch
                    {
                        7 => "磁碟有損壞的磁區",
                        11 => "磁碟控制器錯誤",
                        51 => "寫入分頁檔時發生錯誤",
                        _ => "磁碟讀寫失敗、需要重試",
                    }) + (e.FirstValue.Length > 0 ? $"（{e.FirstValue}）" : "")));
                    break;
                case ("Ntfs" or "Microsoft-Windows-Ntfs", _):
                    disks.Add(new(e.TimeUtc, "檔案系統結構損毀" + (e.FirstValue.Length > 0 ? $"（{e.FirstValue}）" : "")));
                    break;
                // 18 and 20 are errors the hardware could not correct (they usually end in a blue screen); 17, 19, 46 and
                // 47 were corrected on the spot, which many PCs log now and then from PCIe power saving.
                case ("Microsoft-Windows-WHEA-Logger", 18 or 20):
                    hardware.Add(new(e.TimeUtc, "硬體發生無法更正的錯誤（處理器、記憶體或 PCIe 裝置）"));
                    break;
                case ("Microsoft-Windows-WHEA-Logger", _):
                    corrected++;
                    break;
                case ("Application Error" or "Application Hang", _):
                    if (e.FirstValue.Length > 0) crashes.Add(e);
                    break;
            }
        }
        var programs = crashes
            .GroupBy(c => c.FirstValue, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProgramCrash(g.First().FirstValue, g.Count(), g.Max(c => c.TimeUtc)))
            .OrderByDescending(p => p.Count).ThenByDescending(p => p.LastUtc)
            .ToList();
        static List<StabilityEvent> Newest(List<StabilityEvent> list) => list.OrderByDescending(e => e.TimeUtc).ToList();
        return new StabilityReport(true, Newest(blue), Newest(shutdowns), Newest(disks), Newest(hardware), programs, corrected);
    }
}
