using System.Net.WebSockets;
using PcMaintenance.Remote.Core.Crypto;
using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Core.Transport;

/// <summary>
/// 經伺服器轉送的端對端加密連線：金鑰已在控制面交換好（SessionRequest/Consent 互換公鑰），
/// 這裡用導出的金鑰把每個框封成一個 WebSocket 二進位訊息。伺服器只對轉二進位、看不到內容。
/// 每個訊息剛好一個框，所以不需要再加長度前綴。
/// </summary>
public sealed class WebSocketSecureLink : ISecureLink
{
    private readonly WebSocket _ws;
    private readonly SecureChannel _channel;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketSecureLink(WebSocket ws, SessionKeys keys)
    {
        _ws = ws;
        _channel = keys.CreateChannel();
        VerificationCode = keys.VerificationCode;
    }

    public string VerificationCode { get; }

    public async Task SendAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] frame = _channel.Seal((byte)type, payload.Span);
            await WebSocketIo.SendMessageAsync(_ws, frame, WebSocketMessageType.Binary, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public async Task<(FrameType Type, byte[] Payload)?> ReceiveAsync(CancellationToken ct = default)
    {
        var msg = await WebSocketIo.ReceiveMessageAsync(_ws, ct).ConfigureAwait(false);
        if (msg is null) return null;
        if (msg.Value.Type != WebSocketMessageType.Binary)
            throw new TransportException("轉送通道收到非二進位訊息");
        byte[] plaintext = _channel.Open(msg.Value.Data, out byte type);
        return ((FrameType)type, plaintext);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _sendLock.Dispose();
    }
}
