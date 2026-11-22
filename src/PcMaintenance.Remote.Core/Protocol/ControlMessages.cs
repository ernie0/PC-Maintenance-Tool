using System.Text.Json.Serialization;
using PcMaintenance.Remote.Core.Crypto;

namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>連線模式。</summary>
public static class SessionMode
{
    public const string Assist = "assist"; // 協助：員工求助，要對方同意
    public const string Manage = "manage"; // 管理：無人值守，要憑證授權
    public const string View = "view";     // 監看：唯讀
    public const string Wall = "wall";     // 電腦牆：多台低畫質縮圖

    public static bool IsKnown(string mode) =>
        mode is Assist or Manage or View or Wall;

    /// <summary>唯讀模式不送輸入事件。</summary>
    public static bool IsReadOnly(string mode) => mode is View or Wall;
}

/// <summary>
/// 控制通道訊息（WSS 文字框、JSON）。影像與輸入等大量資料走二進位框、見 <see cref="FrameType"/>。
/// 以 "t" 欄位當型別標籤做多型序列化。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(RegisterMessage), "register")]
[JsonDerivedType(typeof(HeartbeatMessage), "heartbeat")]
[JsonDerivedType(typeof(SessionRequestMessage), "session-request")]
[JsonDerivedType(typeof(SessionConsentMessage), "session-consent")]
[JsonDerivedType(typeof(SessionRejectMessage), "session-reject")]
[JsonDerivedType(typeof(QualityReportMessage), "quality-report")]
[JsonDerivedType(typeof(SessionEndMessage), "session-end")]
public abstract record ControlMessage;

/// <summary>被控端上線時向伺服器登記。</summary>
public sealed record RegisterMessage : ControlMessage
{
    public required string ComputerName { get; init; }
    public required string[] Groups { get; init; }
    public required string AgentVersion { get; init; }
    /// <summary>被控端的穩定識別碼（公鑰指紋），讓伺服器與控制端辨識同一台電腦。</summary>
    public required string AgentId { get; init; }
}

/// <summary>定時心跳，維持經 Cloudflare 的長連線並回報線上狀態。</summary>
public sealed record HeartbeatMessage : ControlMessage
{
    public long UnixMs { get; init; }
}

/// <summary>控制端要求連線。經伺服器轉給被控端。</summary>
public sealed record SessionRequestMessage : ControlMessage
{
    public required string RequestId { get; init; }
    public required string Mode { get; init; }
    public required string AdminName { get; init; }
    public required string TargetAgentId { get; init; }
    /// <summary>控制端臨時 ECDH 公鑰。</summary>
    public required byte[] ControllerPublicKey { get; init; }
    /// <summary>管理者憑證（無人值守／管理用）；有人值守協助可為 null、改用一次性密碼。</summary>
    public SignedAdminCertificate? Certificate { get; init; }
    /// <summary>
    /// 要求進入隱藏模式。被控端只有在這台已與此控制端完成配對（雙方都同意過一次）時才接受；
    /// 未配對時忽略此旗標，一律照正常模式顯示提示並請對方同意。
    /// </summary>
    public bool RequestHidden { get; init; }
}

/// <summary>被控端同意連線。</summary>
public sealed record SessionConsentMessage : ControlMessage
{
    public required string RequestId { get; init; }
    /// <summary>被控端臨時 ECDH 公鑰。</summary>
    public required byte[] AgentPublicKey { get; init; }
    /// <summary>這次是否以隱藏模式進行（已配對且控制端要求時才會是 true）。</summary>
    public bool Hidden { get; init; }
    /// <summary>這次連線是否讓此控制端完成配對（本次之後可進隱藏模式）。</summary>
    public bool PairingEstablished { get; init; }
}

/// <summary>被控端拒絕或無法連線。</summary>
public sealed record SessionRejectMessage : ControlMessage
{
    public required string RequestId { get; init; }
    public required string Reason { get; init; }
}

/// <summary>控制端回報想要的畫質，被控端據此調整位元率與幀率。</summary>
public sealed record QualityReportMessage : ControlMessage
{
    public required string RequestId { get; init; }
    public int TargetBitrateKbps { get; init; }
    public int TargetFps { get; init; }
    public bool RequestKeyframe { get; init; }
}

/// <summary>任一端結束連線。</summary>
public sealed record SessionEndMessage : ControlMessage
{
    public required string RequestId { get; init; }
    public string? Reason { get; init; }
}
