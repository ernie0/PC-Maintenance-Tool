using System.Threading.Channels;
using PcMaintenance.Remote.Agent.Capture;
using PcMaintenance.Remote.Agent.Encoding;
using PcMaintenance.Remote.Agent.Input;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Core.Transport;

namespace PcMaintenance.Remote.Agent.Session;

public sealed record AgentSessionOptions
{
    public int Fps { get; init; } = 30;
    public int BitrateKbps { get; init; } = 4000;
    public int StartMonitorIndex { get; init; } = 0;
}

/// <summary>
/// 被控端一條連線的全部流程：對已連上的串流做交握（回應端），持續擷取→編碼→送影像，
/// 同時收控制端的輸入事件打進本機、依要求切換螢幕。編碼器中途故障時就地重建並補關鍵影格，
/// 不讓整條連線掛掉。連線關閉或取消時收尾。非重複使用：一條連線一個實例。
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly ISecureLink _link;
    private readonly AgentSessionOptions _opts;
    private readonly InputInjector _injector = new();
    private readonly Channel<(FrameType Type, byte[] Payload)> _outgoing =
        Channel.CreateBounded<(FrameType, byte[])>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 塞車時丟最舊的影像，壓低延遲；關鍵影格約每秒一張會把畫面補回來
            SingleReader = true,
        });

    private DesktopDuplicator? _capturer;
    private H264Encoder? _encoder;
    private volatile int _switchRequest = -1;

    public AgentSession(ISecureLink link, AgentSessionOptions? options = null)
    {
        _link = link;
        _opts = options ?? new AgentSessionOptions();
    }

    /// <summary>兩端可核對的六位數驗證碼，連線建立時觸發一次，給上層顯示。</summary>
    public event Action<string>? VerificationCodeReady;

    public async Task RunAsync(CancellationToken ct)
    {
        VerificationCodeReady?.Invoke(_link.VerificationCode);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        // 螢幕清單先送一次
        EnqueueMonitorList();

        var captureThread = new Thread(() => CaptureLoop(token)) { IsBackground = true, Name = "agent-capture" };
        captureThread.Start();

        var sendPump = SendPumpAsync(_link, token);
        var receiveLoop = ReceiveLoopAsync(_link, token);

        // 任一邊結束（連線關閉、取消、送出失敗）就收掉整條連線
        await Task.WhenAny(sendPump, receiveLoop).ConfigureAwait(false);
        linked.Cancel();
        _outgoing.Writer.TryComplete();
        captureThread.Join(TimeSpan.FromSeconds(3));
        await Task.WhenAll(
            sendPump.ContinueWith(_ => { }, TaskScheduler.Default),
            receiveLoop.ContinueWith(_ => { }, TaskScheduler.Default)).ConfigureAwait(false);
    }

    private async Task SendPumpAsync(ISecureLink link, CancellationToken ct)
    {
        try
        {
            await foreach (var (type, payload) in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await link.SendAsync(type, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 連線斷了，交由 RunAsync 收尾 */ }
    }

    private async Task ReceiveLoopAsync(ISecureLink link, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await link.ReceiveAsync(ct).ConfigureAwait(false);
                if (msg is null) break; // 控制端關閉連線
                Dispatch(msg.Value.Type, msg.Value.Payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 連線斷了 */ }
    }

    private void Dispatch(FrameType type, byte[] payload)
    {
        switch (type)
        {
            case FrameType.Input:
                if (InputEvent.TryDecode(payload, out var ev)) _injector.Inject(ev);
                break;
            case FrameType.MonitorSwitch:
                if (MonitorProtocol.TryDecodeSwitch(payload, out int idx)) _switchRequest = idx;
                break;
        }
    }

    // ---- 擷取與編碼（獨立執行緒）----

    private void CaptureLoop(CancellationToken ct)
    {
        try
        {
            Vortice.MediaFoundation.MediaFactory.MFStartup(false).CheckError();
            RebuildCapture(_opts.StartMonitorIndex);

            byte[] nv12 = new byte[_capturer!.Nv12Size];
            long frameTicks = TimeSpan.TicksPerSecond / _opts.Fps;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long frame = 0;

            while (!ct.IsCancellationRequested)
            {
                int req = Interlocked.Exchange(ref _switchRequest, -1);
                if (req >= 0)
                {
                    RebuildCapture(req);
                    nv12 = new byte[_capturer!.Nv12Size];
                    EnqueueMonitorList();
                }

                double due = frame * 1000.0 / _opts.Fps;
                double now = sw.Elapsed.TotalMilliseconds;
                if (now < due) Thread.Sleep((int)Math.Min(due - now, 15));

                try
                {
                    if (nv12.Length < _capturer!.Nv12Size) nv12 = new byte[_capturer.Nv12Size];
                    _capturer.TryGetNv12(5, nv12, out _);
                    _encoder!.Submit(nv12, frame * frameTicks);
                }
                catch (InvalidOperationException)
                {
                    // 編碼器背景執行緒故障（偶發硬體錯誤）→ 就地重建，下一張會是關鍵影格把畫面補回來
                    RebuildEncoder();
                }
                catch (SharpGen.Runtime.SharpGenException)
                {
                    RebuildCapture(_capturer!.Monitor.Index); // 擷取側故障，重建
                    nv12 = new byte[_capturer!.Nv12Size];
                }
                frame++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 擷取執行緒結束，連線會隨之收尾 */ }
        finally
        {
            _encoder?.Stop();
            _encoder?.Dispose();
            _capturer?.Dispose();
        }
    }

    private void RebuildCapture(int monitorIndex)
    {
        _encoder?.Stop();
        _encoder?.Dispose();
        _capturer?.Dispose();
        _capturer = new DesktopDuplicator(monitorIndex);
        var m = _capturer.Monitor;
        _injector.SetMonitorBounds(m.Left, m.Top, m.Width, m.Height);
        BuildEncoder();
    }

    private void RebuildEncoder()
    {
        try { _encoder?.Stop(); _encoder?.Dispose(); } catch { }
        BuildEncoder();
    }

    private void BuildEncoder()
    {
        var enc = new H264Encoder(_capturer!.Width, _capturer.Height, _opts.Fps, _opts.BitrateKbps);
        enc.FrameEncoded += OnFrameEncoded;
        enc.Start();
        _encoder = enc;
    }

    private void OnFrameEncoded(EncodedFrame f) =>
        _outgoing.Writer.TryWrite((FrameType.Video, VideoPayload.Encode(f.TimestampTicks, f.IsKeyframe, f.Data)));

    private void EnqueueMonitorList()
    {
        var monitors = DesktopDuplicator.ListMonitors()
            .Select(m => new MonitorDescriptor
            {
                Index = m.Index,
                Width = m.Width,
                Height = m.Height,
                IsPrimary = m.IsPrimary,
                Name = $"螢幕 {m.Index + 1}（{m.Width}×{m.Height}）{(m.IsPrimary ? " 主" : "")}",
            })
            .ToList();
        _outgoing.Writer.TryWrite((FrameType.MonitorList, MonitorProtocol.EncodeList(monitors)));
    }

    public ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        _link.Dispose();
        return ValueTask.CompletedTask;
    }
}
