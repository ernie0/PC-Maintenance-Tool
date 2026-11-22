using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using PcMaintenance.Remote.Core.Crypto;
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
    private ISecureLink? _link;
    private int _displayW, _displayH;
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private int _currentMonitor;

    public event Action<string>? VerificationCodeReady;
    public event Action<IReadOnlyList<MonitorDescriptor>>? MonitorListReceived;
    public event Action<RenderedFrame>? FrameRendered;

    /// <summary>區網直連被控端並持續接收，直到連線關閉或取消。</summary>
    public async Task RunAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        tcp.NoDelay = true;

        using var link = await SecureSession.HandshakeAsync(tcp.GetStream(), isInitiator: true, ct).ConfigureAwait(false);
        await RunWithLinkAsync(link, ct).ConfigureAwait(false);
    }

    /// <summary>用一條已建立好的加密連線（直連或經伺服器轉送）持續接收。</summary>
    public async Task RunWithLinkAsync(ISecureLink link, CancellationToken ct)
    {
        _link = link;
        VerificationCodeReady?.Invoke(link.VerificationCode);

        while (!ct.IsCancellationRequested)
        {
            var msg = await link.ReceiveAsync(ct).ConfigureAwait(false);
            if (msg is null) break;
            Handle(msg.Value.Type, msg.Value.Payload);
        }
    }

    /// <summary>查伺服器目前線上的被控端清單（給連線畫面挑選）。</summary>
    public static async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(Uri serverBase, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = serverBase };
        return await http.GetFromJsonAsync<List<AgentSummary>>("api/agents", ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// 經伺服器連到某台被控端：送連線要求、在控制面交換公鑰、取得同意後開轉送連線持續接收。
    /// 被控端拒絕或不在線上時丟 <see cref="SessionRejectedException"/>。
    /// </summary>
    public async Task RunViaServerAsync(Uri serverBase, string agentId, string adminName,
        string mode = SessionMode.Assist, bool requestHidden = false,
        SignedAdminCertificate? certificate = null, CancellationToken ct = default)
    {
        string requestId = Guid.NewGuid().ToString("N");
        using var kex = new KeyExchange();

        using var controlWs = new ClientWebSocket();
        await controlWs.ConnectAsync(WsUri(serverBase, "/ws/control"), ct).ConfigureAwait(false);
        using var control = new ControlChannel(controlWs);

        await control.SendAsync(new SessionRequestMessage
        {
            RequestId = requestId,
            Mode = mode,
            AdminName = adminName,
            TargetAgentId = agentId,
            ControllerPublicKey = kex.PublicKey,
            Certificate = certificate,
            RequestHidden = requestHidden,
        }, ct).ConfigureAwait(false);

        var resp = await control.ReceiveAsync(ct).ConfigureAwait(false);
        if (resp is SessionRejectMessage reject)
            throw new SessionRejectedException(reject.Reason);
        if (resp is not SessionConsentMessage consent)
            throw new SessionRejectedException("被控端回應無法解析");

        SessionKeys keys = kex.DeriveKeys(consent.AgentPublicKey, isInitiator: true);

        using var relayWs = new ClientWebSocket();
        await relayWs.ConnectAsync(WsUri(serverBase, $"/ws/relay?session={Uri.EscapeDataString(requestId)}&role=controller"), ct).ConfigureAwait(false);
        var link = new WebSocketSecureLink(relayWs, keys);
        await RunWithLinkAsync(link, ct).ConfigureAwait(false);
    }

    private static Uri WsUri(Uri serverBase, string pathAndQuery)
    {
        string scheme = serverBase.Scheme == "https" ? "wss" : "ws";
        return new Uri($"{scheme}://{serverBase.Authority}{pathAndQuery}");
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
        _link?.SendAsync(FrameType.Input, ev.Encode(), ct) ?? Task.CompletedTask;

    public Task SwitchMonitorAsync(int index, CancellationToken ct = default)
    {
        _currentMonitor = index;
        ApplyDisplaySize();
        return _link?.SendAsync(FrameType.MonitorSwitch, MonitorProtocol.EncodeSwitch(index), ct) ?? Task.CompletedTask;
    }

    public void Dispose() => _decoder.Dispose();
}

/// <summary>被控端拒絕連線、不在線上或未回應。</summary>
public sealed class SessionRejectedException(string reason) : Exception($"連線被拒：{reason}")
{
    public string Reason { get; } = reason;
}
