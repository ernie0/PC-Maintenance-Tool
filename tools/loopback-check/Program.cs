// 端對端驗證：同程序內把被控端與控制端用 127.0.0.1 串起來，證實整條管線。
// 存兩張解好的畫面成 PPM（內容是本機自己的桌面，只寫到指定的本機檔案）。
using System.Net;
using System.Net.Sockets;
using PcMaintenance.Remote.Agent.Session;
using PcMaintenance.Remote.Viewer.Net;
using Vortice.MediaFoundation;

int targetFrames = ArgInt(args, "--frames", 60);
string outDir = ArgStr(args, "--outdir", Path.GetTempPath());

Console.OutputEncoding = System.Text.Encoding.UTF8;
MediaFactory.MFStartup(false).CheckError();

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
Console.WriteLine($"被控端監聽 127.0.0.1:{port}");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
string? agentCode = null, viewerCode = null;

var agentTask = Task.Run(async () =>
{
    using TcpClient conn = await listener.AcceptTcpClientAsync(cts.Token);
    conn.NoDelay = true;
    await using var sess = new AgentSession(conn.GetStream(), new AgentSessionOptions { Fps = 30, BitrateKbps = 4000 });
    sess.VerificationCodeReady += c => agentCode = c;
    try { await sess.RunAsync(cts.Token); } catch (OperationCanceledException) { }
});

var viewer = new ViewerClient();
int decoded = 0;
RenderedFrame? first = null, mid = null;
var sw = System.Diagnostics.Stopwatch.StartNew();

viewer.VerificationCodeReady += c => { viewerCode = c; Console.WriteLine($"控制端驗證碼 {c}"); };
viewer.MonitorListReceived += list =>
{
    Console.WriteLine($"收到螢幕清單：{list.Count} 個");
    foreach (var m in list) Console.WriteLine($"  [{m.Index}] {m.Name}");
};
viewer.FrameRendered += f =>
{
    decoded++;
    if (decoded == 1) { first = f; Console.WriteLine($"第一張解碼畫面 {f.Width}x{f.Height}"); }
    if (decoded == targetFrames / 2) mid = f;
    if (decoded >= targetFrames) cts.Cancel();
};

try { await viewer.RunAsync("127.0.0.1", port, cts.Token); }
catch (OperationCanceledException) { }
catch (Exception ex) { Console.WriteLine($"控制端錯誤：{ex.Message}"); }

cts.Cancel();
try { await agentTask; } catch { }
listener.Stop();
viewer.Dispose();

Console.WriteLine($"---");
Console.WriteLine($"交握驗證碼：被控端 {agentCode}、控制端 {viewerCode}、一致={(agentCode == viewerCode && agentCode != null)}");
Console.WriteLine($"解碼畫面共 {decoded} 張，耗時 {sw.Elapsed.TotalSeconds:F1} 秒");

if (first is not null) SavePpm(Path.Combine(outDir, "loopback-first.ppm"), first);
if (mid is not null) SavePpm(Path.Combine(outDir, "loopback-mid.ppm"), mid);
MediaFactory.MFShutdown();

static void SavePpm(string path, RenderedFrame f)
{
    using var fs = new FileStream(path, FileMode.Create);
    byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{f.Width} {f.Height}\n255\n");
    fs.Write(header);
    byte[] rgb = new byte[f.Width * f.Height * 3];
    for (int i = 0, j = 0; i < f.Bgra.Length; i += 4, j += 3)
    {
        rgb[j] = f.Bgra[i + 2];     // R
        rgb[j + 1] = f.Bgra[i + 1]; // G
        rgb[j + 2] = f.Bgra[i];     // B
    }
    fs.Write(rgb);
    Console.WriteLine($"已存 {path}（{f.Width}x{f.Height}）");
}

static int ArgInt(string[] a, string k, int d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : d; }
static string ArgStr(string[] a, string k, string d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
