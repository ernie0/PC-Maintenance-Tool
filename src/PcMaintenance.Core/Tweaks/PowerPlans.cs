using System.Text.RegularExpressions;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Tweaks;

public sealed record PowerPlan(Guid Id, string Name, bool IsActive)
{
    // A drop-down item is read out (and found by UI Automation) by this text, not by its displayed member.
    public override string ToString() => Name;
}

/// <summary>
/// Power plans through Windows' own powercfg. "Ultimate Performance" is hidden by default; Windows 10 1803 and later
/// can add it from its built-in template. Laptops with Modern Standby may only offer the Balanced plan.
/// </summary>
public sealed partial class PowerPlans(ChangeJournal journal, FileLog? log = null)
{
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\((.+?)\)(\s*\*)?")]
    private static partial Regex PlanLine();

    public static async Task<IReadOnlyList<PowerPlan>> ListAsync()
    {
        var (_, output) = await ConsoleTool.RunAsync("powercfg.exe", "/list");
        return Parse(output);
    }

    /// <summary>Lines look like "電源配置 GUID: 381b4222-…  (平衡) *" in every language; the star marks the active plan.</summary>
    internal static IReadOnlyList<PowerPlan> Parse(string output) =>
        output.Split('\n')
            .Select(line => PlanLine().Match(line))
            .Where(m => m.Success)
            .Select(m => new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success))
            .ToList();

    public async Task<bool> SetActiveAsync(PowerPlan plan, PowerPlan? previous)
    {
        var (exitCode, _) = await ConsoleTool.RunAsync("powercfg.exe", $"/setactive {plan.Id}");
        if (exitCode != 0) return false;
        journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.SettingChanged,
            Target = "電源計畫",
            Description = $"電源計畫改為「{plan.Name}」",
            RestoreData = new Dictionary<string, string> { ["powerPlan"] = previous?.Id.ToString() ?? "", ["powerPlanName"] = previous?.Name ?? "" },
        });
        log?.Info($"電源計畫改為「{plan.Name}」（原本「{previous?.Name}」）");
        return true;
    }

    /// <summary>Adds the Ultimate Performance plan from its template; returns false when this Windows cannot.</summary>
    public static async Task<bool> AddUltimateAsync()
    {
        var (exitCode, output) = await ConsoleTool.RunAsync("powercfg.exe", $"-duplicatescheme {UltimateTemplate}");
        return exitCode == 0 && PlanLine().IsMatch(output);
    }

    public static bool CanRestore(ChangeRecord record) =>
        record.Kind == ChangeKind.SettingChanged && record.RestoreData.TryGetValue("powerPlan", out var id) && Guid.TryParse(id, out _);

    public async Task RestoreAsync(ChangeRecord record)
    {
        var (exitCode, output) = await ConsoleTool.RunAsync("powercfg.exe", $"/setactive {record.RestoreData["powerPlan"]}");
        if (exitCode != 0) throw new InvalidOperationException($"無法切回原本的電源計畫：{DiskTools.DiskMaintenance.Tail(output, 2)}");
        journal.MarkRestored(record.Id);
        log?.Info($"已還原電源計畫：{record.RestoreData["powerPlanName"]}");
    }
}
