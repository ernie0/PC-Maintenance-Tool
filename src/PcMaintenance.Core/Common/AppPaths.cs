namespace PcMaintenance.Core.Common;

public static class AppPaths
{
    public const string ProductFolder = "PcMaintenance";

    public static string UserDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

    public static string MachineDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductFolder);

    public static string JournalFile => Path.Combine(UserDataDir, "journal.json");
    public static string LogDir => Path.Combine(UserDataDir, "logs");
    public static string BackupDir => Path.Combine(UserDataDir, "backups");
    public static string AutoCleanStateFile => Path.Combine(UserDataDir, "auto-clean.json");
    public static string MaintenanceStateFile => Path.Combine(UserDataDir, "maintenance.json");
    public static string MachineSettingsFile => Path.Combine(MachineDataDir, "settings.json");
}
