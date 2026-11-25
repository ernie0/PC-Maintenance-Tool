// P0 技術驗證：桌面擷取 → BGRA 轉 NV12 → H.264 編碼（Media Foundation）→ 解碼。
// 量每一段的延遲、位元率、畫質（PSNR）與 CPU。畫面只留在記憶體、不存檔，輸出只有數字。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

internal static class G
{
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-98a6aa2a2f6e");
    public static readonly Guid Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid CatVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid CatVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    public const uint EnumSync = 0x1, EnumHardware = 0x4, EnumSortAndFilter = 0x40;
    public const int NeedMoreInput = unchecked((int)0xC00D6D72);
    public const int StreamChange = unchecked((int)0xC00D6D61);
    public const int WaitTimeout = unchecked((int)0x887A0027);
    public const int AccessLost = unchecked((int)0x887A0026);
    public static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;
}

internal sealed record Stage(string Name, double Avg, double P95, double Max);
internal sealed record RunResult(string Scenario, string Encoder, bool Hardware, int Frames, int NewDesktopFrames,
    Stage Capture, Stage Convert, Stage Encode, Stage Decode, Stage Total,
    double Mbps, double PsnrY, double CpuOneCorePct, double CpuMachinePct, int TimestampMismatches, string D3D, string? Error);

internal static unsafe class Program
{
    const int Fps = 30;
    static readonly int Seconds = int.TryParse(Environment.GetEnvironmentVariable("P0_SECONDS"), out var sec) ? sec : 10;
    const uint Bitrate = 4_000_000;
    static int Total => Fps * Seconds;

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        MediaFactory.MFStartup(false).CheckError();
        using var src = new FrameSource();
        Console.WriteLine($"擷取：{src.Width}x{src.Height}（顯示卡：{src.AdapterName}），每段 {Seconds} 秒、{Fps} fps、目標 {Bitrate / 1_000_000} Mbps");

        var names = ListEncoders().Select(e => $"{e.Name}{(e.Hw ? "（硬體）" : "（軟體）")}").ToList();
        Console.WriteLine("可用編碼器：" + string.Join("、", names));

        var results = new List<RunResult>();
        foreach (var scroll in new[] { false, true })
        {
            src.Scroll = scroll;
            var scenario = scroll ? "整頁捲動" : "一般操作";
            int count = ListEncoders().Count;
            var only = Environment.GetEnvironmentVariable("P0_ONLY");
            for (int i = 0; i < count; i++)
            {
                if (!string.IsNullOrEmpty(only) && !names[i].Contains(only)) continue;
                Console.WriteLine($"開始 [{scenario}] {names[i]} …");
                var r = RunOne(scenario, i, src);
                results.Add(r);
                Print(r);
            }
        }

        var outDir = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(outDir);
        var file = Path.Combine(outDir, $"p0-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { src.Width, src.Height, src.AdapterName, Fps, Seconds, Bitrate, results },
            new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine("結果檔：" + file);
        return 0;
    }

    static List<(string Name, bool Hw, IMFActivate Act)> ListEncoders()
    {
        var input = new RegisterTypeInfo { GuidMajorType = G.Video, GuidSubtype = G.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = G.Video, GuidSubtype = G.H264 };
        var list = new List<(string, bool, IMFActivate)>();
        foreach (var (flags, hw) in new[] { (G.EnumHardware | G.EnumSortAndFilter, true), (G.EnumSync | G.EnumSortAndFilter, false) })
        {
            var col = MediaFactory.MFTEnumEx(G.CatVideoEncoder, flags, input, output);
            foreach (var a in col)
            {
                string name;
                try { name = a.GetString(G.FriendlyName); } catch { name = "未命名"; }
                list.Add((name, hw, a));
            }
        }
        return list;
    }

