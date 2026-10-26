using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace PcMaintenance.Core.Maintenance;

public sealed record AntivirusInfo(string Name, bool Enabled, bool UpToDate);

/// <summary>Antivirus products as Windows Security Center reports them; readable without elevation.</summary>
public static class SecurityStatus
{
    /// <summary>The registered products, or null when Security Center cannot be read (for example on Windows Server).</summary>
    public static IReadOnlyList<AntivirusInfo>? ReadAntivirus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            using var results = searcher.Get();
            var products = new List<AntivirusInfo>();
            foreach (var item in results.Cast<ManagementObject>())
            {
                using (item)
                    products.Add(Decode((item["displayName"] as string)?.Trim() ?? "", Convert.ToUInt32(item["productState"] ?? 0u)));
            }
            return products;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // productState is not documented; the common reading is that bits 12-15 are 1 while real-time protection is on
    // and bits 4-7 are 0 while the virus definitions are up to date.
    internal static AntivirusInfo Decode(string name, uint productState) =>
        new(name, ((productState >> 12) & 0xF) == 1, ((productState >> 4) & 0xF) == 0);
}

/// <summary>Whether Windows is waiting for a restart, and how long it has been running.</summary>
public static class RestartStatus
{
    private static readonly string[] PendingKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
    ];

    /// <summary>Set by Windows Update and component servicing until the restart they need has happened.</summary>
    public static bool IsUpdateRestartPending()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var path in PendingKeys)
            {
                using var key = machine.OpenSubKey(path);
                if (key is not null) return true;
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return false;
    }

    /// <summary>Time since Windows last started. With Fast Startup a shutdown does not restart Windows, so this can run to weeks.</summary>
    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);
}
