using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace PcMaintenance.Core.Startup;

/// <param name="ToDesktop">From power-on to the desktop (MainPathBootTime).</param>
/// <param name="ToReady">From power-on until Windows had settled after sign-in (BootTime = main path + post-boot).</param>
public sealed record BootRecord(DateTime TimeUtc, TimeSpan ToDesktop, TimeSpan ToReady);

/// <summary>A program, driver or service Windows found had slowed a start, and by how much.</summary>
public sealed record SlowStarter(string Name, string Kind, TimeSpan Delay, DateTime TimeUtc);

public sealed record BootReport(bool Readable, IReadOnlyList<BootRecord> Boots, IReadOnlyList<SlowStarter> SlowStarters)
{
    public static readonly BootReport Unreadable = new(false, [], []);

    public BootRecord? Last => Boots.Count > 0 ? Boots[0] : null;

    /// <summary>Average time to the desktop over up to the last ten starts.</summary>
    public TimeSpan? AverageToDesktop => Boots.Count > 0 ? TimeSpan.FromMilliseconds(Boots.Take(10).Average(b => b.ToDesktop.TotalMilliseconds)) : null;
}

/// <summary>
/// Windows' own boot measurements from the Diagnostics-Performance log: event 100 for each full start, 101, 102 and 103
/// for the program, driver or service that made one slower. Only administrators may read that log.
/// </summary>
public static class BootPerformance
{
    private const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";
    private const int MaxEvents = 300;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static BootReport Read()
    {
        try
        {
            var xml = new List<string>();
            var query = new EventLogQuery(Log, PathType.LogName, "*[System[EventID=100 or EventID=101 or EventID=102 or EventID=103]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null && xml.Count < MaxEvents; record = reader.ReadEvent())
            {
                using (record) xml.Add(record.ToXml());
            }
            return Parse(xml);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return BootReport.Unreadable;
        }
    }

    /// <summary>Events newest first, as XML; values the event does not carry are skipped rather than guessed.</summary>
    public static BootReport Parse(IEnumerable<string> events)
    {
        var boots = new List<BootRecord>();
        var slow = new List<SlowStarter>();
        foreach (var text in events)
        {
            XElement root;
            try
            {
                root = XElement.Parse(text);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }
            var system = root.Element(Ns + "System");
            if (!int.TryParse(system?.Element(Ns + "EventID")?.Value, out var id)) continue;
            var time = DateTime.TryParse(system?.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created) ? created : DateTime.MinValue;
            var data = root.Element(Ns + "EventData")?.Elements(Ns + "Data")
                .Where(d => d.Attribute("Name") is not null)
                .GroupBy(d => d.Attribute("Name")!.Value)
                .ToDictionary(g => g.Key, g => g.First().Value) ?? [];
            if (id == 100)
            {
                if (Milliseconds(data, "MainPathBootTime") is { } desktop && Milliseconds(data, "BootTime") is { } ready && desktop > TimeSpan.Zero)
                    boots.Add(new BootRecord(time, desktop, ready));
            }
            else if (Milliseconds(data, "DegradationTime") is { } delay && delay > TimeSpan.Zero)
            {
                var name = data.GetValueOrDefault("FriendlyName") is { Length: > 0 } friendly ? friendly : data.GetValueOrDefault("Name") ?? "";
                if (name.Length == 0) continue;
                slow.Add(new SlowStarter(name, id switch { 101 => "程式", 102 => "驅動程式", _ => "服務" }, delay, time));
            }
        }
        // The same program slows many starts; keep its latest occurrence and rank by how much it cost.
        var latest = slow.GroupBy(s => (s.Name, s.Kind)).Select(g => g.OrderByDescending(s => s.TimeUtc).First())
            .OrderByDescending(s => s.Delay).ToList();
        return new BootReport(true, boots.OrderByDescending(b => b.TimeUtc).ToList(), latest);
    }

    private static TimeSpan? Milliseconds(Dictionary<string, string> data, string name) =>
        data.TryGetValue(name, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms >= 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;
}
