using System.Security.Cryptography;
using System.Text;

namespace PcMaintenance.Remote.Core.Crypto;

/// <summary>
/// 把一次連線的臨時公鑰綁到管理者長期身分上的簽章：控制端用身分私鑰簽
/// (requestId‖臨時公鑰)，被控端用憑證裡的身分公鑰驗。這樣光有一張被側錄的憑證不能冒用——
/// 還要能簽出對應這次連線的簽章，才證明真的握有身分私鑰。配對與隱藏模式都靠它確認是同一位管理者。
/// </summary>
public static class SessionBinding
{
    public static byte[] Sign(ECDsa identityPrivate, string requestId, byte[] controllerPublicKey) =>
        identityPrivate.SignData(Compose(requestId, controllerPublicKey), HashAlgorithmName.SHA256);

    public static bool Verify(byte[] adminPublicKey, string requestId, byte[] controllerPublicKey, byte[] signature)
    {
        using var ec = ECDsa.Create();
        try
        {
            ec.ImportSubjectPublicKeyInfo(adminPublicKey, out _);
            return ec.VerifyData(Compose(requestId, controllerPublicKey), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>管理者身分的穩定指紋（身分公鑰的 SHA-256 十六進位）。配對清單以此為鍵。</summary>
    public static string Fingerprint(byte[] adminPublicKey) =>
        Convert.ToHexString(SHA256.HashData(adminPublicKey));

    private static byte[] Compose(string requestId, byte[] controllerPublicKey)
    {
        byte[] id = Encoding.UTF8.GetBytes(requestId);
        byte[] buffer = new byte[id.Length + controllerPublicKey.Length];
        id.CopyTo(buffer, 0);
        controllerPublicKey.CopyTo(buffer, id.Length);
        return buffer;
    }
}

/// <summary>
/// 控制端的管理者身分：長期身分私鑰加上公司根金鑰簽發的憑證。連線時附上憑證並簽這次連線的綁定值。
/// 私鑰只在這台管理者電腦上，不進 repo、不上伺服器。
/// </summary>
public sealed class AdminIdentity : IDisposable
{
    private readonly ECDsa _key;

    private AdminIdentity(ECDsa key, SignedAdminCertificate certificate)
    {
        _key = key;
        Certificate = certificate;
    }

    public SignedAdminCertificate Certificate { get; }

    public string Fingerprint => SessionBinding.Fingerprint(Certificate.Certificate.AdminPublicKey);

    /// <summary>用身分私鑰與其憑證組出一個身分（私鑰公鑰要和憑證內的身分公鑰相符）。</summary>
    public static AdminIdentity FromPrivateKey(byte[] pkcs8Private, SignedAdminCertificate certificate)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8Private, out _);
        return new AdminIdentity(key, certificate);
    }

    /// <summary>新建一對身分金鑰，並用公司根金鑰簽發憑證（簽發流程用；平時私鑰在管理者端）。</summary>
    public static (byte[] Pkcs8Private, byte[] PublicKey) CreateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec.ExportPkcs8PrivateKey(), ec.ExportSubjectPublicKeyInfo());
    }

    public byte[] SignBinding(string requestId, byte[] controllerPublicKey) =>
        SessionBinding.Sign(_key, requestId, controllerPublicKey);

    public void Dispose() => _key.Dispose();
}
