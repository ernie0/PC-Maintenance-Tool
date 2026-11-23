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
    private readonly FileReassembler _incoming = new(
        Environment.GetEnvironmentVariable("PCM_VIEWER_DOWNLOADS")
        ?? Path.Combine(AppContext.BaseDirectory, "下載"));
    private ISecureLink? _link;
    private int _latestMove = -1; // 合流用：打包的 (nx<<16 | ny)，-1 代表沒有待送的滑鼠移動
    private int _displayW, _displayH;
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private int _currentMonitor;

    public ViewerClient() => _incoming.FileSaved += p => FileReceived?.Invoke(p);

    public event Action<string>? VerificationCodeReady;
    public event Action<IReadOnlyList<MonitorDescriptor>>? MonitorListReceived;
    public event Action<RenderedFrame>? FrameRendered;
    /// <summary>收到被控端送來的剪貼簿文字。</summary>
    public event Action<string>? ClipboardReceived;
    /// <summary>收到被控端傳來的檔案並通過核對，參數為本機存檔路徑。</summary>
    public event Action<string>? FileReceived;
    /// <summary>收到遠端資料夾清單：(路徑, 項目清單)。</summary>
    public event Action<string, IReadOnlyList<RemoteEntry>>? RemoteFilesReceived;
    /// <summary>經伺服器連線獲被控端同意時觸發：(是否隱藏進行, 本次是否完成配對)。</summary>
    public event Action<bool, bool>? SessionGranted;

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
        Volatile.Write(ref _latestMove, -1);
        VerificationCodeReady?.Invoke(link.VerificationCode);

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var movePump = MovePumpAsync(pumpCts.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await link.ReceiveAsync(ct).ConfigureAwait(false);
                if (msg is null) break;
                Handle(msg.Value.Type, msg.Value.Payload);
            }
        }
        finally
        {
            pumpCts.Cancel();
            try { await movePump.ConfigureAwait(false); } catch { /* 收尾 */ }
        }
    }

    /// <summary>以固定頻率（約 125Hz 上限）只送出最新的滑鼠位置，避免連續移動產生洪水般的事件、累積延遲又浪費頻寬。</summary>
    private async Task MovePumpAsync(CancellationToken ct)
    {
        int lastSent = -1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(8, ct).ConfigureAwait(false);
                int cur = Volatile.Read(ref _latestMove);
                if (cur < 0 || cur == lastSent) continue;
                lastSent = cur;
                var link = _link;
                if (link is null) continue;
                var ev = InputEvent.Move((ushort)(cur >> 16), (ushort)(cur & 0xFFFF));
                try { await link.SendAsync(FrameType.Input, ev.Encode(), ct).ConfigureAwait(false); } catch { }
            }
        }
        catch (OperationCanceledException) { }
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
        AdminIdentity? identity = null, CancellationToken ct = default)
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
            Certificate = identity?.Certificate,
            IdentitySignature = identity?.SignBinding(requestId, kex.PublicKey),
            RequestHidden = requestHidden,
        }, ct).ConfigureAwait(false);

        var resp = await control.ReceiveAsync(ct).ConfigureAwait(false);
        if (resp is SessionRejectMessage reject)
            throw new SessionRejectedException(reject.Reason);
        if (resp is not SessionConsentMessage consent)
            throw new SessionRejectedException("被控端回應無法解析");

        SessionGranted?.Invoke(consent.Hidden, consent.PairingEstablished);
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
            case FrameType.Clipboard:
                ClipboardReceived?.Invoke(System.Text.Encoding.UTF8.GetString(payload));
                break;
            case FrameType.FileChunk:
                _incoming.Handle(payload);
                break;
            case FrameType.RemoteFiles:
                if (RemoteFilesProtocol.TryDecodeListResponse(payload, out var rpath, out var rentries))
                    RemoteFilesReceived?.Invoke(rpath, rentries);
                break;
        }
    }

    /// <summary>把本機剪貼簿文字送到被控端。</summary>
    public Task SendClipboardAsync(string text, CancellationToken ct = default) =>
        _link?.SendAsync(FrameType.Clipboard, System.Text.Encoding.UTF8.GetBytes(text), ct) ?? Task.CompletedTask;

    /// <summary>把一個檔案傳到被控端（分塊送、附 SHA-256 讓對方核對）。progress 回報 0..1。</summary>
    public Task SendFileAsync(string path, IProgress<double>? progress = null, CancellationToken ct = default) =>
        _link is { } link ? FileSender.SendAsync(link, path, progress, ct) : Task.CompletedTask;

    /// <summary>向被控端要一個資料夾清單（空字串＝磁碟根目錄清單）。回應由 <see cref="RemoteFilesReceived"/> 送出。</summary>
    public Task RequestRemoteListAsync(string path, CancellationToken ct = default) =>
        _link?.SendAsync(FrameType.RemoteFiles, RemoteFilesProtocol.EncodeListRequest(path), ct) ?? Task.CompletedTask;

    /// <summary>向被控端要一個檔案（被控端會把該檔分塊送回，由 FileReassembler 收到本機）。</summary>
    public Task RequestRemotePullAsync(string filePath, CancellationToken ct = default) =>
        _link?.SendAsync(FrameType.RemoteFiles, RemoteFilesProtocol.EncodePullRequest(filePath), ct) ?? Task.CompletedTask;

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

    /// <summary>
    /// 排入一次滑鼠移動（相對目前螢幕的 0..65535 比例值）。只保留最新位置，由內部定頻送出，
    /// 讓 UI 的高頻 MouseMove 不會塞爆連線。按鍵、點擊、滾輪仍走 <see cref="SendInputAsync"/> 即時送。
    /// </summary>
    public void QueueMouseMove(ushort normX, ushort normY) =>
        Volatile.Write(ref _latestMove, (normX << 16) | normY);

    public Task SwitchMonitorAsync(int index, CancellationToken ct = default)
    {
        _currentMonitor = index;
        ApplyDisplaySize();
        return _link?.SendAsync(FrameType.MonitorSwitch, MonitorProtocol.EncodeSwitch(index), ct) ?? Task.CompletedTask;
    }

    public void Dispose() { _decoder.Dispose(); _incoming.Dispose(); }
}

/// <summary>被控端拒絕連線、不在線上或未回應。</summary>
public sealed class SessionRejectedException(string reason) : Exception($"連線被拒：{reason}")
{
    public string Reason { get; } = reason;
}
