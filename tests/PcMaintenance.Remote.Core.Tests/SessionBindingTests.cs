using System.Security.Cryptography;
using PcMaintenance.Remote.Core.Crypto;

namespace PcMaintenance.Remote.Core.Tests;

public class SessionBindingTests
{
    private static (byte[] priv, byte[] pub) KeyPair() => AdminIdentity.CreateKeyPair();

    [Fact]
    public void Valid_signature_verifies()
    {
        var (priv, pub) = KeyPair();
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(priv, out _);
        byte[] ctrlPub = [1, 2, 3, 4, 5];
        byte[] sig = SessionBinding.Sign(ec, "req-1", ctrlPub);
        Assert.True(SessionBinding.Verify(pub, "req-1", ctrlPub, sig));
    }

    [Fact]
    public void Signature_from_other_key_fails()
    {
        var (priv, _) = KeyPair();
        var (_, otherPub) = KeyPair();
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(priv, out _);
        byte[] ctrlPub = [9, 9, 9];
        byte[] sig = SessionBinding.Sign(ec, "req-1", ctrlPub);
        Assert.False(SessionBinding.Verify(otherPub, "req-1", ctrlPub, sig));
    }

    [Fact]
    public void Tampered_request_id_or_public_key_fails()
    {
        var (priv, pub) = KeyPair();
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(priv, out _);
        byte[] ctrlPub = [1, 2, 3];
        byte[] sig = SessionBinding.Sign(ec, "req-1", ctrlPub);

        Assert.False(SessionBinding.Verify(pub, "req-2", ctrlPub, sig));        // 換 requestId
        Assert.False(SessionBinding.Verify(pub, "req-1", [1, 2, 4], sig));      // 換臨時公鑰
    }

    [Fact]
    public void Fingerprint_is_stable_and_distinct()
    {
        var (_, pubA) = KeyPair();
        var (_, pubB) = KeyPair();
        Assert.Equal(SessionBinding.Fingerprint(pubA), SessionBinding.Fingerprint(pubA));
        Assert.NotEqual(SessionBinding.Fingerprint(pubA), SessionBinding.Fingerprint(pubB));
        Assert.Equal(64, SessionBinding.Fingerprint(pubA).Length); // SHA-256 十六進位
    }
}
