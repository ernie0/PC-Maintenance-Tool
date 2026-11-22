namespace PcMaintenance.Remote.Agent.Encoding;

/// <summary>
/// 依控制端回報的目標畫質，決定實際的位元率與幀率，並排程關鍵影格。純邏輯、可測，
/// 不碰編碼器或網路。控制端的 quality-report 進來、要用的設定出去。
/// </summary>
public sealed class StreamController
{
    public const int MinBitrateKbps = 200;
    public const int MaxBitrateKbps = 12_000;
    public const int MinFps = 1;
    public const int MaxFps = 60;

    private readonly int _keyframeIntervalSeconds;
    private long _lastKeyframeTicks;
    private bool _keyframeRequested = true; // 第一張一定是關鍵影格

    public StreamController(int bitrateKbps = 4_000, int fps = 30, int keyframeIntervalSeconds = 2)
    {
        BitrateKbps = Clamp(bitrateKbps, MinBitrateKbps, MaxBitrateKbps);
        Fps = Clamp(fps, MinFps, MaxFps);
        _keyframeIntervalSeconds = keyframeIntervalSeconds;
    }

    public int BitrateKbps { get; private set; }
    public int Fps { get; private set; }

    /// <summary>每張畫面最短間隔（毫秒），擷取迴圈用來控速。</summary>
    public double FrameIntervalMs => 1000.0 / Fps;

    /// <summary>套用控制端回報。回傳位元率或幀率是否有變（有變才需要重設編碼器）。</summary>
    public bool Apply(int targetBitrateKbps, int targetFps, bool requestKeyframe)
    {
        if (requestKeyframe) _keyframeRequested = true;
        int newBitrate = Clamp(targetBitrateKbps, MinBitrateKbps, MaxBitrateKbps);
        int newFps = Clamp(targetFps, MinFps, MaxFps);
        bool changed = newBitrate != BitrateKbps || newFps != Fps;
        BitrateKbps = newBitrate;
        Fps = newFps;
        return changed;
    }

    public void RequestKeyframe() => _keyframeRequested = true;

    /// <summary>
    /// 這一刻是否該送關鍵影格：有人要求，或距上次關鍵影格超過間隔。呼叫後清除要求並記錄時間。
    /// </summary>
    public bool ShouldEmitKeyframe(long nowTicks)
    {
        bool due = _keyframeRequested ||
                   nowTicks - _lastKeyframeTicks >= _keyframeIntervalSeconds * TimeSpan.TicksPerSecond;
        if (due)
        {
            _keyframeRequested = false;
            _lastKeyframeTicks = nowTicks;
        }
        return due;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
