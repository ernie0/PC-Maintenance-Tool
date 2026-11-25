using System.Security.Cryptography;

namespace PcMaintenance.Remote.Core.Crypto;

/// <summary>
/// 一次連線用的 ECDH P-256 金鑰交換。每次連線雙方各自建立一組臨時金鑰，
/// 交換公鑰後各自導出相同的對稱金鑰；臨時金鑰用完即丟（前向保密）。
/// </summary>
public sealed class KeyExchange : IDisposable
{
    private const string Label = "pcremote-v1";
    private readonly ECDiffieHellman _ecdh;

    public KeyExchange()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>本端臨時公鑰（SubjectPublicKeyInfo 格式），送給對方。</summary>
    public byte[] PublicKey => _ecdh.ExportSubjectPublicKeyInfo();

    /// <summary>
    /// 用對方公鑰導出這次連線的金鑰。isInitiator 由誰發起連線決定，
    /// 兩端一個 true 一個 false，才會把收送金鑰對接起來。
    /// </summary>
    public SessionKeys DeriveKeys(byte[] peerPublicKey, bool isInitiator)
    {
        ArgumentNullException.ThrowIfNull(peerPublicKey);
        using var peer = ECDiffieHellman.Create();
        try
        {
            peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidKeyExchangeException("對方公鑰格式無法解析", ex);
        }

        if (peer.KeySize != _ecdh.KeySize)
            throw new InvalidKeyExchangeException("對方公鑰不是預期的 P-256 曲線");

        byte[] shared = _ecdh.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            // salt 用兩端公鑰排序後串接，和發起方無關，兩端算出同一個值；
            // 中間人對兩端各有不同的 shared，導出的金鑰與驗證碼都會不同。
            byte[] salt = TranscriptSalt(PublicKey, peerPublicKey);

            byte[] i2r = Expand(shared, salt, Label + " i2r", 32);
            byte[] r2i = Expand(shared, salt, Label + " r2i", 32);
            byte[] sas = Expand(shared, salt, Label + " sas", 4);

            // i2r、r2i 一把當送出、一把當接收，兩把都會用到，不清除。
            byte[] sendKey = isInitiator ? i2r : r2i;
            byte[] recvKey = isInitiator ? r2i : i2r;

            uint code = BitConverter.ToUInt32(sas) % 1_000_000;
            return new SessionKeys(sendKey, recvKey, code.ToString("D6"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    private static byte[] Expand(byte[] ikm, byte[] salt, string info, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, System.Text.Encoding.UTF8.GetBytes(info));

    private static byte[] TranscriptSalt(byte[] a, byte[] b)
    {
        bool aFirst = a.AsSpan().SequenceCompareTo(b) <= 0;
        byte[] first = aFirst ? a : b, second = aFirst ? b : a;
        byte[] combined = new byte[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);
        return SHA256.HashData(combined);
    }

    public void Dispose() => _ecdh.Dispose();
}

/// <summary>金鑰交換失敗（公鑰格式錯誤、曲線不符等）。</summary>
public sealed class InvalidKeyExchangeException : Exception
{
    public InvalidKeyExchangeException(string message, Exception? inner = null) : base(message, inner) { }
}
