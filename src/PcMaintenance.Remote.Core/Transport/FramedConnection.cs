using System.Buffers.Binary;

namespace PcMaintenance.Remote.Core.Transport;

/// <summary>
/// 把一條位元組串流（例如 TCP 連線）切成一個個長度前綴框。
/// 線上格式：[4 byte 大端長度 N][N byte 內容]。內容是什麼本型別不管——
/// 交握時是明文公鑰，之後是 <see cref="Crypto.SecureChannel"/> 封好的密文框。
/// 讀寫各自在單一執行緒上使用即可（送一條執行緒、收一條執行緒）。
/// </summary>
public sealed class FramedConnection
{
    /// <summary>單一框的長度上限，防惡意或壞掉的長度前綴把記憶體吃光。1080p 關鍵影格遠小於此。</summary>
    public const int MaxFrameLength = 32 * 1024 * 1024;

    private readonly Stream _stream;
    // 讀與寫各一個長度緩衝：連線是全雙工，送執行緒與收執行緒會同時動長度前綴，
    // 共用一個緩衝會讓送出覆蓋掉接收剛讀進、還沒解析的長度位元組，造成框錯位、解密失敗、斷線。
    private readonly byte[] _writeLengthBuffer = new byte[4];
    private readonly byte[] _readLengthBuffer = new byte[4];

    public FramedConnection(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public async Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxFrameLength)
            throw new ArgumentException($"框長度 {payload.Length} 超過上限 {MaxFrameLength}", nameof(payload));
        BinaryPrimitives.WriteUInt32BigEndian(_writeLengthBuffer, (uint)payload.Length);
        await _stream.WriteAsync(_writeLengthBuffer, ct).ConfigureAwait(false);
        await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>讀一個完整的框。對方正常關閉連線時回傳 null。</summary>
    public async Task<byte[]?> ReadFrameAsync(CancellationToken ct = default)
    {
        int read = await ReadAtMostAsync(_readLengthBuffer, ct).ConfigureAwait(false);
        if (read == 0) return null;                 // 乾淨關閉
        if (read < 4) throw new TransportException("讀長度前綴時連線中斷");

        uint length = BinaryPrimitives.ReadUInt32BigEndian(_readLengthBuffer);
        if (length > MaxFrameLength)
            throw new TransportException($"對方宣告的框長度 {length} 超過上限 {MaxFrameLength}");

        byte[] payload = new byte[length];
        if (length > 0)
            await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    /// <summary>讀滿緩衝區；若一開始就遇到串流結尾回傳 0，用來分辨乾淨關閉。</summary>
    private async Task<int> ReadAtMostAsync(byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

public sealed class TransportException : Exception
{
    public TransportException(string message, Exception? inner = null) : base(message, inner) { }
}
