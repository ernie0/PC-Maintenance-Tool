using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PcMaintenance.Remote.Core.Crypto;

/// <summary>金鑰交換導出的一次連線金鑰，以及雙方要人工核對的驗證碼。</summary>
public sealed class SessionKeys
{
    internal SessionKeys(byte[] sendKey, byte[] recvKey, string verificationCode)
    {
        SendKey = sendKey;
        RecvKey = recvKey;
        VerificationCode = verificationCode;
    }

    internal byte[] SendKey { get; }
    internal byte[] RecvKey { get; }

    /// <summary>兩端畫面上各顯示一次、由使用者口頭核對的六位數驗證碼，用來擋中間人。</summary>
    public string VerificationCode { get; }

    public SecureChannel CreateChannel() => new(SendKey, RecvKey);
}

/// <summary>
/// 一條連線的加解密通道。送出與接收各一把 AES-256-GCM 金鑰、各一個遞增計數器，
/// 計數器就是 nonce，單向嚴格遞增、不重用；收端要求計數器遞增以擋重放。
/// 本型別不是執行緒安全，送與收請各自在單一執行緒上使用。
/// </summary>
public sealed class SecureChannel : IDisposable
{
    private const int TagSize = 16;
    private const int CounterSize = 8;
    private const int HeaderSize = 1 + CounterSize; // 1 byte 類型 + 8 byte 計數器
    private const ulong MaxCounter = ulong.MaxValue - 1;

    private readonly AesGcm _send;
    private readonly AesGcm _recv;
    private ulong _sendCounter;
    private ulong _recvCounter;
    private bool _recvStarted;

    internal SecureChannel(byte[] sendKey, byte[] recvKey)
    {
        _send = new AesGcm(sendKey, TagSize);
        _recv = new AesGcm(recvKey, TagSize);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(recvKey);
    }

    /// <summary>
    /// 把一段明文封成可直接送上線的框：[1 byte 類型][8 byte 計數器][密文+tag]。
    /// 類型 byte 納入 AAD，所以在線上被改類型會驗證失敗。
    /// </summary>
    public byte[] Seal(byte frameType, ReadOnlySpan<byte> plaintext)
    {
        if (_sendCounter > MaxCounter)
            throw new InvalidOperationException("這條連線的送出計數器已用盡，必須重新交換金鑰");

        ulong counter = _sendCounter++;
        byte[] frame = new byte[HeaderSize + plaintext.Length + TagSize];
        frame[0] = frameType;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(1, CounterSize), counter);

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);

        Span<byte> cipher = frame.AsSpan(HeaderSize, plaintext.Length);
        Span<byte> tag = frame.AsSpan(HeaderSize + plaintext.Length, TagSize);
        _send.Encrypt(nonce, plaintext, cipher, tag, frame.AsSpan(0, 1));
        return frame;
    }

    /// <summary>
    /// 解開 <see cref="Seal"/> 封的框，回傳明文與類型。計數器必須比上一次收到的大，
    /// 否則視為重放或亂序，丟 <see cref="ReplayException"/>。
    /// </summary>
    public byte[] Open(ReadOnlySpan<byte> frame, out byte frameType)
    {
        if (frame.Length < HeaderSize + TagSize)
            throw new SecureChannelException("框長度不足");

        frameType = frame[0];
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(1, CounterSize));
        if (_recvStarted && counter <= _recvCounter)
            throw new ReplayException($"收到的計數器沒有遞增（這次 {counter}、上次 {_recvCounter}）");

        int cipherLength = frame.Length - HeaderSize - TagSize;
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);

        byte[] plaintext = new byte[cipherLength];
        try
        {
            _recv.Decrypt(nonce, frame.Slice(HeaderSize, cipherLength),
                frame.Slice(HeaderSize + cipherLength, TagSize), plaintext, frame.Slice(0, 1));
        }
        catch (CryptographicException ex)
        {
            throw new SecureChannelException("解密或驗證失敗（金鑰不符或內容被竄改）", ex);
        }

        _recvCounter = counter;
        _recvStarted = true;
        return plaintext;
    }

    public void Dispose()
    {
        _send.Dispose();
        _recv.Dispose();
    }
}

public class SecureChannelException : Exception
{
    public SecureChannelException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>收到的計數器沒有嚴格遞增，可能是重放攻擊或連線亂序。</summary>
public sealed class ReplayException : SecureChannelException
{
    public ReplayException(string message) : base(message) { }
}
