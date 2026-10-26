using System.Management;
using System.Runtime.InteropServices;

namespace PcMaintenance.Core.DiskTools;

public sealed record VolumeInfo(string Root, string Label, string FileSystem, bool IsRemovable, bool IsSystem, long TotalBytes, long FreeBytes, int? DiskNumber)
{
    public string Letter => Root[..1];
    public long UsedBytes => TotalBytes - FreeBytes;
    public double FreePercent => TotalBytes == 0 ? 0 : 100.0 * FreeBytes / TotalBytes;
}

/// <summary>Counters the drive itself reports; reading them needs elevation, and zero means "not reported".</summary>
public sealed record DiskReliability(int? TemperatureC, long? PowerOnHours, int? WearPercent);

public sealed record PhysicalDiskInfo(int Number, string Model, string MediaType, string BusType, long SizeBytes, string Health, bool IsHealthy, DiskReliability? Reliability);

public sealed record DiskInventoryResult(IReadOnlyList<VolumeInfo> Volumes, IReadOnlyList<PhysicalDiskInfo> Disks, string? Error);

public static class DiskInventory
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public static DiskInventoryResult Read(bool includeReliability)
    {
        string? error = null;
        var diskOfLetter = new Dictionary<char, int>();
        var disks = new List<PhysicalDiskInfo>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(StorageNamespace, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
            using (var results = searcher.Get())
            {
                foreach (var item in results.Cast<ManagementObject>())
                {
                    using (item)
                    {
                        var letter = Convert.ToChar(item["DriveLetter"] ?? '\0');
                        if (char.IsLetter(letter)) diskOfLetter[char.ToUpperInvariant(letter)] = Convert.ToInt32(item["DiskNumber"]);
                    }
                }
            }
            using (var searcher = new ManagementObjectSearcher(StorageNamespace,
                       "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"))
            using (var results = searcher.Get())
            {
                foreach (var item in results.Cast<ManagementObject>())
                {
                    using (item)
                    {
                        if (!int.TryParse(item["DeviceId"] as string, out var number)) continue;
                        var health = Convert.ToUInt16(item["HealthStatus"] ?? (ushort)5);
                        disks.Add(new PhysicalDiskInfo(
                            number,
                            (item["FriendlyName"] as string)?.Trim() ?? "",
                            MediaTypeName(Convert.ToUInt16(item["MediaType"] ?? (ushort)0)),
                            BusTypeName(Convert.ToUInt16(item["BusType"] ?? (ushort)0)),
                            Convert.ToInt64(item["Size"] ?? 0L),
                            HealthName(health),
                            health == 0,
                            includeReliability ? Reliability(item) : null));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            error = "無法讀取實體磁碟資訊，只顯示磁碟機空間";
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var volumes = DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .Select(d => new VolumeInfo(
                d.Name,
                SafeLabel(d),
                d.DriveFormat,
                d.DriveType == DriveType.Removable,
                d.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase),
                d.TotalSize,
                d.AvailableFreeSpace,
                diskOfLetter.TryGetValue(char.ToUpperInvariant(d.Name[0]), out var disk) ? disk : null))
            .ToList();
        return new DiskInventoryResult(volumes, disks.OrderBy(d => d.Number).ToList(), error);
    }

    private static DiskReliability? Reliability(ManagementObject disk)
    {
        try
        {
            using var related = disk.GetRelated("MSFT_StorageReliabilityCounter", "MSFT_PhysicalDiskToStorageReliabilityCounter",
                null, null, null, null, false, null);
            foreach (var counter in related.Cast<ManagementObject>())
            {
                using (counter)
                {
                    int? temperature = Convert.ToInt32(counter["Temperature"] ?? 0) is > 0 and var t ? t : null;
                    long? hours = Convert.ToInt64(counter["PowerOnHours"] ?? 0L) is > 0 and var h ? h : null;
                    int? wear = counter["Wear"] is { } w ? Convert.ToInt32(w) : null;
                    return new DiskReliability(temperature, hours, wear);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    internal static string HealthName(ushort status) => status switch
    {
        0 => "正常",
        1 => "警告",
        2 => "不良",
        _ => "未知",
    };

    internal static string MediaTypeName(ushort type) => type switch
    {
        3 => "傳統硬碟",
        4 => "SSD",
        5 => "SCM",
        _ => "未知",
    };

    internal static string BusTypeName(ushort type) => type switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD 卡",
        17 => "NVMe",
        _ => "其他",
    };
}
