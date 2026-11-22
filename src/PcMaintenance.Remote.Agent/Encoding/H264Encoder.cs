using System.Collections.Concurrent;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace PcMaintenance.Remote.Agent.Encoding;

/// <summary>
/// 把 NV12 畫面編成 H.264（Annex B）。開始時做自我測試，挑一個真的會吐出畫面的編碼器：
/// 先試硬體（P0 發現某些 Intel 內顯的 MF 編碼器會收了畫面卻不輸出），再退回軟體。
/// 畫面以系統記憶體樣本餵入（P0 證實 NVIDIA 硬體 MFT 可接受），不需綁 D3D 裝置。
/// 編好的片段由 <see cref="FrameEncoded"/> 在背景執行緒回呼。本型別非執行緒安全：
/// 由單一執行緒呼叫 <see cref="Submit"/>。
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private readonly int _width, _height, _fps, _bitrateKbps;
    private IMFTransform? _mft;
    private bool _isAsync;
    private Thread? _worker;
    private Thread? _eventPump;
    private readonly BlockingCollection<(byte[] data, long ts)> _input = new(boundedCapacity: 8);
    private readonly BlockingCollection<IMFMediaEvent> _events = new();
    private volatile bool _faulted;

    public H264Encoder(int width, int height, int fps, int bitrateKbps)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("寬與高必須是偶數");
        _width = width; _height = height; _fps = fps; _bitrateKbps = bitrateKbps;
    }

    public event Action<EncodedFrame>? FrameEncoded;
    public string EncoderName { get; private set; } = "";
    public bool IsHardware { get; private set; }

    /// <summary>自我測試後選定編碼器並啟動背景編碼。選不到可用編碼器時丟例外。</summary>
    public void Start()
    {
        int? which = SelectEncoderIndex();
        if (which is null)
            throw new InvalidOperationException("找不到任何能正常輸出的 H.264 編碼器");

        // 必須重新列舉拿到乾淨的 activate：同一個 IMFActivate 在自我測試時已 ActivateObject 過，
        // 再呼叫一次會回傳同一個已關閉的物件（不發事件）。重新列舉會得到全新的 COM 物件。
        var chosen = Enumerate().ElementAt(which.Value);
        (EncoderName, IsHardware) = (chosen.Name, chosen.Hardware);
        _mft = CreateConfigured(chosen.Activate, chosen.Hardware, out _isAsync);
        if (_isAsync)
        {
            var gen = _mft.QueryInterface<IMFMediaEventGenerator>();
            _eventPump = new Thread(() => PumpEvents(gen)) { IsBackground = true, Name = "h264-events" };
            _eventPump.Start();
        }
        _worker = new Thread(RunWorker) { IsBackground = true, Name = "h264-encode" };
        _worker.Start();
    }

    /// <summary>送一張 NV12 畫面去編碼。內部複製一份，呼叫端的緩衝區可立即重用。</summary>
    public void Submit(ReadOnlySpan<byte> nv12, long timestampTicks)
    {
        if (_faulted) throw new InvalidOperationException("編碼器已發生錯誤");
        if (_input.IsAddingCompleted) return;
        byte[] copy = nv12.ToArray();
        try { _input.Add((copy, timestampTicks)); }
        catch (InvalidOperationException) { /* 已停止 */ }
    }

    /// <summary>停止：排空剩餘畫面後結束背景執行緒。</summary>
    public void Stop()
    {
        if (!_input.IsAddingCompleted) _input.CompleteAdding();
        // 加逾時，避免編碼器異常時卡死呼叫端；背景執行緒會隨程序結束。
        _worker?.Join(TimeSpan.FromSeconds(5));
        _eventPump?.Join(TimeSpan.FromSeconds(2));
    }

    // ---- 編碼器選擇與自我測試 ----

    /// <summary>回傳第一個通過自我測試的編碼器在 <see cref="Enumerate"/> 順序中的序號。</summary>
    private int? SelectEncoderIndex()
    {
        int index = 0;
        foreach (var cand in Enumerate())
        {
            if (Probe(cand.Activate, cand.Hardware)) return index;
            index++;
        }
        return null;
    }

    private static IEnumerable<(IMFActivate Activate, string Name, bool Hardware)> Enumerate()
    {
        var input = new RegisterTypeInfo { GuidMajorType = Mf.Video, GuidSubtype = Mf.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = Mf.Video, GuidSubtype = Mf.H264 };
        foreach (var (flags, hw) in new[] { (Mf.EnumHardware | Mf.EnumSortAndFilter, true), (Mf.EnumSync | Mf.EnumSortAndFilter, false) })
        {
            IMFActivateCollection col;
            try { col = MediaFactory.MFTEnumEx(Mf.CatVideoEncoder, flags, input, output); }
            catch { continue; }
            foreach (var a in col)
            {
                string name;
                try { name = a.GetString(Mf.FriendlyName); } catch { name = "未命名"; }
                yield return (a, name, hw);
            }
        }
    }

    /// <summary>用全丟棄的實例餵兩張黑畫面，2 秒內有輸出才算可用（避開 P0 的 Intel 卡住）。</summary>
    private bool Probe(IMFActivate activate, bool hardware)
    {
        IMFTransform? mft = null;
        try
        {
            mft = CreateConfigured(activate, hardware, out bool async);
            byte[] black = new byte[Capture.Nv12Converter.RequiredSize(_width, _height)];
            Array.Fill(black, (byte)16, 0, _width * _height);
            Array.Fill(black, (byte)128, _width * _height, black.Length - _width * _height);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            bool gotOutput = false;
            if (async)
            {
                // 用抽取執行緒把事件丟進佇列，主流程以有逾時的 TryTake 取。
                // 關鍵：不可直接在這裡呼叫會阻塞的 GetEvent——P0 的某些 Intel 編碼器完全不發事件，
                // 直接 GetEvent 會永遠卡住、逾時判斷跑不到。抽取執行緒卡住沒關係（背景、開始時只一次）。
                var gen = mft.QueryInterface<IMFMediaEventGenerator>();
                var queue = new BlockingCollection<IMFMediaEvent>();
                var pump = new Thread(() =>
                {
                    try
                    {
                        while (true)
                        {
                            var ev = gen.GetEvent(0);
                            queue.Add(ev);
                            if ((int)ev.EventType == Mf.DrainComplete) break;
                        }
                    }
                    catch { }
                    finally { try { queue.CompleteAdding(); } catch { } }
                }) { IsBackground = true, Name = "h264-probe" };
                pump.Start();

                int fed = 0;
                while (DateTime.UtcNow < deadline && !gotOutput)
                {
                    if (!queue.TryTake(out var ev, 100)) continue;
                    using (ev)
                    {
                        int type = (int)ev.EventType;
                        if (type == Mf.NeedInput && fed < 3)
                            FeedFrame(mft, black, fed++ * TimeSpan.TicksPerSecond / _fps);
                        else if (type == Mf.HaveOutput)
                            gotOutput = DrainOne(mft) is not null;
                    }
                }
            }
            else
            {
                for (int i = 0; i < 3 && !gotOutput; i++)
                {
                    FeedFrame(mft, black, (long)i * TimeSpan.TicksPerSecond / _fps);
                    gotOutput = DrainOne(mft) is not null;
                }
            }
            return gotOutput;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { mft?.ProcessMessage((TMessageType)Mf.MsgEndStreaming, 0); } catch { }
            mft?.Dispose();
            try { activate.ShutdownObject(); } catch { } // 釋放自我測試用的實例
        }
    }

    // ---- 建立與設定 ----

    private IMFTransform CreateConfigured(IMFActivate activate, bool hardware, out bool isAsync)
    {
        var mft = activate.ActivateObject<IMFTransform>();
        isAsync = hardware;
        if (hardware) mft.Attributes.Set(Mf.AsyncUnlock, 1u).CheckError();
        try { mft.Attributes.Set(Mf.LowLatency, 1u); } catch { }

        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(Mf.MajorType, Mf.Video);
        outType.Set(Mf.Subtype, Mf.H264);
        outType.Set(Mf.AvgBitrate, (uint)(_bitrateKbps * 1000));
        outType.Set(Mf.FrameSize, Mf.Pack(_width, _height));
        outType.Set(Mf.FrameRate, Mf.Pack(_fps, 1));
        outType.Set(Mf.PixelAspect, Mf.Pack(1, 1));
        outType.Set(Mf.Interlace, 2u); // progressive
        outType.Set(Mf.Profile, Mf.MainProfile);
        mft.SetOutputType(0, outType, 0);

        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(Mf.MajorType, Mf.Video);
        inType.Set(Mf.Subtype, Mf.NV12);
        inType.Set(Mf.FrameSize, Mf.Pack(_width, _height));
        inType.Set(Mf.FrameRate, Mf.Pack(_fps, 1));
        inType.Set(Mf.PixelAspect, Mf.Pack(1, 1));
        inType.Set(Mf.Interlace, 2u);
        mft.SetInputType(0, inType, 0);

        mft.ProcessMessage((TMessageType)Mf.MsgBeginStreaming, 0);
        mft.ProcessMessage((TMessageType)Mf.MsgStartOfStream, 0);
        return mft;
    }

    // ---- 背景編碼 ----

    private void PumpEvents(IMFMediaEventGenerator gen)
    {
        try
        {
            while (true)
            {
                var ev = gen.GetEvent(0);
                int type = (int)ev.EventType;
                _events.Add(ev);
                if (type == Mf.DrainComplete) break;
            }
        }
        catch { }
        finally { _events.CompleteAdding(); gen.Dispose(); }
    }

    private void RunWorker()
    {
        try
        {
            if (_isAsync) RunAsync();
            else RunSync();
        }
        catch (Exception)
        {
            _faulted = true;
        }
    }

    private void RunAsync()
    {
        bool drainSent = false;
        foreach (var ev in _events.GetConsumingEnumerable())
        {
            using (ev)
            {
                int type = (int)ev.EventType;
                if (type == Mf.NeedInput)
                {
                    if (TryTakeInput(out var frame))
                        FeedFrame(_mft!, frame.data, frame.ts);
                    else if (!drainSent)
                    {
                        _mft!.ProcessMessage((TMessageType)Mf.MsgDrain, 0);
                        drainSent = true;
                    }
                }
                else if (type == Mf.HaveOutput)
                {
                    EmitIfAny(DrainOne(_mft!));
                }
                else if (type == Mf.DrainComplete)
                {
                    break;
                }
            }
        }
    }

    private void RunSync()
    {
        foreach (var (data, ts) in _input.GetConsumingEnumerable())
        {
            FeedFrame(_mft!, data, ts);
            EmitIfAny(DrainOne(_mft!));
        }
        _mft!.ProcessMessage((TMessageType)Mf.MsgDrain, 0);
        while (EmitIfAny(DrainOne(_mft!))) { }
    }

    private bool TryTakeInput(out (byte[] data, long ts) frame)
    {
        try { frame = _input.Take(); return true; }
        catch (InvalidOperationException) { frame = default; return false; } // 已完成且排空
    }

    private bool EmitIfAny(EncodedFrame? f)
    {
        if (f is null) return false;
        FrameEncoded?.Invoke(f);
        return true;
    }

    private static void FeedFrame(IMFTransform mft, byte[] nv12, long tsTicks)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
        buffer.Lock(out var p, out _, out _);
        new Span<byte>(nv12).CopyTo(new Span<byte>((void*)p, nv12.Length));
        buffer.Unlock();
        buffer.CurrentLength = nv12.Length;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = tsTicks;
        sample.SampleDuration = TimeSpan.TicksPerSecond / 30;
        mft.ProcessInput(0, sample, 0);
        buffer.Dispose();
        sample.Dispose();
    }

    private static EncodedFrame? DrainOne(IMFTransform mft)
    {
        var info = mft.GetOutputStreamInfo(0);
        bool provides = (info.Flags & 0x300) != 0;
        var buf = new OutputDataBuffer { StreamID = 0 };
        IMFSample? alloc = null;
        if (!provides)
        {
            alloc = MediaFactory.MFCreateSample();
            alloc.AddBuffer(MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 1)));
            buf.Sample = alloc;
        }
        var r = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
        buf.Events?.Dispose();
        if (r.Code == Mf.NeedMoreInput) { alloc?.Dispose(); return null; }
        if (r.Failure)
        {
            alloc?.Dispose();
            if (buf.Sample is not null && !ReferenceEquals(buf.Sample, alloc)) buf.Sample.Dispose();
            return null;
        }

        var outSample = buf.Sample!;
        try
        {
            bool keyframe = true;
            try { keyframe = outSample.GetUInt32(Mf.CleanPoint) != 0; } catch { }
            long ts = outSample.SampleTime;
            using var contiguous = outSample.ConvertToContiguousBuffer();
            contiguous.Lock(out var p, out _, out var len);
            byte[] data = new byte[len];
            new Span<byte>((void*)p, len).CopyTo(data);
            contiguous.Unlock();
            return new EncodedFrame { Data = data, TimestampTicks = ts, IsKeyframe = keyframe };
        }
        finally
        {
            if (!ReferenceEquals(outSample, alloc)) outSample.Dispose();
            alloc?.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        try { _mft?.ProcessMessage((TMessageType)Mf.MsgEndStreaming, 0); } catch { }
        _mft?.Dispose();
        _input.Dispose();
        _events.Dispose();
    }
}