    static RunResult RunOne(string scenario, int index, FrameSource src)
    {
        var (name, hw, act) = ListEncoders()[index];
        var cap = new List<double>(); var conv = new List<double>(); var enc = new List<double>();
        var dec = new List<double>(); var total = new List<double>();
        var psnr = new List<double>();
        long bytes = 0; int mismatches = 0; int newFrames0 = src.NewFrames;
        var pending = new Queue<(long Ts, double CapStart, double EncStart)>();
        IMFTransform? encoder = null; IMFTransform? decoder = null;
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        long frameTicks = TimeSpan.TicksPerSecond / Fps;
        int sent = 0, got = 0;
        var nv12Copies = new Dictionary<long, byte[]>();
        var otherEvents = new List<string>();
        string d3dNote = "";

        double Now() => sw.Elapsed.TotalMilliseconds;

        IMFSample Feed()
        {
            WaitUntil(sw, sent * 1000.0 / Fps);
            double t0 = Now();
            src.Produce(sent);
            cap.Add(src.LastCaptureMs); conv.Add(src.LastConvertMs);
            long ts = sent * frameTicks;
            if (sent % Fps == 5) nv12Copies[ts] = src.CopyLuma();
            var sample = MakeSample(src.Nv12, src.Nv12Length, ts, frameTicks);
            pending.Enqueue((ts, t0, Now()));
            sent++;
            return sample;
        }

        void Consume(IMFSample s)
        {
            double tEnc = Now();
            var (ts, capStart, encStart) = pending.Dequeue();
            if (s.SampleTime != ts) mismatches++;
            using (var b = s.ConvertToContiguousBuffer()) bytes += b.CurrentLength;
            enc.Add(tEnc - encStart);
            decoder!.ProcessInput(0, s, 0);
            s.Dispose();
            while (true)
            {
                var o = Pull(decoder, out bool change);
                if (change) { SetNv12Output(decoder); continue; }
                if (o is null) break;
                double tDec = Now();
                dec.Add(tDec - tEnc);
                total.Add(tDec - capStart);
                if (nv12Copies.Remove(o.SampleTime, out var reference) || nv12Copies.Remove(ts, out reference))
                    psnr.Add(PsnrY(reference, o, src.Width, src.Height));
                o.Dispose();
                got++;
            }
        }

        try
        {
            encoder = CreateEncoder(act, hw, src.Width, src.Height, out d3dNote);
            decoder = CreateDecoder(src.Width, src.Height);
            if (hw)
            {
                var events = encoder.QueryInterface<IMFMediaEventGenerator>();
                var queue = new System.Collections.Concurrent.BlockingCollection<IMFMediaEvent>();
                var pump = new Thread(() =>
                {
                    try
                    {
                        while (true)
                        {
                            var ev = events.GetEvent(0);
                            int t = (int)ev.EventType;
                            queue.Add(ev);
                            if (t == 603) break;
                        }
                    }
                    catch { }
                    finally { queue.CompleteAdding(); }
                }) { IsBackground = true };
                pump.Start();
                bool drainSent = false;
                while (true)
                {
                    if (!queue.TryTake(out var taken, 5000))
                        throw new TimeoutException($"5 秒沒有編碼器事件（已送 {sent}、已解碼 {got}、等待輸出 {pending.Count}、已送 drain {drainSent}；其他事件：{string.Join(",", otherEvents)}）");
                    using var e = taken;
                    int type = (int)e.EventType;
                    if (type is not (601 or 602 or 603)) otherEvents.Add($"{type}/0x{e.Status.Code:X8}");
                    if (type == 601) // METransformNeedInput
                    {
                        if (sent < Total)
                        {
                            using var sample = Feed();
                            encoder.ProcessInput(0, sample, 0);
                        }
                        if (sent >= Total && !drainSent) { encoder.ProcessMessage((TMessageType)1, 0); drainSent = true; }
                    }
                    else if (type == 602) // METransformHaveOutput
                    {
                        var s = Pull(encoder, out _);
                        if (s is not null) Consume(s);
                    }
                    else if (type == 603) break; // METransformDrainComplete
                }
            }
            else
            {
                while (sent < Total)
                {
                    using (var sample = Feed()) encoder.ProcessInput(0, sample, 0);
                    while (Pull(encoder, out _) is { } s) Consume(s);
                }
                encoder.ProcessMessage((TMessageType)1, 0);
                while (Pull(encoder, out _) is { } s) Consume(s);
            }

            double wall = sw.Elapsed.TotalMilliseconds;
            double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            return new RunResult(scenario, name, hw, got, src.NewFrames - newFrames0,
                S("擷取", cap), S("轉換", conv), S("編碼", enc), S("解碼", dec), S("合計", total),
                bytes * 8.0 / (Total / (double)Fps) / 1_000_000,
                psnr.Count > 0 ? psnr.Average() : double.NaN,
                cpuMs / wall * 100, cpuMs / wall * 100 / Environment.ProcessorCount, mismatches, d3dNote, null);
        }
        catch (Exception ex)
        {
            return new RunResult(scenario, name, hw, got, 0, S("擷取", cap), S("轉換", conv), S("編碼", enc), S("解碼", dec), S("合計", total),
                0, double.NaN, 0, 0, mismatches, d3dNote, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { encoder?.ProcessMessage((TMessageType)0x10000001, 0); } catch { } // end streaming
            encoder?.Dispose();
            try { act.ShutdownObject(); } catch { }
            decoder?.Dispose();
        }
    }

    static readonly List<object> KeepAlive = new();

    // 硬體編碼器指定它所屬顯示卡的 D3D11 裝置（MFT_ENUM_ADAPTER_LUID → 同一張卡建立裝置 → MFT_MESSAGE_SET_D3D_MANAGER）。
    static string AttachD3DManager(IMFActivate act, IMFTransform enc)
    {
        ulong luid = 0;
        try { luid = act.GetUInt64(new Guid("1d39518c-e220-4da8-a07f-ba172552d6b1")); } catch { }
        string friendly = "";
        try { friendly = act.GetString(G.FriendlyName); } catch { }
        uint vendor = friendly.Contains("Intel") ? 0x8086u : friendly.Contains("NVIDIA") ? 0x10DEu : friendly.Contains("AMD") ? 0x1002u : 0;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            var d = adapter.Description1;
            ulong id = ((ulong)(uint)d.Luid.HighPart << 32) | d.Luid.LowPart;
            bool match = luid != 0 ? id == luid : vendor != 0 && d.VendorId == vendor;
            if (!match) { adapter.Dispose(); continue; }
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
            using (var mt = dev!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
            var manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(dev).CheckError();
            enc.ProcessMessage((TMessageType)2, (nuint)manager.NativePointer); // MFT_MESSAGE_SET_D3D_MANAGER
            KeepAlive.Add(manager); KeepAlive.Add(dev); KeepAlive.Add(ctx!);
            adapter.Dispose();
            return "D3D：" + d.Description;
        }
        return "找不到對應顯示卡，未指定 D3D";
    }

    static IMFTransform CreateEncoder(IMFActivate act, bool hw, int w, int h, out string d3dNote)
    {
        var enc = act.ActivateObject<IMFTransform>();
        d3dNote = "";
        if (hw)
        {
            enc.Attributes.Set(G.AsyncUnlock, 1u).CheckError();
            d3dNote = AttachD3DManager(act, enc);
        }
        if (Environment.GetEnvironmentVariable("P0_NOLOWLAT") != "1") { try { enc.Attributes.Set(G.LowLatency, 1u); } catch { } }

        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(G.MajorType, G.Video);
        outType.Set(G.Subtype, G.H264);
        outType.Set(G.AvgBitrate, Bitrate);
        outType.Set(G.FrameSize, G.Pack(w, h));
        outType.Set(G.FrameRate, G.Pack(Fps, 1));
        outType.Set(G.PixelAspect, G.Pack(1, 1));
        outType.Set(G.Interlace, 2u);
        outType.Set(G.Profile, 77u);
        enc.SetOutputType(0, outType, 0);

        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(G.MajorType, G.Video);
        inType.Set(G.Subtype, G.NV12);
        inType.Set(G.FrameSize, G.Pack(w, h));
        inType.Set(G.FrameRate, G.Pack(Fps, 1));
        inType.Set(G.PixelAspect, G.Pack(1, 1));
        inType.Set(G.Interlace, 2u);
        enc.SetInputType(0, inType, 0);

        enc.ProcessMessage((TMessageType)0x10000000, 0); // begin streaming
        enc.ProcessMessage((TMessageType)0x10000003, 0); // start of stream
        return enc;
    }

    static IMFTransform CreateDecoder(int w, int h)
    {
        var input = new RegisterTypeInfo { GuidMajorType = G.Video, GuidSubtype = G.H264 };
        var col = MediaFactory.MFTEnumEx(G.CatVideoDecoder, G.EnumSync | G.EnumSortAndFilter, input, null);
        var dec = col.First().ActivateObject<IMFTransform>();
        try { dec.Attributes.Set(G.LowLatency, 1u); } catch { }
        var t = MediaFactory.MFCreateMediaType();
        t.Set(G.MajorType, G.Video);
        t.Set(G.Subtype, G.H264);
        t.Set(G.FrameSize, G.Pack(w, h));
        t.Set(G.FrameRate, G.Pack(Fps, 1));
        dec.SetInputType(0, t, 0);
        SetNv12Output(dec);
        dec.ProcessMessage((TMessageType)0x10000000, 0);
        dec.ProcessMessage((TMessageType)0x10000003, 0);
        return dec;
    }

    static void SetNv12Output(IMFTransform t)
    {
        for (int i = 0; ; i++)
        {
            var ot = t.GetOutputAvailableType(0, i);
            if (ot.GetGUID(G.Subtype) == G.NV12) { t.SetOutputType(0, ot, 0); return; }
        }
    }

    static IMFSample? Pull(IMFTransform t, out bool streamChange)
    {
        streamChange = false;
        var info = t.GetOutputStreamInfo(0);
        bool provides = (info.Flags & 0x300) != 0; // PROVIDES_SAMPLES | CAN_PROVIDE_SAMPLES
        var buf = new OutputDataBuffer { StreamID = 0 };
        if (!provides)
        {
            var s = MediaFactory.MFCreateSample();
            s.AddBuffer(MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 1)));
            buf.Sample = s;
        }
        var r = t.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
        buf.Events?.Dispose();
        if (r.Code == G.NeedMoreInput) { buf.Sample?.Dispose(); return null; }
        if (r.Code == G.StreamChange) { buf.Sample?.Dispose(); streamChange = true; return null; }
        r.CheckError();
        return buf.Sample;
    }

    static IMFSample MakeSample(byte* data, int length, long ts, long duration)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out var p, out _, out _);
        Buffer.MemoryCopy(data, (void*)p, length, length);
        buffer.Unlock();
        buffer.CurrentLength = length;
        var s = MediaFactory.MFCreateSample();
        s.AddBuffer(buffer);
        buffer.Dispose();
        s.SampleTime = ts;
        s.SampleDuration = duration;
        return s;
    }

    static double PsnrY(byte[] reference, IMFSample decoded, int w, int h)
    {
        using var b = decoded.ConvertToContiguousBuffer();
        b.Lock(out var p, out _, out var len);
        try
        {
            int stride = len / (h * 3 / 2) >= w ? w : w; // 解碼器輸出 NV12 的寬度即為列距（1080 會對齊成 1088 列）
            byte* d = (byte*)p;
            double se = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int diff = reference[y * w + x] - d[y * stride + x];
                    se += diff * diff;
                }
            double mse = se / (w * (double)h);
            return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
        }
        finally { b.Unlock(); }
    }

    static Stage S(string name, List<double> v)
    {
        if (v.Count == 0) return new Stage(name, double.NaN, double.NaN, double.NaN);
        var sorted = v.OrderBy(x => x).ToArray();
        return new Stage(name, v.Average(), sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(sorted.Length * 0.95) - 1)], sorted[^1]);
    }

    static void WaitUntil(Stopwatch sw, double ms)
    {
        while (sw.Elapsed.TotalMilliseconds < ms - 1.5) Thread.Sleep(1);
        while (sw.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(50);
    }

    static void Print(RunResult r)
    {
        if (r.Error is not null) { Console.WriteLine($"[{r.Scenario}] {r.Encoder}：失敗 {r.Error}（已輸出 {r.Frames} 張；{r.D3D}）"); return; }
        static string F(Stage s) => $"{s.Avg,5:F1}/{s.P95,5:F1}";
        Console.WriteLine($"[{r.Scenario}] {r.Encoder}{(r.Hardware ? "（硬體）" : "（軟體）")} 張數 {r.Frames}（桌面新畫面 {r.NewDesktopFrames}）{(r.D3D.Length > 0 ? "；" + r.D3D : "")}");
        Console.WriteLine($"   平均/P95 ms  擷取 {F(r.Capture)}  轉換 {F(r.Convert)}  編碼 {F(r.Encode)}  解碼 {F(r.Decode)}  合計 {F(r.Total)}（最大 {r.Total.Max:F1}）");
        Console.WriteLine($"   位元率 {r.Mbps:F2} Mbps  PSNR(Y) {r.PsnrY:F1} dB  CPU 單核 {r.CpuOneCorePct:F0}%／全機 {r.CpuMachinePct:F1}%  時間戳不符 {r.TimestampMismatches}");
    }
}

