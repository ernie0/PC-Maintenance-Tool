using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace PcMaintenance.Core.Uninstall;

public sealed record InstalledApp
{
    public required string DisplayName { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateOnly? InstallDate { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public string InstallLocation { get; init; } = "";
    public required string UninstallString { get; init; }
    public string QuietUninstallString { get; init; } = "";
    public bool IsWindowsInstaller { get; init; }
    public string Scope { get; init; } = "";
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string KeyPath { get; init; }

    public string RegistryDisplayPath => RegistryNames.Display(Hive, View, KeyPath);
}

public sealed record UninstallRoot(RegistryHive Hive, RegistryView View, string KeyPath, string Scope);

public sealed class InstalledAppProvider(IReadOnlyList<UninstallRoot>? roots = null)
{
    public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<UninstallRoot> DefaultRoots { get; } =
    [
        new(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallPath, "所有使用者"),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallPath, "所有使用者（32 位元）"),
        new(RegistryHive.CurrentUser, RegistryView.Default, UninstallPath, "目前使用者"),
    ];

    private static readonly HashSet<string> UpdateReleaseTypes =
        new(["Update", "Hotfix", "Security Update", "Service Pack", "Update Rollup"], StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<UninstallRoot> _roots = roots ?? DefaultRoots;

    public IReadOnlyList<InstalledApp> GetApps()
    {
        var apps = new List<InstalledApp>();
        foreach (var root in _roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(root.Hive, root.View);
                using var uninstall = baseKey.OpenSubKey(root.KeyPath);
                if (uninstall is null) continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    if (key is not null && Read(key, root, $@"{root.KeyPath}\{subKeyName}") is { } app) apps.Add(app);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Skips entries Windows' own "Installed apps" list hides: system components and updates.</summary>
    internal static InstalledApp? Read(RegistryKey key, UninstallRoot root, string keyPath)
    {
        var name = (key.GetValue("DisplayName") as string)?.Trim();
        var uninstallString = (key.GetValue("UninstallString") as string)?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uninstallString)) return null;
        if (key.GetValue("SystemComponent") is 1) return null;
        if (key.GetValue("ParentKeyName") is string { Length: > 0 }) return null;
        if (key.GetValue("ReleaseType") is string releaseType && UpdateReleaseTypes.Contains(releaseType)) return null;

        DateOnly? installDate = DateOnly.TryParseExact(key.GetValue("InstallDate") as string, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        long? size = key.GetValue("EstimatedSize") is int kb and > 0 ? kb * 1024L : null;

        return new InstalledApp
        {
            DisplayName = name,
            Publisher = (key.GetValue("Publisher") as string)?.Trim() ?? "",
            Version = (key.GetValue("DisplayVersion") as string)?.Trim() ?? "",
            InstallDate = installDate,
            EstimatedSizeBytes = size,
            InstallLocation = ((key.GetValue("InstallLocation") as string) ?? "").Trim().Trim('"'),
            UninstallString = uninstallString,
            QuietUninstallString = (key.GetValue("QuietUninstallString") as string)?.Trim() ?? "",
            IsWindowsInstaller = key.GetValue("WindowsInstaller") is 1,
            Scope = root.Scope,
            Hive = root.Hive,
            View = root.View,
            KeyPath = keyPath,
        };
    }
}

internal static class RegistryNames
{
    public static string ShortHive(RegistryHive hive) => hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

    public static string Display(RegistryHive hive, RegistryView view, string keyPath)
    {
        var hiveName = hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
        return $@"{hiveName}\{keyPath}{(view == RegistryView.Registry32 ? "（32 位元）" : "")}";
    }

    public static bool KeyExists(RegistryHive hive, RegistryView view, string keyPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
