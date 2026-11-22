using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace PcMaintenance.Remote.Viewer.Decoding;

/// <summary>一張解好的畫面（NV12）。CodedWidth/Height 可能比實際顯示大（補到 16 的倍數）。</summary>
public sealed record DecodedNv12(byte[] Nv12, int CodedWidth, int CodedHeight, int StrideY, long TimestampTicks);

/// <summary>
/// 用 Media Foundation 同步解碼器把 H.264（Annex B）解回 NV12。選同步（軟體）解碼器，
/// 1080p30 在現代 CPU 綽綽有餘又最不容易出狀況；之後要再換硬體解碼可比照編碼器加自我測試。
/// 由單一執行緒呼叫 <see cref="Decode"/>；每餵一段資料就把目前能吐的畫面全部交給 onFrame。
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private readonly IMFTransform _mft;
    private int _codedW, _codedH, _strideY;

    public H264Decoder()
    {
        _mft = CreateDecoder();
        SetInputType();
        _mft.ProcessMessage((TMessageType)Mf.MsgBeginStreaming, 0);
        _mft.ProcessMessage((TMessageType)Mf.MsgStartOfStream, 0);
        try { ConfigureOutput(); } catch { /* 有些解碼器要等解析 SPS 才有輸出型別，等 STREAM_CHANGE 再設 */ }
    }

    public void Decode(ReadOnlySpan<byte> h264, long timestampTicks, Action<DecodedNv12> onFrame)
    {
        byte[] data = h264.ToArray();
        var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var p, out _, out _);
        new Span<byte>(data).CopyTo(new Span<byte>((void*)p, data.Length));
        buffer.Unlock();
        buffer.CurrentLength = data.Length;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestampTicks;
        try { _mft.ProcessInput(0, sample, 0); }
        finally { buffer.Dispose(); sample.Dispose(); }

        DrainOutputs(onFrame);
    }

    private void DrainOutputs(Action<DecodedNv12> onFrame)
    {
        while (true)
        {
            var info = _mft.GetOutputStreamInfo(0);
            bool provides = (info.Flags & 0x300) != 0;
            var buf = new OutputDataBuffer { StreamID = 0 };
            IMFSample? alloc = null;
            if (!provides)
            {
                alloc = MediaFactory.MFCreateSample();
                alloc.AddBuffer(MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 1)));
                buf.Sample = alloc;
            }

            var r = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
            buf.Events?.Dispose();

            if (r.Code == Mf.NeedMoreInput) { alloc?.Dispose(); return; }
            if (r.Code == Mf.StreamChange)
            {
                alloc?.Dispose();
                if (buf.Sample is not null && !ReferenceEquals(buf.Sample, alloc)) buf.Sample.Dispose();
                ConfigureOutput();
                continue;
            }
            if (r.Failure) { alloc?.Dispose(); return; }

            var outSample = buf.Sample!;
            try
            {
                long ts = outSample.SampleTime;
                using var contiguous = outSample.ConvertToContiguousBuffer();
                contiguous.Lock(out var p, out _, out var len);
                byte[] nv12 = new byte[len];
                new Span<byte>((void*)p, len).CopyTo(nv12);
                contiguous.Unlock();
                onFrame(new DecodedNv12(nv12, _codedW, _codedH, _strideY, ts));
            }
            finally
            {
                if (!ReferenceEquals(outSample, alloc)) outSample.Dispose();
                alloc?.Dispose();
            }
        }
    }

    private static IMFTransform CreateDecoder()
    {
        var input = new RegisterTypeInfo { GuidMajorType = Mf.Video, GuidSubtype = Mf.H264 };
        var output = new RegisterTypeInfo { GuidMajorType = Mf.Video, GuidSubtype = Mf.NV12 };
        var col = MediaFactory.MFTEnumEx(Mf.CatVideoDecoder, Mf.EnumSync | Mf.EnumSortAndFilter, input, output);
        foreach (var a in col)
        {
            try { return a.ActivateObject<IMFTransform>(); }
            catch { /* 試下一個 */ }
        }
        throw new InvalidOperationException("找不到可用的 H.264 解碼器");
    }

    private void SetInputType()
    {
        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(Mf.MajorType, Mf.Video);
        inType.Set(Mf.Subtype, Mf.H264);
        _mft.SetInputType(0, inType, 0);
    }

    private void ConfigureOutput()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = _mft.GetOutputAvailableType(0, i); }
            catch { throw new InvalidOperationException("解碼器未提供 NV12 輸出型別"); }
            if (type is null) throw new InvalidOperationException("解碼器未提供 NV12 輸出型別");

            if (type.GetGUID(Mf.Subtype) == Mf.NV12)
            {
                _mft.SetOutputType(0, type, 0);
                ReadOutputDimensions();
                return;
            }
        }
    }

    private void ReadOutputDimensions()
    {
        var cur = _mft.GetOutputCurrentType(0);
        ulong size = cur.GetUInt64(Mf.FrameSize);
        _codedW = Mf.Hi(size);
        _codedH = Mf.Lo(size);
        try { _strideY = (int)cur.GetUInt32(Mf.DefaultStride); } catch { _strideY = 0; }
        if (_strideY <= 0) _strideY = _codedW;
    }

    public void Dispose()
    {
        try { _mft.ProcessMessage((TMessageType)Mf.MsgEndStreaming, 0); } catch { }
        _mft.Dispose();
    }
}
