using PcMaintenance.Core.Journal;

namespace PcMaintenance.Core.Startup;

public enum StartupSource
{
    RegistryRun,
    StartupFolder,
    ScheduledTask,
    Service,
}

public enum ServiceStartType
{
    Boot,
    System,
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
}

public sealed record StartupItem
{
    public required string Key { get; init; }
    public required StartupSource Source { get; init; }
    public required string Name { get; init; }
    public string Command { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string Publisher { get; init; } = "";
    public string Location { get; init; } = "";
    public bool Enabled { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool IsMicrosoft { get; init; }
    public bool CanChange { get; init; }
    public ServiceStartType? StartType { get; init; }
    public string? ServiceStatus { get; init; }
    public string Note { get; init; } = "";
    /// <summary>Provider-specific identifiers (registry value, task path, service name).</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

public interface IStartupProvider
{
    StartupSource Source { get; }
    ChangeKind Kind { get; }
    IReadOnlyList<StartupItem> Enumerate();
    /// <summary>Applies the change and returns what <see cref="Restore"/> needs to undo it.</summary>
    Dictionary<string, string> SetEnabled(StartupItem item, bool enabled);
    void Restore(IReadOnlyDictionary<string, string> data);
}

public static class StartupText
{
    public static string SourceName(StartupSource source) => source switch
    {
        StartupSource.RegistryRun => "登錄檔啟動項",
        StartupSource.StartupFolder => "啟動資料夾",
        StartupSource.ScheduledTask => "排程工作",
        StartupSource.Service => "服務",
        _ => source.ToString(),
    };

    public static string StateName(StartupItem item) =>
        item.StartType is { } type ? StartTypeName(type) : item.Enabled ? "啟用" : "已停用";

    public static string StartTypeName(ServiceStartType type) => type switch
    {
        ServiceStartType.Automatic => "自動",
        ServiceStartType.AutomaticDelayed => "自動（延遲啟動）",
        ServiceStartType.Manual => "手動",
        ServiceStartType.Disabled => "停用",
        ServiceStartType.Boot => "開機",
        ServiceStartType.System => "系統",
        _ => type.ToString(),
    };
}
