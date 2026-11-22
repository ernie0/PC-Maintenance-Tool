using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Core.Transport;

/// <summary>
/// 一條已完成金鑰交換、可直接收送加密框的連線，抽象掉底層是 TCP 直連還是經伺服器轉送的 WebSocket。
/// 被控端會談與控制端邏輯都只依賴這個介面，兩種連法共用同一套收送程式。
/// 送出要排隊（同一把金鑰的計數器），收下在另一條執行緒進行。
/// </summary>
public interface ISecureLink : IDisposable
{
    /// <summary>兩端各自顯示、由使用者口頭核對的六位數驗證碼（擋中間人）。</summary>
    string VerificationCode { get; }

    Task SendAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>收下一個框並解密。對方關閉連線時回傳 null。</summary>
    Task<(FrameType Type, byte[] Payload)?> ReceiveAsync(CancellationToken ct = default);
}
