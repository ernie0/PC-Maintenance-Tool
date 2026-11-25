using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMaintenance.Remote.Core.Crypto;

/// <summary>
/// 管理者憑證。公司根金鑰（離線保管）簽發給每位資訊人員，內容是：管理者公鑰、可操作的群組與模式、到期日。
/// 被控端安裝時寫入公司根公鑰，連線時驗證這張憑證由根金鑰簽署、未過期、模式在授權內，
/// 所以伺服器被攻破也無法偽造連線。P1 的有人值守協助用一次性密碼即可，這張憑證給 P2 無人值守與管理用。
/// </summary>
public sealed record AdminCertificate
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("admin")] public required string AdminName { get; init; }
    [JsonPropertyName("adminKey")] public required byte[] AdminPublicKey { get; init; }
    /// <summary>可用的模式：assist／manage／view／wall。</summary>
    [JsonPropertyName("modes")] public required string[] Modes { get; init; }
    /// <summary>可操作的電腦群組；["*"] 代表全部。</summary>
    [JsonPropertyName("groups")] public required string[] Groups { get; init; }
    [JsonPropertyName("notBefore")] public required DateTimeOffset NotBefore { get; init; }
    [JsonPropertyName("notAfter")] public required DateTimeOffset NotAfter { get; init; }

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        // 欄位順序固定（靠屬性宣告順序），不縮排，確保簽章前後位元組一致。
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>要被簽章或驗章的位元組，雙方算法一致。</summary>
    public byte[] ToSignedBytes() => JsonSerializer.SerializeToUtf8Bytes(this, CanonicalJson);

    public bool AllowsMode(string mode) => Array.Exists(Modes, m => m == mode);

    public bool AllowsGroup(string group) =>
        Array.Exists(Groups, g => g == "*" || string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
}

/// <summary>已簽章的憑證：憑證本體加上根金鑰的 ECDSA 簽章。</summary>
public sealed record SignedAdminCertificate
{
    [JsonPropertyName("cert")] public required AdminCertificate Certificate { get; init; }
    [JsonPropertyName("sig")] public required byte[] Signature { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static SignedAdminCertificate FromJson(string json) =>
        JsonSerializer.Deserialize<SignedAdminCertificate>(json)
        ?? throw new FormatException("憑證 JSON 解析為 null");
}

/// <summary>公司根金鑰：產生、簽發憑證。私鑰只在使用者的離線機器上，不進 repo、不上伺服器。</summary>
public sealed class CompanyRootKey : IDisposable
{
    private readonly ECDsa _ecdsa;

    private CompanyRootKey(ECDsa ecdsa) => _ecdsa = ecdsa;

    public static CompanyRootKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static CompanyRootKey FromPrivateKey(byte[] pkcs8)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return new CompanyRootKey(ecdsa);
    }

    public byte[] ExportPublicKey() => _ecdsa.ExportSubjectPublicKeyInfo();
    public byte[] ExportPrivateKey() => _ecdsa.ExportPkcs8PrivateKey();

    public SignedAdminCertificate Sign(AdminCertificate certificate)
    {
        byte[] signature = _ecdsa.SignData(certificate.ToSignedBytes(), HashAlgorithmName.SHA256);
        return new SignedAdminCertificate { Certificate = certificate, Signature = signature };
    }

    public void Dispose() => _ecdsa.Dispose();
}

/// <summary>被控端用：拿公司根公鑰驗證管理者憑證。</summary>
public sealed class CertificateVerifier : IDisposable
{
    private readonly ECDsa _rootPublic;

    public CertificateVerifier(byte[] rootPublicKey)
    {
        _rootPublic = ECDsa.Create();
        _rootPublic.ImportSubjectPublicKeyInfo(rootPublicKey, out _);
    }

    /// <summary>驗證簽章、有效期與（可選的）模式與群組。任何一項不過就回傳失敗原因。</summary>
    public CertificateCheck Verify(SignedAdminCertificate signed, string? requiredMode, string? requiredGroup,
        DateTimeOffset now)
    {
        bool ok;
        try
        {
            ok = _rootPublic.VerifyData(signed.Certificate.ToSignedBytes(), signed.Signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            ok = false;
        }
        if (!ok) return CertificateCheck.Fail("簽章無效，不是公司根金鑰簽發的");

        var cert = signed.Certificate;
        if (now < cert.NotBefore) return CertificateCheck.Fail("憑證尚未生效");
        if (now > cert.NotAfter) return CertificateCheck.Fail("憑證已過期");
        if (requiredMode is not null && !cert.AllowsMode(requiredMode))
            return CertificateCheck.Fail($"憑證未授權「{requiredMode}」模式");
        if (requiredGroup is not null && !cert.AllowsGroup(requiredGroup))
            return CertificateCheck.Fail($"憑證未授權群組「{requiredGroup}」");
        return CertificateCheck.Pass();
    }

    public void Dispose() => _rootPublic.Dispose();
}

public readonly record struct CertificateCheck(bool IsValid, string? Reason)
{
    public static CertificateCheck Pass() => new(true, null);
    public static CertificateCheck Fail(string reason) => new(false, reason);
}
