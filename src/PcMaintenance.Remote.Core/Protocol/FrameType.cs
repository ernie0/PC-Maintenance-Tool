namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>
/// 二進位資料框的類型（<see cref="Crypto.SecureChannel.Seal"/> 的第一個 byte）。
/// 這個 byte 納入 AES-GCM 的 AAD，在線上被改會驗證失敗。
/// </summary>
public enum FrameType : byte
{
    /// <summary>H.264 影像片段。P1。</summary>
    Video = 1,

    /// <summary>滑鼠或鍵盤事件（控制端 → 被控端）。P1。</summary>
    Input = 2,

    /// <summary>游標形狀或位置更新（被控端 → 控制端）。P1。</summary>
    Cursor = 3,

    /// <summary>螢幕清單（被控端 → 控制端）。P1。</summary>
    MonitorList = 4,

    /// <summary>切換要擷取的螢幕（控制端 → 被控端）。P1。</summary>
    MonitorSwitch = 5,

    /// <summary>剪貼簿內容。P3。</summary>
    Clipboard = 6,

    /// <summary>檔案傳輸片段。P3。</summary>
    FileChunk = 7,

    /// <summary>文字對話。P3。</summary>
    Chat = 8,

    /// <summary>遠端檔案瀏覽：清單要求／清單回應／拉檔要求（配合 <see cref="FileChunk"/> 回傳）。</summary>
    RemoteFiles = 10,
}
