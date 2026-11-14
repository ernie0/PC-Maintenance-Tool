using System.Globalization;
using System.Net;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Network;
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
    public const string ConnectionsKind = "網路連線";
    public const string DevicesKind = "區域網路裝置";
    public const string ExtensionsKind = "瀏覽器擴充功能";

    public static string DefaultFileName(string kind, string computerName, DateTime now, string extension = ".csv") =>
        $"{kind}-{computerName}-{now:yyyyMMdd}{extension}";

    /// <summary>An existing folder, or a path ending in a separator, gets the default file name inside it.</summary>
    public static string ResolveTarget(string path, string kind, string computerName, DateTime now, string extension = ".csv") =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) || Directory.Exists(path)
            ? Path.Combine(path, DefaultFileName(kind, computerName, now, extension))
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

    public static int WriteExtensions(string path, IEnumerable<BrowserExtension> extensions, string computerName)
    {
        var rows = extensions.Select(e => new[] { computerName, e.Browser, e.Profile, e.Name, e.Version, e.StateText, e.Source, e.Id }).ToList();
        CsvFile.Write(path, ["電腦名稱", "瀏覽器", "設定檔", "名稱", "版本", "狀態", "來源", "識別碼"], rows);
        return rows.Count;
    }

    /// <param name="attention">What the page marked for a second look, when it has checked the signatures.</param>
    public static int WriteStartup(string path, IEnumerable<StartupItem> items, string computerName, Func<StartupItem, string?>? attention = null)
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
            attention?.Invoke(i) ?? "",
        }).ToList();
        CsvFile.Write(path, ["電腦名稱", "名稱", "類型", "狀態", "發行者", "命令", "位置", "Microsoft 項目", "需要注意"], rows);
        return rows.Count;
    }

    /// <summary>Every connection, loopback ones included; host names are filled in only when the user looked them up.</summary>
    public static int WriteConnections(string path, IEnumerable<ConnectionInfo> connections, string computerName, Func<IPAddress, string?>? hostName = null)
    {
        var rows = connections.Select(c => new[]
        {
            computerName,
            c.ProgramName,
            c.Connection.ProcessId.ToString(CultureInfo.InvariantCulture),
            c.ProgramPath ?? "",
            ConnectionText.Protocol(c.Connection.Protocol),
            ConnectionText.Endpoint(c.Connection.Local),
            c.Connection.Remote is { } remote ? ConnectionText.Endpoint(remote) : "",
            c.RemoteAddress is { } address ? hostName?.Invoke(address) ?? "" : "",
            ConnectionText.State(c.Connection),
            c.Service?.Text ?? "",
            ConnectionText.Scope(c),
            c.IsSuspicious ? "是" : "否",
        }).ToList();
        CsvFile.Write(path, ["電腦名稱", "程式", "處理程序編號", "程式路徑", "協定", "本機位址", "遠端位址", "主機名稱", "狀態", "服務", "範圍", "可疑 IP"], rows);
        return rows.Count;
    }

    public static int WriteDevices(string path, IEnumerable<NeighborDevice> devices, string computerName, Func<IPAddress, string?>? hostName = null)
    {
        var rows = devices.Select(d => new[]
        {
            computerName,
            d.Address.ToString(),
            hostName?.Invoke(d.Address) ?? "",
            d.Mac,
            d.AdapterName,
            d.IsStatic ? "手動設定" : "自動學習",
            d.IsGateway ? "是" : "否",
        }).ToList();
        CsvFile.Write(path, ["電腦名稱", "IP 位址", "主機名稱", "實體位址", "網路卡", "類型", "預設閘道"], rows);
        return rows.Count;
    }

    /// <summary>One row per computer, so the files from every PC can be pasted under one header in Excel.</summary>
    public static void WriteSystem(string path, ComputerInfo info, ComputerHealth health, DateTime exportedAt)
    {
        string Join(IEnumerable<string> values) => string.Join("；", values.Where(v => v.Length > 0));
        var os = info.Os;
        var updates = health.Updates;
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
            Join(info.Disks.Volumes.Where(v => !v.IsRemovable).Select(v => $"{v.Letter}: 剩 {ByteSize.Format(v.FreeBytes)}／{ByteSize.Format(v.TotalBytes)}（{v.FreePercent:0}%）")),
            Join(info.Network.Select(n => n.Description)),
            Join(info.Network.Select(n => n.Mac)),
            Join(info.Network.SelectMany(n => n.IPv4)),
            !updates.Readable ? "無法讀取" : updates.LastInstalledUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "沒有紀錄",
            Join(updates.Failures.Select(f => f.Title)),
            Join(health.Devices.Where(d => !d.IsDisabled).Select(d => $"{d.Name}（{d.Reason}）")),
            Join(health.Batteries.Select(b => b.HealthPercent is { } percent ? $"{percent}%" : "無法讀取")),
            Join(health.Printers.Select(p => p.IsDefault ? $"{p.Name}（預設）" : p.Name)),
            Join(health.SecurityConcerns.Select(c => c.Title)),
            !health.Stability.Readable ? "無法讀取" : Join(new[]
            {
                health.Stability.BlueScreens.Count > 0 ? $"藍色畫面 {health.Stability.BlueScreens.Count}" : "",
                health.Stability.UnexpectedShutdowns.Count > 0 ? $"不正常關機 {health.Stability.UnexpectedShutdowns.Count}" : "",
                health.Stability.DiskErrors.Count > 0 ? $"磁碟錯誤 {health.Stability.DiskErrors.Count}" : "",
                health.Stability.HardwareErrors.Count > 0 ? $"硬體錯誤 {health.Stability.HardwareErrors.Count}" : "",
                health.Stability.ProgramCrashes.Count > 0 ? $"程式當機 {health.Stability.ProgramCrashes.Sum(p => p.Count)}" : "",
            }),
            os.LastBoot.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            health.Windows11?.Summary ?? "",
            health.Backup.Summary,
            exportedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ];
        CsvFile.Write(path,
        [
            "電腦名稱", "網域／工作群組", "製造商", "型號", "序號", "主機板", "BIOS", "Windows", "版本", "組建", "安裝日期",
            "處理器", "核心／邏輯處理器", "記憶體", "記憶體模組", "顯示卡", "磁碟", "磁碟空間", "網路卡", "MAC 位址", "IPv4 位址",
            "上次 Windows 更新", "更新安裝失敗", "有問題的裝置", "電池健康度", "印表機", "安全性建議", "最近 30 天的問題",
            "上次開機", "Windows 11 升級", "備份", "匯出時間",
        ], [row]);
    }
}
