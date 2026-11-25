namespace PcMaintenance.Remote.Agent.Capture;

/// <summary>一個接在桌面上的螢幕。</summary>
public sealed record MonitorInfo
{
    public required int Index { get; init; }
    public required string DeviceName { get; init; }
    public required string AdapterName { get; init; }
    /// <summary>此螢幕在整個虛擬桌面座標中的左上角（像素）；輸入注入要用它把座標映回正確螢幕。</summary>
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool IsPrimary { get; init; }
}
