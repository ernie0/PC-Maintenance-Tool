using System.Globalization;
using System.Text.RegularExpressions;

namespace PcMaintenance.Core.Inventory;

/// <summary>One row of a software list or browser extension list.</summary>
/// <param name="Key">What makes two rows the same program: the name without version numbers, or the extension's id.</param>
public sealed record FleetItem(string Kind, string Key, string Name, string Publisher, string Version);

public sealed record FleetInstall(string Computer, string Version, bool IsOutdated);

/// <summary>A program or extension and the PCs that have it.</summary>
public sealed record FleetSoftware(string Kind, string Name, string Publisher, string NewestVersion, IReadOnlyList<FleetInstall> Installs)
{
    public int Computers => Installs.Count;
    public int Outdated => Installs.Count(i => i.IsOutdated);

    /// <summary>"26.06（5 台）、26.05（2 台）", most common first.</summary>
    public string VersionsText => string.Join("、", Installs
        .GroupBy(i => i.Version)
        .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key, Comparer<string>.Create(FleetSoftwareList.CompareVersions))
        .Select(g => $"{(g.Key.Length > 0 ? g.Key : "未知版本")}（{g.Count()} 台）"));
}

public static partial class FleetSoftwareList
{
    public const string SoftwareKind = "軟體";

    public static FleetItem? ItemFrom(IReadOnlyDictionary<string, string> fields)
    {
        string Field(string name) => fields.GetValueOrDefault(name, "").Trim();
        var name = Field("名稱");
        if (name.Length == 0) return null;
        if (Field("瀏覽器") is { Length: > 0 } browser)
        {
            var kind = browser == "Firefox" ? "Firefox 附加元件" : $"{browser} 擴充功能";
            var id = Field("識別碼");
            return new FleetItem(kind, $"{browser}|{(id.Length > 0 ? id : name)}", name, Field("來源"), Field("版本"));
        }
        var normalized = Normalize(name);
        return new FleetItem(SoftwareKind, normalized.ToUpperInvariant(), normalized, Field("發行者"), Field("版本"));
    }

    /// <summary>Each program once, with the version each PC has; most widespread first.</summary>
    public static IReadOnlyList<FleetSoftware> Aggregate(IEnumerable<(string Computer, FleetItem Item)> installs) => installs
        .GroupBy(i => (i.Item.Kind, i.Item.Key))
        .Select(g =>
        {
            var perComputer = g
                .GroupBy(i => i.Computer, StringComparer.OrdinalIgnoreCase)
                .Select(c => (Computer: c.First().Computer, Version: c.Select(i => i.Item.Version).Max(Comparer<string>.Create(CompareVersions)) ?? ""))
                .ToList();
            var newest = perComputer.Select(c => c.Version).Max(Comparer<string>.Create(CompareVersions)) ?? "";
            var name = g.GroupBy(i => i.Item.Name).OrderByDescending(n => n.Count()).First().Key;
            var publisher = g.Select(i => i.Item.Publisher).Where(p => p.Length > 0).GroupBy(p => p).OrderByDescending(p => p.Count()).FirstOrDefault()?.Key ?? "";
            return new FleetSoftware(g.Key.Kind, name, publisher, newest, perComputer
                .Select(c => new FleetInstall(c.Computer, c.Version, IsOlder(c.Version, newest)))
                .OrderBy(c => c.IsOutdated ? 0 : 1).ThenBy(c => c.Computer, StringComparer.CurrentCulture)
                .ToList());
        })
        .OrderBy(s => s.Kind == SoftwareKind ? 0 : 1)
        .ThenByDescending(s => s.Computers)
        .ThenBy(s => s.Name, StringComparer.CurrentCulture)
        .ToList();

    /// <summary>
    /// The program's name without the version numbers many installers put in it ("7-Zip 26.05 (x64 edition)" and
    /// "7-Zip 26.06 (x64 edition)" are the same program). Year ranges and single numbers ("2015-2022", "365") stay.
    /// </summary>
    public static string Normalize(string name)
    {
        var text = TrailingVersion().Replace(name, "");
        text = DottedVersion().Replace(text, "");
        text = EmptyParentheses().Replace(text, "");
        return Spaces().Replace(text, " ").Trim(' ', '-');
    }

    /// <summary>Numeric versions compare part by part ("10.2" after "9.11"); anything else alphabetically.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var pa = Parts(a);
        var pb = Parts(b);
        if (pa is null || pb is null) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>Only numeric versions are called older; "未知" or "2024 R2" against "2025" is not judged.</summary>
    private static bool IsOlder(string version, string newest) =>
        Parts(version) is not null && Parts(newest) is not null && CompareVersions(version, newest) < 0;

    private static long[]? Parts(string version)
    {
        var m = LeadingNumbers().Match(version.Trim());
        if (!m.Success) return null;
        var parts = m.Value.Split('.');
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        }
        return numbers;
    }

    [GeneratedRegex(@"^\d+(?:\.\d+)*")]
    private static partial Regex LeadingNumbers();

    [GeneratedRegex(@"\s+-\s+[vV]?\d+(?:\.\d+)+\s*$")]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"(?<![\w.])[vV]?\d+(?:\.\d+)+(?![\w.])")]
    private static partial Regex DottedVersion();

    [GeneratedRegex(@"\(\s*\)")]
    private static partial Regex EmptyParentheses();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
