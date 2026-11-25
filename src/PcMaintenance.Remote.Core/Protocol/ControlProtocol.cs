using System.Text.Json;

namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>控制通道訊息的 JSON 編解碼。</summary>
public static class ControlProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(ControlMessage message) =>
        JsonSerializer.Serialize(message, Options);

    /// <summary>
    /// 解析控制訊息。型別標籤未知或 JSON 格式錯誤時丟 <see cref="UnknownControlMessageException"/>，
    /// 讓呼叫端可以略過不認得的訊息而非中斷連線。
    /// </summary>
    public static ControlMessage Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(json, Options)
                ?? throw new UnknownControlMessageException("訊息解析為 null");
        }
        catch (JsonException ex)
        {
            throw new UnknownControlMessageException("控制訊息無法解析或型別未知", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new UnknownControlMessageException("控制訊息型別未知", ex);
        }
    }
}

public sealed class UnknownControlMessageException : Exception
{
    public UnknownControlMessageException(string message, Exception? inner = null) : base(message, inner) { }
}
