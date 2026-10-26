using System.Text.Json;

namespace PcMaintenance.Core.Maintenance;

/// <summary>When one-click maintenance last ran on this account, and what it did, for the page's summary line.</summary>
public sealed record MaintenanceRun(DateTimeOffset Time, int Fixed, long FreedBytes);

public static class MaintenanceHistory
{
    public static MaintenanceRun? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<MaintenanceRun>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string file, MaintenanceRun run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(run));
        File.Move(temp, file, overwrite: true);
    }
}
