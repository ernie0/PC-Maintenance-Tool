// 端對端驗證（經伺服器）：同程序起轉送伺服器、被控端、控制端，全部走 WebSocket 轉送。
// 證實：被控端註冊→控制端要求→伺服器路由→被控端同意→端對端加密轉送→解碼顯示。
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcMaintenance.Remote.Agent.Session;
using PcMaintenance.Remote.Server;
using PcMaintenance.Remote.Viewer.Net;
using Vortice.MediaFoundation;

int targetFrames = ArgInt(args, "--frames", 45);
string outDir = ArgStr(args, "--outdir", Path.GetTempPath());

Console.OutputEncoding = System.Text.Encoding.UTF8;
MediaFactory.MFStartup(false).CheckError();

// 起轉送伺服器（127.0.0.1 隨機 port）
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var web = builder.Build();
string auditPath = Path.Combine(outDir, "server-audit.jsonl");
if (File.Exists(auditPath)) File.Delete(auditPath);
var audit = new AuditLog(auditPath);
var hub = new RelayHub(audit);
web.MapRemoteServer(hub);
await web.StartAsync();
var baseUrl = new Uri(web.Urls.First());
Console.WriteLine($"轉送伺服器 {baseUrl}");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
const string agentId = "demo-agent-1";
string? agentCode = null, viewerCode = null;

// 被控端連伺服器登記
var agent = new ServerAgent(baseUrl,
    new AgentIdentity { AgentId = agentId, ComputerName = "示範被控端", Groups = ["示範"] },
    new AgentSessionOptions { Fps = 30, BitrateKbps = 4000 });
agent.VerificationCodeReady += c => agentCode = c;
var agentTask = Task.Run(() => agent.RunAsync(cts.Token));

// 等被控端上線
for (int i = 0; i < 60 && !cts.IsCancellationRequested; i++)
{
    var list = await ViewerClient.ListAgentsAsync(baseUrl, cts.Token);
    if (list.Any(a => a.AgentId == agentId)) { Console.WriteLine($"線上被控端 {list.Count} 台，找到 {agentId}（{list[0].ComputerName}）"); break; }
    await Task.Delay(100, cts.Token);
}

// 控制端經伺服器連上
var viewer = new ViewerClient();
int decoded = 0;
RenderedFrame? first = null;
viewer.VerificationCodeReady += c => { viewerCode = c; Console.WriteLine($"控制端驗證碼 {c}"); };
viewer.MonitorListReceived += l => Console.WriteLine($"收到螢幕清單：{l.Count} 個");
viewer.FrameRendered += f =>
{
    decoded++;
    if (decoded == 1) { first = f; Console.WriteLine($"第一張解碼畫面 {f.Width}x{f.Height}"); }
    if (decoded >= targetFrames) cts.Cancel();
};

try { await viewer.RunViaServerAsync(baseUrl, agentId, "示範管理者", ct: cts.Token); }
catch (OperationCanceledException) { }
catch (Exception ex) { Console.WriteLine($"控制端錯誤：{ex.Message}"); }

cts.Cancel();
try { await agentTask; } catch { }
await web.StopAsync();

Console.WriteLine("---");
Console.WriteLine($"交握驗證碼：被控端 {agentCode}、控制端 {viewerCode}、一致={(agentCode == viewerCode && agentCode != null)}");
Console.WriteLine($"經伺服器轉送解碼共 {decoded} 張");
audit.Dispose(); // 放開檔案鎖再讀
int auditLines = File.Exists(auditPath) ? File.ReadAllLines(auditPath).Length : 0;
Console.WriteLine($"稽核紀錄 {auditLines} 筆：{auditPath}");

if (first is not null) SavePpm(Path.Combine(outDir, "server-loopback-first.ppm"), first);
MediaFactory.MFShutdown();

static void SavePpm(string path, RenderedFrame f)
{
    using var fs = new FileStream(path, FileMode.Create);
    fs.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{f.Width} {f.Height}\n255\n"));
    byte[] rgb = new byte[f.Width * f.Height * 3];
    for (int i = 0, j = 0; i < f.Bgra.Length; i += 4, j += 3)
    { rgb[j] = f.Bgra[i + 2]; rgb[j + 1] = f.Bgra[i + 1]; rgb[j + 2] = f.Bgra[i]; }
    fs.Write(rgb);
    Console.WriteLine($"已存 {path}（{f.Width}x{f.Height}）");
}

static int ArgInt(string[] a, string k, int d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : d; }
static string ArgStr(string[] a, string k, string d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
