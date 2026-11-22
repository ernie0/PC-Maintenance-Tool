using System.Buffers.Binary;

namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>
/// 一個 <see cref="FrameType.Video"/> 框的內容：小標頭 [8 byte 大端時間戳(ticks)][1 byte 是否關鍵影格]
/// 接著 H.264（Annex B）位元組。時間戳給控制端排顯示用，關鍵影格旗標讓控制端知道能否從這張起解。
/// </summary>
public static class VideoPayload
{
    public const int HeaderSize = 9;

    public static byte[] Encode(long timestampTicks, bool keyframe, ReadOnlySpan<byte> h264)
    {
        byte[] buffer = new byte[HeaderSize + h264.Length];
        BinaryPrimitives.WriteInt64BigEndian(buffer, timestampTicks);
        buffer[8] = (byte)(keyframe ? 1 : 0);
        h264.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out long timestampTicks, out bool keyframe, out ReadOnlySpan<byte> h264)
    {
        if (payload.Length < HeaderSize)
        {
            timestampTicks = 0; keyframe = false; h264 = default;
            return false;
        }
        timestampTicks = BinaryPrimitives.ReadInt64BigEndian(payload);
        keyframe = payload[8] != 0;
        h264 = payload[HeaderSize..];
        return true;
    }
}
