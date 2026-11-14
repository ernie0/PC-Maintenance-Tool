using System.Text.Json;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.SystemInfo;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.Core.Inventory;

public sealed record AutoExportState(DateTimeOffset LastRun, string Folder);

/// <summary>
/// The logon export ("--auto-export"): the computer information, software list and browser extensions go to the company's
/// inventory folder, at most once per interval, so the multi-computer page always has fresh files.
/// </summary>
public static class AutoExport
{
    /// <summary>Due on a later calendar day than the interval allows, at every logon for 0 days, or when the folder changed.</summary>
    public static bool IsDue(AutoExportState? last, DateTimeOffset now, int intervalDays, string folder) =>
        last is null || intervalDays <= 0 ||
        !string.Equals(last.Folder, folder, StringComparison.OrdinalIgnoreCase) ||
        now.LocalDateTime.Date >= last.LastRun.LocalDateTime.Date.AddDays(intervalDays);

    /// <summary>At logon the network and mapped drives may come up a little after this runs.</summary>
    public static bool WaitForFolder(string folder, TimeSpan timeout, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        var step = TimeSpan.FromSeconds(10);
        for (var waited = TimeSpan.Zero; ; waited += step)
        {
            if (Directory.Exists(folder)) return true;
            if (waited >= timeout) return false;
            sleep(step);
        }
    }

    /// <summary>Writes the three files, named "電腦配備-名稱-日期.csv" and so on, replacing today's if they exist.</summary>
    public static IReadOnlyList<string> Write(string folder, bool isElevated, DateTime now)
    {
        var name = Environment.MachineName;
        var info = SystemInfoReader.Read();
        var health = ComputerHealth.Read(isElevated, info);
        var system = Path.Combine(folder, InventoryExport.DefaultFileName(InventoryExport.SystemKind, name, now));
        var software = Path.Combine(folder, InventoryExport.DefaultFileName(InventoryExport.SoftwareKind, name, now));
        var extensions = Path.Combine(folder, InventoryExport.DefaultFileName(InventoryExport.ExtensionsKind, name, now));
        InventoryExport.WriteSystem(system, info, health, now);
        InventoryExport.WriteSoftware(software, new InstalledAppProvider().GetApps(), name);
        InventoryExport.WriteExtensions(extensions, BrowserExtensions.Read(), name);
        return [system, software, extensions];
    }

    public static AutoExportState? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<AutoExportState>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string file, AutoExportState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(state));
    }
}
