using System.Net.Sockets;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Core.Transport;
using PcMaintenance.Remote.Viewer.Decoding;

namespace PcMaintenance.Remote.Viewer.Net;

/// <summary>一張已轉成 BGRA、可直接畫的畫面。</summary>
public sealed record RenderedFrame(byte[] Bgra, int Width, int Height);

/// <summary>
/// 控制端的連線邏輯（不含 UI）：連到被控端、交握（發起端）、收影像解碼轉成 BGRA、
/// 把本機輸入事件與切換螢幕的要求送回去。畫面與螢幕清單以事件丟出，UI 或驗證工具各自接。
/// 事件在接收執行緒上觸發，WPF 端要自行 Dispatcher.Invoke 回 UI 執行緒。
/// </summary>
public sealed class ViewerClient : IDisposable
{
    private readonly H264Decoder _decoder = new();
    private SecureSession? _session;
    private int _displayW, _displayH;
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private int _currentMonitor;

    public event Action<string>? VerificationCodeReady;
    public event Action<IReadOnlyList<MonitorDescriptor>>? MonitorListReceived;
    public event Action<RenderedFrame>? FrameRendered;

    /// <summary>連到被控端並持續接收，直到連線關閉或取消。</summary>
    public async Task RunAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        tcp.NoDelay = true;

        using var session = await SecureSession.HandshakeAsync(tcp.GetStream(), isInitiator: true, ct).ConfigureAwait(false);
        _session = session;
        VerificationCodeReady?.Invoke(session.VerificationCode);

        while (!ct.IsCancellationRequested)
        {
            var msg = await session.ReceiveAsync(ct).ConfigureAwait(false);
            if (msg is null) break;
            Handle(msg.Value.Type, msg.Value.Payload);
        }
    }

    private void Handle(FrameType type, byte[] payload)
    {
        switch (type)
        {
            case FrameType.Video:
                if (VideoPayload.TryDecode(payload, out long ts, out _, out var h264))
                    _decoder.Decode(h264, ts, OnNv12);
                break;
            case FrameType.MonitorList:
                _monitors = MonitorProtocol.DecodeList(payload);
                ApplyDisplaySize();
                MonitorListReceived?.Invoke(_monitors);
                break;
        }
    }

    private void OnNv12(DecodedNv12 f)
    {
        int w = _displayW > 0 ? _displayW : f.CodedWidth;
        int h = _displayH > 0 ? _displayH : f.CodedHeight;
        byte[] bgra = Nv12ToBgra.Convert(f.Nv12, f.CodedWidth, f.CodedHeight, f.StrideY, w, h);
        int outW = Math.Min(w, f.CodedWidth);
        int outH = Math.Min(h, f.CodedHeight);
        FrameRendered?.Invoke(new RenderedFrame(bgra, outW, outH));
    }

    private void ApplyDisplaySize()
    {
        var m = _monitors.FirstOrDefault(x => x.Index == _currentMonitor) ?? _monitors.FirstOrDefault();
        if (m is not null) { _displayW = m.Width; _displayH = m.Height; }
    }

    public Task SendInputAsync(InputEvent ev, CancellationToken ct = default) =>
        _session?.SendAsync(FrameType.Input, ev.Encode(), ct) ?? Task.CompletedTask;

    public Task SwitchMonitorAsync(int index, CancellationToken ct = default)
    {
        _currentMonitor = index;
        ApplyDisplaySize();
        return _session?.SendAsync(FrameType.MonitorSwitch, MonitorProtocol.EncodeSwitch(index), ct) ?? Task.CompletedTask;
    }

    public void Dispose() => _decoder.Dispose();
}
