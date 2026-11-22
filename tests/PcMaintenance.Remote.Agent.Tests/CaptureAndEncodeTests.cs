using PcMaintenance.Remote.Agent.Capture;
using PcMaintenance.Remote.Agent.Encoding;

namespace PcMaintenance.Remote.Agent.Tests;

public class Nv12ConverterTests
{
    private static byte[] SolidBgra(int w, int h, byte b, byte g, byte r, int stride)
    {
        byte[] buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                buf[o] = b; buf[o + 1] = g; buf[o + 2] = r; buf[o + 3] = 255;
            }
        return buf;
    }

    [Fact]
    public void Black_maps_to_y16_uv128()
    {
        const int w = 8, h = 4, stride = w * 4;
        byte[] dst = new byte[Nv12Converter.RequiredSize(w, h)];
        Nv12Converter.Convert(SolidBgra(w, h, 0, 0, 0, stride), stride, w, h, dst);

        Assert.All(dst[..(w * h)], y => Assert.Equal(16, y));
        Assert.All(dst[(w * h)..], uv => Assert.Equal(128, uv));
    }

    [Fact]
    public void White_maps_to_y235()
    {
        const int w = 8, h = 4, stride = w * 4;
        byte[] dst = new byte[Nv12Converter.RequiredSize(w, h)];
        Nv12Converter.Convert(SolidBgra(w, h, 255, 255, 255, stride), stride, w, h, dst);
        Assert.All(dst[..(w * h)], y => Assert.Equal(235, y));
    }

    [Fact]
    public void Honours_source_stride_with_padding()
    {
        const int w = 4, h = 2;
        int stride = w * 4 + 64; // 列尾有填充，模擬 Desktop Duplication 的列距
        byte[] dst = new byte[Nv12Converter.RequiredSize(w, h)];
        Nv12Converter.Convert(SolidBgra(w, h, 255, 255, 255, stride), stride, w, h, dst);
        Assert.All(dst[..(w * h)], y => Assert.Equal(235, y));
    }

    [Fact]
    public void Odd_dimensions_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Nv12Converter.Convert(new byte[100], 12, 3, 2, new byte[100]));
    }

    [Fact]
    public void Too_small_destination_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Nv12Converter.Convert(new byte[8 * 4 * 4], 8 * 4, 8, 4, new byte[4]));
    }
}

public class StreamControllerTests
{
    [Fact]
    public void First_frame_is_always_a_keyframe()
    {
        var c = new StreamController();
        Assert.True(c.ShouldEmitKeyframe(0));
    }

    [Fact]
    public void Keyframe_emitted_again_after_interval_but_not_before()
    {
        var c = new StreamController(keyframeIntervalSeconds: 2);
        Assert.True(c.ShouldEmitKeyframe(0));
        Assert.False(c.ShouldEmitKeyframe(TimeSpan.TicksPerSecond));         // 1 秒，未到
        Assert.True(c.ShouldEmitKeyframe(2 * TimeSpan.TicksPerSecond));      // 2 秒，到了
    }

    [Fact]
    public void Requested_keyframe_emitted_on_next_check()
    {
        var c = new StreamController();
        c.ShouldEmitKeyframe(0);
        Assert.False(c.ShouldEmitKeyframe(1000));
        c.RequestKeyframe();
        Assert.True(c.ShouldEmitKeyframe(1001));
    }

    [Theory]
    [InlineData(50, StreamController.MinBitrateKbps)]
    [InlineData(999_999, StreamController.MaxBitrateKbps)]
    [InlineData(3000, 3000)]
    public void Bitrate_is_clamped(int requested, int expected)
    {
        var c = new StreamController();
        c.Apply(requested, 30, false);
        Assert.Equal(expected, c.BitrateKbps);
    }

    [Theory]
    [InlineData(0, StreamController.MinFps)]
    [InlineData(240, StreamController.MaxFps)]
    [InlineData(30, 30)]
    public void Fps_is_clamped(int requested, int expected)
    {
        var c = new StreamController();
        c.Apply(4000, requested, false);
        Assert.Equal(expected, c.Fps);
        Assert.Equal(1000.0 / expected, c.FrameIntervalMs, 3);
    }

    [Fact]
    public void Apply_reports_change_only_when_values_move()
    {
        var c = new StreamController(4000, 30);
        Assert.False(c.Apply(4000, 30, false));
        Assert.True(c.Apply(2000, 30, false));
        Assert.True(c.Apply(2000, 15, false));
        Assert.False(c.Apply(2000, 15, true)); // 只要關鍵影格，位元率幀率沒變
    }
}
