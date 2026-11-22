using System.Security.Cryptography;
using PcMaintenance.Remote.Core.Crypto;
using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Core.Tests;

public class ControlProtocolTests
{
    [Fact]
    public void Register_round_trips_with_type_tag()
    {
        var msg = new RegisterMessage
        {
            ComputerName = "工地-大門-PC",
            Groups = new[] { "工地", "大林" },
            AgentVersion = "1.0.0",
            AgentId = "abc123",
        };
        var json = ControlProtocol.Serialize(msg);
        Assert.Contains("\"t\":\"register\"", json);

        var back = Assert.IsType<RegisterMessage>(ControlProtocol.Deserialize(json));
        Assert.Equal(msg.ComputerName, back.ComputerName);
        Assert.Equal(msg.Groups, back.Groups);
        Assert.Equal(msg.AgentVersion, back.AgentVersion);
        Assert.Equal(msg.AgentId, back.AgentId);
    }

    [Fact]
    public void Session_request_carries_hidden_flag_and_optional_certificate()
    {
        using var root = CompanyRootKey.Create();
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cert = root.Sign(new AdminCertificate
        {
            AdminName = "王工程師",
            AdminPublicKey = admin.ExportSubjectPublicKeyInfo(),
            Modes = new[] { SessionMode.Manage },
            Groups = new[] { "*" },
            NotBefore = DateTimeOffset.UtcNow,
            NotAfter = DateTimeOffset.UtcNow.AddYears(1),
        });

        var msg = new SessionRequestMessage
        {
            RequestId = "r1",
            Mode = SessionMode.Manage,
            AdminName = "王工程師",
            TargetAgentId = "abc123",
            ControllerPublicKey = new byte[] { 1, 2, 3, 4 },
            Certificate = cert,
            RequestHidden = true,
        };

        var back = Assert.IsType<SessionRequestMessage>(
            ControlProtocol.Deserialize(ControlProtocol.Serialize(msg)));
        Assert.True(back.RequestHidden);
        Assert.Equal(SessionMode.Manage, back.Mode);
        Assert.NotNull(back.Certificate);
        Assert.Equal(cert.Signature, back.Certificate!.Signature);
    }

    [Fact]
    public void Consent_round_trips_pairing_and_hidden()
    {
        var msg = new SessionConsentMessage
        {
            RequestId = "r1",
            AgentPublicKey = new byte[] { 9, 8, 7 },
            Hidden = true,
            PairingEstablished = true,
        };
        var back = Assert.IsType<SessionConsentMessage>(
            ControlProtocol.Deserialize(ControlProtocol.Serialize(msg)));
        Assert.True(back.Hidden);
        Assert.True(back.PairingEstablished);
    }

    [Fact]
    public void Unknown_message_type_throws_so_caller_can_skip()
    {
        Assert.Throws<UnknownControlMessageException>(() =>
            ControlProtocol.Deserialize("{\"t\":\"from-a-newer-version\",\"x\":1}"));
    }

    [Fact]
    public void Malformed_json_throws()
    {
        Assert.Throws<UnknownControlMessageException>(() => ControlProtocol.Deserialize("not json"));
    }

    [Theory]
    [InlineData(SessionMode.View, true)]
    [InlineData(SessionMode.Wall, true)]
    [InlineData(SessionMode.Assist, false)]
    [InlineData(SessionMode.Manage, false)]
    public void Read_only_modes_are_classified(string mode, bool readOnly)
    {
        Assert.Equal(readOnly, SessionMode.IsReadOnly(mode));
        Assert.True(SessionMode.IsKnown(mode));
    }
}

public class InputEventTests
{
    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { InputEvent.Move(0, 0) },
        new object[] { InputEvent.Move(65535, 32768) },
        new object[] { InputEvent.MouseDown(MouseButton.Left) },
        new object[] { InputEvent.MouseUp(MouseButton.X2) },
        new object[] { InputEvent.Wheel(-120) },
        new object[] { InputEvent.Wheel(120, horizontal: true) },
        new object[] { InputEvent.KeyDown(0x1E) },
        new object[] { InputEvent.KeyUp(0x9D, extended: true) },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Encode_then_decode_restores_the_event(InputEvent ev)
    {
        Assert.True(InputEvent.TryDecode(ev.Encode(), out var back));
        Assert.Equal(ev, back);
    }

    [Fact]
    public void Decoding_too_few_bytes_fails()
    {
        Assert.False(InputEvent.TryDecode(new byte[] { 1, 2 }, out _));
    }

    [Fact]
    public void Decoding_unknown_kind_fails()
    {
        Assert.False(InputEvent.TryDecode(new byte[] { 99, 0, 0, 0, 0, 0, 0, 0 }, out _));
    }

    [Fact]
    public void Invalid_mouse_button_fails()
    {
        byte[] bad = InputEvent.MouseDown(MouseButton.Left).Encode();
        bad[1] = 0; // 不合法的按鍵編號
        Assert.False(InputEvent.TryDecode(bad, out _));
    }
}
