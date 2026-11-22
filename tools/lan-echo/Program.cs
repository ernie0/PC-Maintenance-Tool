// 跨網路連線測試：和產品走 TCP 時同一套 Core 傳輸（交握＋AES-GCM），但不含影像編解碼。
//   監聽端： lan-echo --listen <port> --frames N --result <檔>
//   連線端： lan-echo --connect <host> <port> --frames N --result <檔>
// 監聽端收到連線後送 N 個加密框（內含遞增序號）；連線端收下、驗章、檢查序號。
// 兩端各把「驗證碼、框數」寫進 result 檔，比對驗證碼一致即證實是同一條端對端連線（非中間人）。
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Core.Transport;

int frames = ArgInt(args, "--frames", 60);
string? result = ArgStr(args, "--result", null);
var lines = new List<string>();
void Log(string s) { Console.WriteLine(s); lines.Add(s); }

try
{
    if (Has(args, "--listen"))
    {
        int port = ArgInt(args, "--listen", 18010);
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Log($"listening port={port}");
        using var client = await listener.AcceptTcpClientAsync();
        client.NoDelay = true;
        using var link = await SecureSession.HandshakeAsync(client.GetStream(), isInitiator: false);
        Log($"role=listener code={link.VerificationCode}");
        for (int i = 0; i < frames; i++)
        {
            byte[] payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, i);
            await link.SendAsync(FrameType.Chat, payload);
            await Task.Delay(15);
        }
        Log($"sent={frames}");
        await Task.Delay(300);
        listener.Stop();
    }
    else if (Has(args, "--connect"))
    {
        int idx = Array.IndexOf(args, "--connect");
        string host = args[idx + 1];
        int port = int.Parse(args[idx + 2]);

        using var tcp = new TcpClient();
        for (int attempt = 0; ; attempt++)
        {
            try { await tcp.ConnectAsync(host, port); break; }
            catch when (attempt < 30) { await Task.Delay(500); }
        }
        tcp.NoDelay = true;
        using var link = await SecureSession.HandshakeAsync(tcp.GetStream(), isInitiator: true);
        Log($"role=connector code={link.VerificationCode}");

        int received = 0, expected = 0; bool ordered = true;
        while (true)
        {
            var msg = await link.ReceiveAsync();
            if (msg is null) break;
            if (msg.Value.Type == FrameType.Chat && msg.Value.Payload.Length >= 4)
            {
                int seq = BinaryPrimitives.ReadInt32BigEndian(msg.Value.Payload);
                if (seq != expected++) ordered = false;
                received++;
            }
        }
        Log($"received={received} ordered={ordered}");
    }
    else
    {
        Log("用法：--listen <port> | --connect <host> <port>");
    }
}
catch (Exception ex)
{
    Log($"error={ex.GetType().Name}:{ex.Message}");
}

if (result is not null)
{
    try { File.WriteAllLines(result, lines); } catch { }
}

static bool Has(string[] a, string k) => Array.IndexOf(a, k) >= 0;
static int ArgInt(string[] a, string k, int d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : d; }
static string? ArgStr(string[] a, string k, string? d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
