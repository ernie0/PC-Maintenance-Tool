using System.Net;
using System.Net.Sockets;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Core.Transport;

namespace PcMaintenance.Remote.Core.Tests;

public class TransportTests
{
    /// <summary>開一對 loopback TCP 連線，最貼近實際網路（不是假串流）。</summary>
    private static async Task<(NetworkStream Initiator, NetworkStream Responder)> LoopbackPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var acceptTask = listener.AcceptTcpClientAsync();
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var server = await acceptTask;
            return (client.GetStream(), server.GetStream());
        }
        finally { listener.Stop(); }
    }

    private static async Task<(SecureSession Initiator, SecureSession Responder)> ConnectedSessionsAsync()
    {
        var (initStream, respStream) = await LoopbackPairAsync();
        var initTask = SecureSession.HandshakeAsync(initStream, isInitiator: true);
        var respTask = SecureSession.HandshakeAsync(respStream, isInitiator: false);
        await Task.WhenAll(initTask, respTask);
        return (await initTask, await respTask);
    }

    [Fact]
    public async Task Handshake_yields_matching_verification_code()
    {
        var (init, resp) = await ConnectedSessionsAsync();
        using (init)
        using (resp)
        {
            Assert.Equal(6, init.VerificationCode.Length);
            Assert.Equal(init.VerificationCode, resp.VerificationCode);
        }
    }

    [Fact]
    public async Task Frame_sent_by_responder_is_received_and_decrypted_by_initiator()
    {
        var (init, resp) = await ConnectedSessionsAsync();
        using (init)
        using (resp)
        {
            byte[] h264 = [0, 0, 0, 1, 0x65, 0xAA, 0xBB];
            byte[] payload = VideoPayload.Encode(1234, keyframe: true, h264);
            await resp.SendAsync(FrameType.Video, payload);

            var received = await init.ReceiveAsync();
            Assert.NotNull(received);
            Assert.Equal(FrameType.Video, received!.Value.Type);
            Assert.True(VideoPayload.TryDecode(received.Value.Payload, out long ts, out bool key, out var data));
            Assert.Equal(1234, ts);
            Assert.True(key);
            Assert.Equal(h264, data.ToArray());
        }
    }

    [Fact]
    public async Task Input_event_round_trips_from_initiator_to_responder()
    {
        var (init, resp) = await ConnectedSessionsAsync();
        using (init)
        using (resp)
        {
            var ev = InputEvent.Move(32000, 16000);
            await init.SendAsync(FrameType.Input, ev.Encode());

            var received = await resp.ReceiveAsync();
            Assert.NotNull(received);
            Assert.Equal(FrameType.Input, received!.Value.Type);
            Assert.True(InputEvent.TryDecode(received.Value.Payload, out var back));
            Assert.Equal(ev, back);
        }
    }

    [Fact]
    public async Task Monitor_list_and_switch_round_trip()
    {
        var (init, resp) = await ConnectedSessionsAsync();
        using (init)
        using (resp)
        {
            IReadOnlyList<MonitorDescriptor> monitors =
            [
                new() { Index = 0, Width = 1920, Height = 1080, IsPrimary = true, Name = "主螢幕" },
                new() { Index = 1, Width = 2560, Height = 1440, IsPrimary = false, Name = "副螢幕" },
            ];
            await resp.SendAsync(FrameType.MonitorList, MonitorProtocol.EncodeList(monitors));
            var listFrame = await init.ReceiveAsync();
            var back = MonitorProtocol.DecodeList(listFrame!.Value.Payload);
            Assert.Equal(2, back.Count);
            Assert.Equal("副螢幕", back[1].Name);
            Assert.True(back[0].IsPrimary);

            await init.SendAsync(FrameType.MonitorSwitch, MonitorProtocol.EncodeSwitch(1));
            var switchFrame = await resp.ReceiveAsync();
            Assert.Equal(FrameType.MonitorSwitch, switchFrame!.Value.Type);
            Assert.True(MonitorProtocol.TryDecodeSwitch(switchFrame.Value.Payload, out int idx));
            Assert.Equal(1, idx);
        }
    }

    [Fact]
    public async Task Receive_returns_null_when_peer_closes()
    {
        var (initStream, respStream) = await LoopbackPairAsync();
        var initTask = SecureSession.HandshakeAsync(initStream, true);
        var respTask = SecureSession.HandshakeAsync(respStream, false);
        await Task.WhenAll(initTask, respTask);
        using var init = await initTask;
        using var resp = await respTask;

        respStream.Socket.Shutdown(SocketShutdown.Both); // 被控端乾淨關閉
        var got = await init.ReceiveAsync();
        Assert.Null(got);
    }

    [Fact]
    public async Task Tampered_frame_is_rejected()
    {
        // 直接驗證框被改一個 byte 會被 SecureChannel 擋下（透過自訂串流攔截）。
        var (initStream, respStream) = await LoopbackPairAsync();
        var initTask = SecureSession.HandshakeAsync(initStream, true);
        var respTask = SecureSession.HandshakeAsync(respStream, false);
        await Task.WhenAll(initTask, respTask);
        using var init = await initTask;
        using var resp = await respTask;

        // 攔截：手動送一個被竄改的框到 init（繞過 SendAsync，模擬線上被改）。
        var conn = new FramedConnection(respStream);
        // 先讓 resp 封一個正常框，取得其位元組
        var field = typeof(SecureSession).GetField("_channel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var channel = (PcMaintenance.Remote.Core.Crypto.SecureChannel)field.GetValue(resp)!;
        byte[] frame = channel.Seal((byte)FrameType.Input, InputEvent.Move(1, 2).Encode());
        frame[^1] ^= 0xFF; // 改掉 tag 最後一個 byte
        await conn.WriteFrameAsync(frame);

        await Assert.ThrowsAsync<PcMaintenance.Remote.Core.Crypto.SecureChannelException>(() => init.ReceiveAsync());
    }
}
