// 錄製桌面幾秒成 H.264，驗證被控端的擷取＋編碼管線。畫面只寫到指定的本機檔案。
using System.Diagnostics;
using PcMaintenance.Remote.Agent.Capture;
using PcMaintenance.Remote.Agent.Encoding;

int seconds = ArgInt(args, "--seconds", 4);
int monitorIndex = ArgInt(args, "--monitor", 0);
int fps = ArgInt(args, "--fps", 30);
int bitrateKbps = ArgInt(args, "--kbps", 4000);
string output = ArgStr(args, "--out", Path.Combine(Path.GetTempPath(), $"pcremote-capture-{DateTime.Now:HHmmss}.h264"));

Console.OutputEncoding = System.Text.Encoding.UTF8;
Vortice.MediaFoundation.MediaFactory.MFStartup(false).CheckError();

Console.WriteLine("螢幕：");
foreach (var m in DesktopDuplicator.ListMonitors())
    Console.WriteLine($"  [{m.Index}] {m.Width}x{m.Height} {(m.IsPrimary ? "（主）" : "")} {m.AdapterName}");

using var capturer = new DesktopDuplicator(monitorIndex);
Console.WriteLine($"擷取螢幕 [{capturer.Monitor.Index}] {capturer.Width}x{capturer.Height}，{fps} fps，目標 {bitrateKbps} kbps，{seconds} 秒");

byte[] nv12 = new byte[capturer.Nv12Size];
long totalBytes = 0;
int encoded = 0, keyframes = 0;
using var file = new FileStream(output, FileMode.Create, FileAccess.Write);
var fileLock = new object();

using var encoder = new H264Encoder(capturer.Width, capturer.Height, fps, bitrateKbps);
encoder.FrameEncoded += f =>
{
    lock (fileLock) file.Write(f.Data);
    Interlocked.Add(ref totalBytes, f.Data.Length);
    Interlocked.Increment(ref encoded);
    if (f.IsKeyframe) Interlocked.Increment(ref keyframes);
};
encoder.Start();
Console.WriteLine($"編碼器：{encoder.EncoderName}{(encoder.IsHardware ? "（硬體）" : "（軟體）")}");

int captured = 0, changed = 0;
var sw = Stopwatch.StartNew();
long frameTicks = TimeSpan.TicksPerSecond / fps;
int target = seconds * fps;
for (int i = 0; i < target; i++)
{
    double due = i * 1000.0 / fps;
    while (sw.Elapsed.TotalMilliseconds < due) Thread.SpinWait(80);

    if (capturer.TryGetNv12(5, nv12, out bool updated))
    {
        captured++;
        if (updated) changed++;
        encoder.Submit(nv12, i * frameTicks); // 不論是否更新都送，維持固定幀率的錄影
    }
    if ((i + 1) % fps == 0)
        Console.WriteLine($"  第 {(i + 1) / fps} 秒：擷取 {captured}（有變 {changed}），已編碼 {encoded}");
}
Console.WriteLine("擷取迴圈結束，排空編碼器…");
encoder.Stop();
file.Flush();

double wall = sw.Elapsed.TotalSeconds;
Console.WriteLine($"擷取 {captured} 次（畫面有變 {changed}），編碼輸出 {encoded} 片（關鍵影格 {keyframes}）");
Console.WriteLine($"檔案 {output}（{totalBytes / 1024.0:F0} KB，約 {totalBytes * 8 / wall / 1_000_000:F2} Mbps）");

static int ArgInt(string[] a, string k, int d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : d; }
static string ArgStr(string[] a, string k, string d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
