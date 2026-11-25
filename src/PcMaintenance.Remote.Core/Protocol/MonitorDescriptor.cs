using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>
/// 被控端送給控制端的一個螢幕描述（跨平台 DTO，不含 Windows 型別）。
/// 被控端的實際螢幕物件轉成這個再序列化，控制端用來畫螢幕清單。
/// </summary>
public sealed record MonitorDescriptor
{
    public required int Index { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool IsPrimary { get; init; }
    public required string Name { get; init; }
}

/// <summary>
/// <see cref="FrameType.MonitorList"/>（JSON 螢幕清單）與 <see cref="FrameType.MonitorSwitch"/>
/// （4 byte 大端螢幕序號）兩種框內容的編解碼。
/// </summary>
public static class MonitorProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] EncodeList(IReadOnlyList<MonitorDescriptor> monitors) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(monitors, Options));

    public static IReadOnlyList<MonitorDescriptor> DecodeList(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<List<MonitorDescriptor>>(payload, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static byte[] EncodeSwitch(int monitorIndex)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, monitorIndex);
        return b;
    }

    public static bool TryDecodeSwitch(ReadOnlySpan<byte> payload, out int monitorIndex)
    {
        if (payload.Length < 4) { monitorIndex = 0; return false; }
        monitorIndex = BinaryPrimitives.ReadInt32BigEndian(payload);
        return true;
    }
}
