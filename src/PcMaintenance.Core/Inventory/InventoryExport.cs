using System.Globalization;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Startup;
using PcMaintenance.Core.SystemInfo;
using PcMaintenance.Core.Uninstall;

namespace PcMaintenance.Core.Inventory;

/// <summary>CSV lists for IT inventory. Every row carries the computer name so files from many PCs can be combined.</summary>
public static class InventoryExport
{
    public const string SoftwareKind = "軟體清單";
    public const string StartupKind = "開機項目";
    public const string SystemKind = "電腦配備";

    public static string DefaultFileName(string kind, string computerName, DateTime now) => $"{kind}-{computerName}-{now:yyyyMMdd}.csv";

    /// <summary>An existing folder, or a path ending in a separator, gets the default file name inside it.</summary>
    public static string ResolveTarget(string path, string kind, string computerName, DateTime now) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) || Directory.Exists(path)
            ? Path.Combine(path, DefaultFileName(kind, computerName, now))
            : path;

    public static int WriteSoftware(string path, IEnumerable<InstalledApp> apps, string computerName)
    {
        var rows = apps.Select(a => new[]
        {
            computerName,
            a.DisplayName,
            a.Publisher,
            a.Version,
            a.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            a.EstimatedSizeBytes is { } bytes ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) : "",
            a.Scope,
            a.InstallLocation,
        }).ToList();
        CsvFile.Write(path, ["電腦名稱", "名稱", "發行者", "版本", "安裝日期", "大小（MB）", "安裝範圍", "安裝位置"], rows);
        return rows.Count;
    }

    public static int WriteStartup(string path, IEnumerable<StartupItem> items, string computerName)
    {
        var rows = items.Select(i => new[]
        {
            computerName,
            i.Name,
            StartupText.SourceName(i.Source),
            StartupText.StateName(i),
            i.Publisher,
            i.Command,
            i.Location,
            i.IsMicrosoft ? "是" : "否",
        }).ToList();
        CsvFile.Write(path, ["電腦名稱", "名稱", "類型", "狀態", "發行者", "命令", "位置", "Microsoft 項目"], rows);
        return rows.Count;
    }

    /// <summary>One row per computer, so the files from every PC can be pasted under one header in Excel.</summary>
    public static void WriteSystem(string path, ComputerInfo info, DateTime exportedAt)
    {
        string Join(IEnumerable<string> values) => string.Join("；", values.Where(v => v.Length > 0));
        var os = info.Os;
        var machine = info.Machine;
        var cpu = info.Processor;
        string[] row =
        [
            machine.ComputerName,
            machine.DomainOrWorkgroup,
            machine.Manufacturer,
            machine.Model,
            machine.SerialNumber,
            machine.BaseBoard,
            Join([machine.BiosVersion, machine.BiosDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""]),
            os.Name,
            os.DisplayVersion,
            os.Build,
            os.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            cpu.Name,
            cpu.Cores is { } cores ? $"{cores}／{cpu.LogicalProcessors}" : $"{cpu.LogicalProcessors}",
            ByteSize.Format((long)info.Memory.InstalledPhysical),
            Join(info.MemoryModules.Modules.Select(m => $"{m.Slot} {ByteSize.Format((long)m.CapacityBytes)} {m.Type} {m.SpeedMHz} MHz".Trim())),
            Join(info.Gpus.Select(g => g.Name)),
            Join(info.Disks.Disks.Select(d => $"{d.Model} {ByteSize.Format(d.SizeBytes)} {d.MediaType}（{d.Health}）")),
            Join(info.Network.Select(n => n.Description)),
            Join(info.Network.Select(n => n.Mac)),
            Join(info.Network.SelectMany(n => n.IPv4)),
            exportedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ];
        CsvFile.Write(path,
        [
            "電腦名稱", "網域／工作群組", "製造商", "型號", "序號", "主機板", "BIOS", "Windows", "版本", "組建", "安裝日期",
            "處理器", "核心／邏輯處理器", "記憶體", "記憶體模組", "顯示卡", "磁碟", "網路卡", "MAC 位址", "IPv4 位址", "匯出時間",
        ], [row]);
    }
}
