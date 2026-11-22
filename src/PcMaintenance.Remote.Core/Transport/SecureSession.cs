using PcMaintenance.Remote.Core.Crypto;
using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Core.Transport;

/// <summary>
/// 一條已完成金鑰交換、可直接收送加密框的連線。建立方式是對一條已連上的位元組串流
/// 呼叫 <see cref="HandshakeAsync"/>：雙方先交換臨時公鑰（明文、長度前綴），各自導出金鑰，
/// 之後所有資料都經 <see cref="SecureChannel"/> 加密。發起端與回應端一端傳 isInitiator=true、
/// 另一端 false，收送金鑰才會對接。
/// </summary>
public sealed class SecureSession : ISecureLink
{
    private readonly FramedConnection _conn;
    private readonly SecureChannel _channel;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private SecureSession(FramedConnection conn, SecureChannel channel, string verificationCode)
    {
        _conn = conn;
        _channel = channel;
        VerificationCode = verificationCode;
    }

    /// <summary>兩端各自顯示、由使用者口頭核對的六位數驗證碼（擋中間人）。</summary>
    public string VerificationCode { get; }

    public static async Task<SecureSession> HandshakeAsync(Stream stream, bool isInitiator, CancellationToken ct = default)
    {
        var conn = new FramedConnection(stream);
        using var exchange = new KeyExchange();

        // 雙方都先送出自己的公鑰再讀對方的。公鑰很小（約 91 byte），TCP 送出緩衝塞得下，不會互等死結。
        await conn.WriteFrameAsync(exchange.PublicKey, ct).ConfigureAwait(false);
        byte[] peerKey = await conn.ReadFrameAsync(ct).ConfigureAwait(false)
            ?? throw new TransportException("交握時對方未送出公鑰就關閉連線");

        SessionKeys keys = exchange.DeriveKeys(peerKey, isInitiator);
        return new SecureSession(conn, keys.CreateChannel(), keys.VerificationCode);
    }

    public async Task SendAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        // Seal 會動到送出計數器，多個送出端要排隊；收端是另一把金鑰與計數器，不受此鎖影響。
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] frame = _channel.Seal((byte)type, payload.Span);
            await _conn.WriteFrameAsync(frame, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>收下一個框並解密。對方關閉連線時回傳 null。</summary>
    public async Task<(FrameType Type, byte[] Payload)?> ReceiveAsync(CancellationToken ct = default)
    {
        byte[]? frame = await _conn.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frame is null) return null;
        byte[] plaintext = _channel.Open(frame, out byte type);
        return ((FrameType)type, plaintext);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _sendLock.Dispose();
    }
}
