using System.Security.Cryptography;
using System.Text;
using PcMaintenance.Remote.Core.Crypto;

namespace PcMaintenance.Remote.Core.Tests;

public class KeyExchangeTests
{
    private static (SessionKeys initiator, SessionKeys responder) Handshake()
    {
        using var a = new KeyExchange();
        using var b = new KeyExchange();
        var ka = a.DeriveKeys(b.PublicKey, isInitiator: true);
        var kb = b.DeriveKeys(a.PublicKey, isInitiator: false);
        return (ka, kb);
    }

    [Fact]
    public void Both_sides_compute_the_same_verification_code()
    {
        var (ka, kb) = Handshake();
        Assert.Equal(ka.VerificationCode, kb.VerificationCode);
        Assert.Matches("^[0-9]{6}$", ka.VerificationCode);
    }

    [Fact]
    public void Derived_keys_let_each_side_decrypt_the_other()
    {
        var (ka, kb) = Handshake();
        using var ca = ka.CreateChannel();
        using var cb = kb.CreateChannel();

        byte[] fromA = ca.Seal(1, Encoding.UTF8.GetBytes("來自發起端"));
        Assert.Equal("來自發起端", Encoding.UTF8.GetString(cb.Open(fromA, out _)));

        byte[] fromB = cb.Seal(1, Encoding.UTF8.GetBytes("來自回應端"));
        Assert.Equal("來自回應端", Encoding.UTF8.GetString(ca.Open(fromB, out _)));
    }

    [Fact]
    public void Separate_handshakes_produce_different_codes()
    {
        var (first, _) = Handshake();
        var (second, _) = Handshake();
        Assert.NotEqual(first.VerificationCode, second.VerificationCode);
    }

    [Fact]
    public void A_man_in_the_middle_gets_different_codes_on_each_side()
    {
        // 中間人 M 分別和 A、B 握手；A 看到的碼與 B 看到的碼對不起來，使用者口頭核對就會發現。
        using var a = new KeyExchange();
        using var b = new KeyExchange();
        using var m = new KeyExchange();

        var aWithM = a.DeriveKeys(m.PublicKey, isInitiator: true);
        var mWithA = m.DeriveKeys(a.PublicKey, isInitiator: false);
        var mWithB = m.DeriveKeys(b.PublicKey, isInitiator: true);
        var bWithM = b.DeriveKeys(m.PublicKey, isInitiator: false);

        Assert.Equal(aWithM.VerificationCode, mWithA.VerificationCode);
        Assert.Equal(mWithB.VerificationCode, bWithM.VerificationCode);
        Assert.NotEqual(aWithM.VerificationCode, bWithM.VerificationCode);
    }

    [Fact]
    public void Garbage_public_key_is_rejected()
    {
        using var a = new KeyExchange();
        Assert.Throws<InvalidKeyExchangeException>(() => a.DeriveKeys(new byte[] { 1, 2, 3 }, true));
    }
}

public class SecureChannelTests
{
    private static (SecureChannel a, SecureChannel b) Pair()
    {
        using var ka = new KeyExchange();
        using var kb = new KeyExchange();
        return (ka.DeriveKeys(kb.PublicKey, true).CreateChannel(),
                kb.DeriveKeys(ka.PublicKey, false).CreateChannel());
    }

    [Fact]
    public void Round_trips_many_frames_in_order()
    {
        var (a, b) = Pair();
        for (int i = 0; i < 1000; i++)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"frame-{i}");
            byte[] frame = a.Seal((byte)(i % 256), payload);
            byte[] got = b.Open(frame, out byte type);
            Assert.Equal((byte)(i % 256), type);
            Assert.Equal(payload, got);
        }
    }

    [Fact]
    public void Empty_payload_round_trips()
    {
        var (a, b) = Pair();
        byte[] frame = a.Seal(9, ReadOnlySpan<byte>.Empty);
        Assert.Empty(b.Open(frame, out byte type));
        Assert.Equal(9, type);
    }

    [Fact]
    public void Tampered_ciphertext_fails_to_open()
    {
        var (a, b) = Pair();
        byte[] frame = a.Seal(1, Encoding.UTF8.GetBytes("機密"));
        frame[^1] ^= 0xFF; // 動 tag 最後一個 byte
        Assert.Throws<SecureChannelException>(() => b.Open(frame, out _));
    }

    [Fact]
    public void Tampered_frame_type_fails_because_type_is_authenticated()
    {
        var (a, b) = Pair();
        byte[] frame = a.Seal(1, Encoding.UTF8.GetBytes("x"));
        frame[0] = 2; // 類型是 AAD，改了就驗不過
        Assert.Throws<SecureChannelException>(() => b.Open(frame, out _));
    }

    [Fact]
    public void Replayed_frame_is_rejected()
    {
        var (a, b) = Pair();
        byte[] f0 = a.Seal(1, Encoding.UTF8.GetBytes("0"));
        byte[] f1 = a.Seal(1, Encoding.UTF8.GetBytes("1"));
        b.Open(f0, out _);
        b.Open(f1, out _);
        Assert.Throws<ReplayException>(() => b.Open(f0, out _)); // 舊計數器重放
    }

    [Fact]
    public void Wrong_key_cannot_decrypt()
    {
        var (a, _) = Pair();
        var (_, other) = Pair();
        byte[] frame = a.Seal(1, Encoding.UTF8.GetBytes("x"));
        Assert.Throws<SecureChannelException>(() => other.Open(frame, out _));
    }

    [Fact]
    public void Short_frame_is_rejected()
    {
        var (_, b) = Pair();
        Assert.Throws<SecureChannelException>(() => b.Open(new byte[] { 1, 2, 3 }, out _));
    }
}

