namespace PcMaintenance.Remote.Agent.Encoding;

/// <summary>編碼器輸出的一個 H.264 片段。</summary>
public sealed class EncodedFrame
{
    public required byte[] Data { get; init; }
    public required long TimestampTicks { get; init; }
    /// <summary>是否為關鍵影格（可獨立解碼的起點）。</summary>
    public required bool IsKeyframe { get; init; }
}
