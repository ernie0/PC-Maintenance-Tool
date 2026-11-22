namespace PcMaintenance.Remote.Agent.Capture;

/// <summary>一個接在桌面上的螢幕。</summary>
public sealed record MonitorInfo
{
    public required int Index { get; init; }
    public required string DeviceName { get; init; }
    public required string AdapterName { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool IsPrimary { get; init; }
}
