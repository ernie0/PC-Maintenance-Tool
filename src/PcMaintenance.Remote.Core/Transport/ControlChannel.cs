using System.Net.WebSockets;
using System.Text;
using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Core.Transport;

/// <summary>
/// 在一條 WebSocket 上收送控制訊息（JSON 文字框）。被控端↔伺服器↔控制端的註冊、連線要求、
/// 同意／拒絕都走這條。送出有鎖；收下在單一執行緒。這條是明文、伺服器看得到，用來路由與稽核；
/// 真正的畫面與操作走另一條端對端加密的轉送通道。
/// </summary>
public sealed class ControlChannel : IDisposable
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ControlChannel(WebSocket ws) => _ws = ws;

    public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ControlProtocol.Serialize(message));
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await WebSocketIo.SendMessageAsync(_ws, bytes, WebSocketMessageType.Text, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    /// <summary>收下一個控制訊息。對方關閉回傳 null。不認得的型別會丟 <see cref="UnknownControlMessageException"/>。</summary>
    public async Task<ControlMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var msg = await WebSocketIo.ReceiveMessageAsync(_ws, ct).ConfigureAwait(false);
        if (msg is null) return null;
        if (msg.Value.Type != WebSocketMessageType.Text)
            throw new TransportException("控制通道收到非文字訊息");
        return ControlProtocol.Deserialize(Encoding.UTF8.GetString(msg.Value.Data));
    }

    public void Dispose() => _sendLock.Dispose();
}