internal sealed unsafe class FrameSource : IDisposable
{
    readonly ID3D11Device _dev;
    readonly ID3D11DeviceContext _ctx;
    readonly IDXGIOutput1 _output;
    IDXGIOutputDuplication _dup;
    readonly ID3D11Texture2D _staging;
    readonly byte* _bgra;
    public readonly byte* Nv12;
    public readonly int Nv12Length;
    public int Width { get; }
    public int Height { get; }
    public string AdapterName { get; }
    public bool Scroll { get; set; }
    public double LastCaptureMs { get; private set; }
    public double LastConvertMs { get; private set; }
    public int NewFrames { get; private set; }

    public FrameSource()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (!output.Description.AttachedToDesktop) { output.Dispose(); continue; }
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0 }, out _dev!, out _ctx!).CheckError();
                _output = output.QueryInterface<IDXGIOutput1>();
                output.Dispose();
                AdapterName = adapter.Description1.Description;
                var bounds = _output.Description.DesktopCoordinates;
                Width = bounds.Right - bounds.Left;
                Height = bounds.Bottom - bounds.Top;
                break;
            }
            adapter.Dispose();
            if (_output is not null) break;
        }
        if (_output is null) throw new InvalidOperationException("找不到接在桌面上的螢幕");
        _dup = _output.DuplicateOutput(_dev);
        _staging = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _bgra = (byte*)NativeMemory.AlignedAlloc((nuint)(Width * Height * 4), 64);
        Nv12Length = Width * Height * 3 / 2;
        Nv12 = (byte*)NativeMemory.AlignedAlloc((nuint)Nv12Length, 64);
        if (!Acquire(1000)) throw new InvalidOperationException("第一張桌面畫面沒有取得");
    }

    bool Acquire(uint timeout)
    {
        var r = _dup.AcquireNextFrame(timeout, out var info, out var res);
        if (r.Code == G.WaitTimeout) return false;
        if (r.Code == G.AccessLost) { _dup.Dispose(); _dup = _output.DuplicateOutput(_dev); return false; }
        r.CheckError();
        using (res)
        using (var tex = res.QueryInterface<ID3D11Texture2D>())
            _ctx.CopyResource(_staging, tex);
        _dup.ReleaseFrame();
        var map = _ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = Width * 4;
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, _bgra + (long)y * rowBytes, rowBytes, rowBytes);
        }
        finally { _ctx.Unmap(_staging, 0); }
        NewFrames++;
        return true;
    }

    public void Produce(int frame)
    {
        long t0 = Stopwatch.GetTimestamp();
        Acquire(0);
        long t1 = Stopwatch.GetTimestamp();
        int offset = Scroll ? (frame * 12) % Height : 0;
        ConvertNv12(offset);
        DrawBox(frame);
        long t2 = Stopwatch.GetTimestamp();
        LastCaptureMs = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
        LastConvertMs = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
    }

    void ConvertNv12(int offset)
    {
        int w = Width, h = Height;
        byte* bgra = _bgra, yPlane = Nv12, uv = Nv12 + w * h;
        Parallel.For(0, h / 2, pair =>
        {
            for (int k = 0; k < 2; k++)
            {
                int y = pair * 2 + k;
                byte* s = bgra + (long)((y + offset) % h) * w * 4;
                byte* d = yPlane + (long)y * w;
                for (int x = 0; x < w; x++, s += 4)
                    d[x] = (byte)(((47 * s[2] + 157 * s[1] + 16 * s[0] + 128) >> 8) + 16);
            }
            byte* s0 = bgra + (long)((pair * 2 + offset) % h) * w * 4;
            byte* uvRow = uv + (long)pair * w;
            for (int x = 0; x < w; x += 2)
            {
                int b = s0[x * 4], g = s0[x * 4 + 1], r = s0[x * 4 + 2];
                uvRow[x] = (byte)(((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128);
                uvRow[x + 1] = (byte)(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128);
            }
        });
    }

    void DrawBox(int frame)
    {
        const int size = 160;
        int bx = (frame * 16) % (Width - size);
        int by = (Height - size) / 2;
        for (int y = 0; y < size; y++)
        {
            byte* row = Nv12 + (long)(by + y) * Width + bx;
            for (int x = 0; x < size; x++) row[x] = (byte)(((x / 20 + y / 20 + frame) & 1) == 0 ? 235 : 40);
        }
    }

    public byte[] CopyLuma()
    {
        var copy = new byte[Width * Height];
        Marshal.Copy((IntPtr)Nv12, copy, 0, copy.Length);
        return copy;
    }

    public void Dispose()
    {
        _dup.Dispose(); _staging.Dispose(); _output.Dispose(); _ctx.Dispose(); _dev.Dispose();
        NativeMemory.AlignedFree(_bgra); NativeMemory.AlignedFree(Nv12);
    }
}
