// 配對與隱藏模式驗證：同程序起伺服器＋被控端（配對策略）＋控制端。
// 1) 第一次要求隱藏但未配對 → 跳提示請人同意、完成配對（非隱藏）。
// 2) 第二次要求隱藏、已配對 → 靜默同意、隱藏進行、完全不跳提示。
// 3) 偽造憑證（別的根簽的）要求隱藏 → 拿不到隱藏，仍需人同意。
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using PcMaintenance.Remote.Agent.Session;
using PcMaintenance.Remote.Core.Crypto;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Server;
using PcMaintenance.Remote.Viewer.Net;
using Vortice.MediaFoundation;

string outDir = Args(args, "--outdir", Path.GetTempPath());
Console.OutputEncoding = System.Text.Encoding.UTF8;
MediaFactory.MFStartup(false).CheckError();

var now = DateTimeOffset.UtcNow;

// 公司根金鑰＋簽給控制端的管理者憑證（真憑證）
using var root = CompanyRootKey.Create();
var (adminPriv, adminPub) = AdminIdentity.CreateKeyPair();
var signed = root.Sign(new AdminCertificate
{
    AdminName = "示範管理者",
    AdminPublicKey = adminPub,
    Modes = [SessionMode.Assist, SessionMode.Manage],
    Groups = ["示範"],
    NotBefore = now.AddHours(-1),
    NotAfter = now.AddYears(1),
});
using var identity = AdminIdentity.FromPrivateKey(adminPriv, signed);

// 偽造憑證：另一把「流氓根」簽的，被控端只信真的公司根
using var rogueRoot = CompanyRootKey.Create();
var (forgePriv, forgePub) = AdminIdentity.CreateKeyPair();
var forgedSigned = rogueRoot.Sign(new AdminCertificate
{
    AdminName = "冒充者",
    AdminPublicKey = forgePub,
    Modes = [SessionMode.Manage],
    Groups = ["示範"],
    NotBefore = now.AddHours(-1),
    NotAfter = now.AddYears(1),
});
using var forged = AdminIdentity.FromPrivateKey(forgePriv, forgedSigned);

// 伺服器
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var web = builder.Build();
var audit = new AuditLog(Path.Combine(outDir, "pairing-audit.jsonl"));
web.MapRemoteServer(new RelayHub(audit));
await web.StartAsync();
var baseUrl = new Uri(web.Urls.First());

// 被控端：配對策略（只信真公司根），提示回呼計數並自動同意
string pairingPath = Path.Combine(outDir, "pairing.json");
if (File.Exists(pairingPath)) File.Delete(pairingPath);
var store = new PairingStore(pairingPath);
using var verifier = new CertificateVerifier(root.ExportPublicKey());
var policy = new PairingConsentPolicy(verifier, store, ["示範"]);
int promptCount = 0;
policy.VisiblePrompt = _ => { Interlocked.Increment(ref promptCount); return true; };

var agent = new ServerAgent(baseUrl,
    new AgentIdentity { AgentId = "demo-agent-1", ComputerName = "示範被控端", Groups = ["示範"] },
    new AgentSessionOptions { Fps = 30, BitrateKbps = 4000 }) { ConsentPolicy = policy.Decide };
using var agentCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var agentTask = Task.Run(() => agent.RunAsync(agentCts.Token));

for (int i = 0; i < 60; i++)
{
    if ((await ViewerClient.ListAgentsAsync(baseUrl, agentCts.Token)).Any(a => a.AgentId == "demo-agent-1")) break;
    await Task.Delay(100, agentCts.Token);
}

// 第 1 次：要求隱藏，但還沒配對 → 應跳提示、配對、非隱藏
int b1 = promptCount;
var (hidden1, paired1, frames1) = await ConnectAsync(baseUrl, identity, requestHidden: true);
int prompt1 = promptCount - b1;
Console.WriteLine($"第1次（未配對求隱藏）：本次提示={prompt1}、隱藏={hidden1}、完成配對={paired1}、解碼 {frames1} 張");

await Task.Delay(400);

// 第 2 次：要求隱藏，已配對 → 應靜默、不跳提示
int b2 = promptCount;
var (hidden2, _, frames2) = await ConnectAsync(baseUrl, identity, requestHidden: true);
int prompt2 = promptCount - b2;
Console.WriteLine($"第2次（已配對求隱藏）：本次提示={prompt2}、隱藏={hidden2}、解碼 {frames2} 張");

await Task.Delay(400);

// 第 3 次：偽造憑證求隱藏 → 拿不到隱藏、仍需人同意
int b3 = promptCount;
var (hidden3, _, frames3) = await ConnectAsync(baseUrl, forged, requestHidden: true);
int prompt3 = promptCount - b3;
Console.WriteLine($"第3次（偽造憑證求隱藏）：本次提示={prompt3}、隱藏={hidden3}、解碼 {frames3} 張");

agentCts.Cancel();
try { await agentTask; } catch { }
await web.StopAsync();
audit.Dispose();

bool pass =
    prompt1 == 1 && !hidden1 && paired1 && frames1 > 0 &&   // 第1次：跳一次提示、配對、非隱藏、有畫面
    prompt2 == 0 && hidden2 && frames2 > 0 &&               // 第2次：零提示、靜默隱藏、有畫面
    prompt3 == 1 && !hidden3 && frames3 > 0;                // 第3次：偽造憑證仍需提示、拿不到隱藏
Console.WriteLine("---");
Console.WriteLine($"配對＋隱藏模式驗證：{(pass ? "通過" : "不通過")}");
MediaFactory.MFShutdown();

async Task<(bool Hidden, bool Paired, int Frames)> ConnectAsync(Uri server, AdminIdentity id, bool requestHidden)
{
    using var c = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    var v = new ViewerClient();
    bool hidden = false, paired = false; int frames = 0;
    v.SessionGranted += (h, p) => { hidden = h; paired = p; };
    v.FrameRendered += _ => { if (Interlocked.Increment(ref frames) >= 20) c.Cancel(); };
    try { await v.RunViaServerAsync(server, "demo-agent-1", id.Certificate.Certificate.AdminName, SessionMode.Manage, requestHidden, id, c.Token); }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Console.WriteLine($"  連線結束：{ex.Message}"); }
    v.Dispose();
    return (hidden, paired, frames);
}

static string Args(string[] a, string k, string d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
