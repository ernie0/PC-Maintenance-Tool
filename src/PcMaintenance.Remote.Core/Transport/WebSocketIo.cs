using System.Net.WebSockets;

namespace PcMaintenance.Remote.Core.Transport;

/// <summary>WebSocket 收送一整個訊息的小工具（處理分段）。控制面用文字訊息、轉送面用二進位訊息。</summary>
public static class WebSocketIo
{
    public const int MaxMessageLength = 32 * 1024 * 1024;

    /// <summary>收一整個訊息；對方關閉回傳 null。</summary>
    public static async Task<(byte[] Data, WebSocketMessageType Type)?> ReceiveMessageAsync(
        WebSocket ws, CancellationToken ct)
    {
        var chunk = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(chunk, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(chunk, 0, result.Count);
            if (ms.Length > MaxMessageLength)
                throw new TransportException($"WebSocket 訊息超過上限 {MaxMessageLength}");
        }
        while (!result.EndOfMessage);
        return (ms.ToArray(), result.MessageType);
    }

    public static ValueTask SendMessageAsync(WebSocket ws, ReadOnlyMemory<byte> data,
        WebSocketMessageType type, CancellationToken ct) =>
        ws.SendAsync(data, type, endOfMessage: true, ct);
}
