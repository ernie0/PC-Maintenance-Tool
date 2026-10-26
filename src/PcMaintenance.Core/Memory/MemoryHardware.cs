using System.Management;
using System.Runtime.InteropServices;

namespace PcMaintenance.Core.Memory;

public sealed record MemoryModule(string Slot, ulong CapacityBytes, uint SpeedMHz, string Type, string Manufacturer, string PartNumber, string FormFactor = "");

/// <summary>Installed modules and the board's slot count, for deciding whether memory can be added.</summary>
public sealed record MemoryHardware(IReadOnlyList<MemoryModule> Modules, int? SlotCount, ulong? MaxCapacityBytes, string? Error);

public static class MemoryHardwareReader
{
    public static MemoryHardware Read()
    {
        try
        {
            var modules = new List<MemoryModule>();
            using (var searcher = new ManagementObjectSearcher(
                       "SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory"))
            using (var results = searcher.Get())
            {
                foreach (var item in results.Cast<ManagementObject>())
                {
                    using (item)
                    {
                        modules.Add(new MemoryModule(
                            Text(item["DeviceLocator"]) is { Length: > 0 } slot ? slot : Text(item["BankLabel"]),
                            Convert.ToUInt64(item["Capacity"] ?? 0UL),
                            Convert.ToUInt32(item["ConfiguredClockSpeed"] ?? item["Speed"] ?? 0U),
                            TypeName(Convert.ToUInt32(item["SMBIOSMemoryType"] ?? 0U)),
                            Text(item["Manufacturer"]),
                            Text(item["PartNumber"]),
                            Convert.ToUInt16(item["FormFactor"] ?? (ushort)0) switch { 8 => "DIMM", 12 => "SODIMM", _ => "" }));
                    }
                }
            }

            int? slots = null;
            ulong? max = null;
            using (var searcher = new ManagementObjectSearcher("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray"))
            using (var results = searcher.Get())
            {
                foreach (var item in results.Cast<ManagementObject>())
                {
                    using (item)
                    {
                        slots = (slots ?? 0) + Convert.ToInt32(item["MemoryDevices"] ?? 0);
                        if (item["MaxCapacityEx"] is { } kilobytes) max = (max ?? 0) + Convert.ToUInt64(kilobytes) * 1024;
                    }
                }
            }
            return new MemoryHardware(modules, slots is > 0 ? slots : null, max is > 0 ? max : null, null);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return new MemoryHardware([], null, null, "無法讀取記憶體模組資訊");
        }
    }

    private static string Text(object? value) => (value as string)?.Trim() ?? "";

    // SMBIOS memory type codes.
    private static string TypeName(uint smbiosType) => smbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };
}