public class AdminCertificateTests
{
    private static AdminCertificate SampleCert(byte[] adminKey, DateTimeOffset? from = null, DateTimeOffset? to = null) => new()
    {
        AdminName = "資訊課-王工程師",
        AdminPublicKey = adminKey,
        Modes = new[] { SessionModeNames.Manage, SessionModeNames.View },
        Groups = new[] { "工地" },
        NotBefore = from ?? DateTimeOffset.UtcNow.AddMinutes(-1),
        NotAfter = to ?? DateTimeOffset.UtcNow.AddYears(1),
    };

    private static class SessionModeNames
    {
        public const string Manage = "manage";
        public const string View = "view";
    }

    [Fact]
    public void Valid_certificate_passes()
    {
        using var root = CompanyRootKey.Create();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = root.Sign(SampleCert(admin.ExportSubjectPublicKeyInfo()));

        using var verifier = new CertificateVerifier(root.ExportPublicKey());
        var check = verifier.Verify(signed, "manage", "工地", DateTimeOffset.UtcNow);
        Assert.True(check.IsValid, check.Reason);
    }

    [Fact]
    public void Certificate_from_another_root_fails()
    {
        using var root = CompanyRootKey.Create();
        using var attacker = CompanyRootKey.Create();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = attacker.Sign(SampleCert(admin.ExportSubjectPublicKeyInfo()));

        using var verifier = new CertificateVerifier(root.ExportPublicKey());
        Assert.False(verifier.Verify(forged, null, null, DateTimeOffset.UtcNow).IsValid);
    }

    [Fact]
    public void Tampered_certificate_fails()
    {
        using var root = CompanyRootKey.Create();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = root.Sign(SampleCert(admin.ExportSubjectPublicKeyInfo()));
        var tampered = signed with { Certificate = signed.Certificate with { Groups = new[] { "*" } } };

        using var verifier = new CertificateVerifier(root.ExportPublicKey());
        Assert.False(verifier.Verify(tampered, null, null, DateTimeOffset.UtcNow).IsValid);
    }

    [Fact]
    public void Expired_certificate_fails()
    {
        using var root = CompanyRootKey.Create();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = root.Sign(SampleCert(admin.ExportSubjectPublicKeyInfo(),
            to: DateTimeOffset.UtcNow.AddMinutes(-1)));

        using var verifier = new CertificateVerifier(root.ExportPublicKey());
        var check = verifier.Verify(signed, null, null, DateTimeOffset.UtcNow);
        Assert.False(check.IsValid);
        Assert.Contains("過期", check.Reason);
    }

    [Fact]
    public void Mode_and_group_outside_grant_fail()
    {
        using var root = CompanyRootKey.Create();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = root.Sign(SampleCert(admin.ExportSubjectPublicKeyInfo()));

        using var verifier = new CertificateVerifier(root.ExportPublicKey());
        Assert.False(verifier.Verify(signed, "assist", "工地", DateTimeOffset.UtcNow).IsValid); // 沒授權 assist
        Assert.False(verifier.Verify(signed, "manage", "辦公室", DateTimeOffset.UtcNow).IsValid); // 沒授權辦公室
    }

    [Fact]
    public void Wildcard_group_allows_any_group()
    {
        using var root = CompanyRootKey.Create();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cert = SampleCert(admin.ExportSubjectPublicKeyInfo()) with { Groups = new[] { "*" } };
        var signed = root.Sign(cert);

        using var verifier = new CertificateVerifier(root.ExportPublicKey());
        Assert.True(verifier.Verify(signed, "manage", "任何群組", DateTimeOffset.UtcNow).IsValid);
    }

    [Fact]
    public void Signed_certificate_survives_json_round_trip()
    {
        using var root = CompanyRootKey.Create();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = root.Sign(SampleCert(admin.ExportSubjectPublicKeyInfo()));

        var restored = SignedAdminCertificate.FromJson(signed.ToJson());
        using var verifier = new CertificateVerifier(root.ExportPublicKey());
        Assert.True(verifier.Verify(restored, "manage", "工地", DateTimeOffset.UtcNow).IsValid);
    }

    [Fact]
    public void Exported_root_private_key_round_trips()
    {
        using var root = CompanyRootKey.Create();
        byte[] priv = root.ExportPrivateKey();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = root.Sign(SampleCert(admin.ExportSubjectPublicKeyInfo()));

        using var reloaded = CompanyRootKey.FromPrivateKey(priv);
        using var verifier = new CertificateVerifier(reloaded.ExportPublicKey());
        Assert.True(verifier.Verify(signed, "manage", "工地", DateTimeOffset.UtcNow).IsValid);
    }
}
