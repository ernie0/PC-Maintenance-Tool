// P1 被控端：在指定 port 監聽，控制端連上後持續送畫面、收滑鼠鍵盤操作。
// 區網直連測試用；按 Ctrl+C 結束。
using System.Net;
using PcMaintenance.Remote.Agent.Session;

int port = ArgInt(args, "--port", 18010);
string bind = ArgStr(args, "--bind", "0.0.0.0"); // 預設接受區網連入；只要本機測試可傳 127.0.0.1

Console.OutputEncoding = System.Text.Encoding.UTF8;

var listener = new AgentListener(
    new IPEndPoint(IPAddress.Parse(bind), port),
    new AgentSessionOptions { Fps = 30, BitrateKbps = 4000 });
listener.VerificationCodeReady += code =>
    Console.WriteLine($"控制端已連上。請核對兩端的驗證碼：{code}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"被控端監聽 {bind}:{port}，等待控制端連入（Ctrl+C 結束）…");
try { await listener.ListenAsync(cts.Token); }
catch (OperationCanceledException) { }
Console.WriteLine("已結束。");

static int ArgInt(string[] a, string k, int d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : d; }
static string ArgStr(string[] a, string k, string d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
