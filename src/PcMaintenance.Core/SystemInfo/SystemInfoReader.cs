using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using PcMaintenance.Core.DiskTools;
using PcMaintenance.Core.Memory;
using PcMaintenance.Core.Performance;

namespace PcMaintenance.Core.SystemInfo;

public sealed record OsInfo(string Name, string DisplayVersion, string Build, string Architecture, DateTime? InstallDate, DateTime LastBoot);

public sealed record MachineInfo(
    string ComputerName, string DomainOrWorkgroup, bool IsDomainJoined, string Manufacturer, string Model, string SerialNumber,
    string BaseBoard, string BiosVersion, DateTime? BiosDate, bool? SecureBoot);

public sealed record ProcessorInfo(string Name, int? Sockets, int? Cores, int LogicalProcessors, int? BaseMHz, int? L2KB, int? L3KB, bool? Virtualization);

public sealed record GpuInfo(string Name, string Luid, ulong DedicatedBytes, ulong SharedBytes, string DriverVersion, DateTime? DriverDate);

public sealed record NetworkAdapterInfo(
    string Id, string Name, string Description, string Kind, long SpeedBitsPerSecond, string Mac, IReadOnlyList<string> IPv4, IReadOnlyList<string> IPv6);

public sealed record ComputerInfo(
    OsInfo Os, MachineInfo Machine, ProcessorInfo Processor, MemoryStatus Memory, MemoryHardware MemoryModules,
    IReadOnlyList<GpuInfo> Gpus, DiskInventoryResult Disks, IReadOnlyList<NetworkAdapterInfo> Network);

/// <summary>What the computer is and runs: Windows version, model and BIOS, processor, memory, graphics, disks, network.</summary>
public static class SystemInfoReader
{
    public static ComputerInfo Read() => new(
        ReadOs(), ReadMachine(), ReadProcessor(), MemoryInfo.Read(), MemoryHardwareReader.Read(),
        ReadGpus(), DiskInventory.Read(includeReliability: false), ReadNetwork());

    public static OsInfo ReadOs()
    {
        string name = "Windows", architecture = Environment.Is64BitOperatingSystem ? "64 位元" : "32 位元";
        DateTime? installed = null;
        Query("SELECT Caption, InstallDate FROM Win32_OperatingSystem", item =>
        {
            // The registry ProductName still says "Windows 10" on Windows 11; WMI's caption is right.
            if (item["Caption"] is string caption) name = caption.Replace("Microsoft ", "", StringComparison.Ordinal).Trim();
            installed = CimDate(item["InstallDate"]);
        });
        var displayVersion = "";
        var build = Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            displayVersion = key?.GetValue("DisplayVersion") as string ?? "";
            if (key?.GetValue("CurrentBuildNumber") is string number) build = number;
            if (key?.GetValue("UBR") is int revision) build += $".{revision}";
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        var lastBoot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new OsInfo(name, displayVersion, build, architecture, installed, lastBoot);
    }

    public static MachineInfo ReadMachine()
    {
        string domain = "", manufacturer = "", model = "", serial = "", board = "", bios = "";
        var joined = false;
        DateTime? biosDate = null;
        Query("SELECT Domain, PartOfDomain, Manufacturer, Model FROM Win32_ComputerSystem", item =>
        {
            domain = Text(item["Domain"]);
            joined = item["PartOfDomain"] is true;
            manufacturer = Text(item["Manufacturer"]);
            model = Text(item["Model"]);
        });
        Query("SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", item =>
        {
            serial = Text(item["SerialNumber"]);
            bios = Text(item["SMBIOSBIOSVersion"]);
            biosDate = CimDate(item["ReleaseDate"]);
        });
        Query("SELECT Manufacturer, Product FROM Win32_BaseBoard", item => board = $"{Text(item["Manufacturer"])} {Text(item["Product"])}".Trim());

        bool? secureBoot = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int enabled) secureBoot = enabled == 1;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return new MachineInfo(Environment.MachineName, domain, joined, manufacturer, model, serial, board, bios, biosDate, secureBoot);
    }

    public static ProcessorInfo ReadProcessor()
    {
        string name = "";
        int? mhz = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
            mhz = key?.GetValue("~MHz") is int value and > 0 ? value : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        int sockets = 0, cores = 0, l2 = 0, l3 = 0;
        bool? virtualization = null;
        Query("SELECT NumberOfCores, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled FROM Win32_Processor", item =>
        {
            sockets++;
            cores += Convert.ToInt32(item["NumberOfCores"] ?? 0);
            l2 += Convert.ToInt32(item["L2CacheSize"] ?? 0);
            l3 += Convert.ToInt32(item["L3CacheSize"] ?? 0);
            if (item["VirtualizationFirmwareEnabled"] is bool enabled) virtualization = (virtualization ?? false) || enabled;
        });
        // With Hyper-V running the firmware flag reads false although virtualization is on and in use.
        Query("SELECT HypervisorPresent FROM Win32_ComputerSystem", item =>
        {
            if (item["HypervisorPresent"] is true) virtualization = true;
        });
        return new ProcessorInfo(name, sockets > 0 ? sockets : null, cores > 0 ? cores : null, Environment.ProcessorCount, mhz,
            l2 > 0 ? l2 : null, l3 > 0 ? l3 : null, virtualization);
    }

    public static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var drivers = new Dictionary<string, (string Version, DateTime? Date)>(StringComparer.OrdinalIgnoreCase);
        Query("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController", item =>
            drivers[Text(item["Name"])] = (Text(item["DriverVersion"]), CimDate(item["DriverDate"])));
        IReadOnlyList<Dxgi.Adapter> adapters;
        try
        {
            adapters = Dxgi.Adapters();
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            adapters = [];
        }
        return adapters.Select(a =>
        {
            var (version, date) = drivers.GetValueOrDefault(a.Name, ("", null));
            return new GpuInfo(a.Name, a.Luid, a.DedicatedBytes, a.SharedBytes, version, date);
        }).ToList();
    }

    public static IReadOnlyList<NetworkAdapterInfo> ReadNetwork()
    {
        var adapters = new List<NetworkAdapterInfo>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!NetworkActivitySampler.IsShown(adapter.NetworkInterfaceType, adapter.Description, adapter.OperationalStatus)) continue;
            List<string> v4 = [], v6 = [];
            try
            {
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork) v4.Add(address.Address.ToString());
                    else if (address.Address.AddressFamily == AddressFamily.InterNetworkV6 && !address.Address.IsIPv6LinkLocal) v6.Add(address.Address.ToString());
                }
            }
            catch (NetworkInformationException)
            {
            }
            var mac = string.Join("-", adapter.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            var kind = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "乙太網路";
            adapters.Add(new NetworkAdapterInfo(adapter.Id, adapter.Name, adapter.Description, kind, adapter.Speed, mac, v4, v6));
        }
        return adapters;
    }

    private static void Query(string wql, Action<ManagementObject> read)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
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

    // Placeholders board makers leave in the firmware instead of a real model or serial number.
    private static readonly HashSet<string> Placeholders = new(
        ["Default string", "To be filled by O.E.M.", "To Be Filled By O.E.M.", "System Serial Number", "System Product Name",
         "System manufacturer", "Not Specified", "Not Applicable", "None", "0", "N/A"], StringComparer.OrdinalIgnoreCase);

    internal static string Text(object? value) => (value as string)?.Trim() is { } text && !Placeholders.Contains(text) ? text : "";

    /// <summary>WMI dates look like "20250612000000.000000-000".</summary>
    internal static DateTime? CimDate(object? value) =>
        value is string { Length: >= 8 } text && DateTime.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
