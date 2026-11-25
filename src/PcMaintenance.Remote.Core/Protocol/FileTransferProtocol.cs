using System.Buffers.Binary;

namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>檔案傳輸的子訊息種類（放在 <see cref="FrameType.FileChunk"/> 框內容的第 1 個 byte）。</summary>
public enum FileMessageKind : byte
{
    Begin = 1,  // 開始：傳輸編號＋總大小＋檔名
    Chunk = 2,  // 片段：傳輸編號＋位移＋資料
    End = 3,     // 結束：傳輸編號＋SHA-256
}

/// <summary>
/// 檔案傳輸的二進位編碼（<see cref="FrameType.FileChunk"/> 框的內容）。一次傳輸以 16 byte 的編號串起
/// Begin→多個 Chunk→End；收端依位移寫檔、用 End 的 SHA-256 核對完整性。純二進位、與加密層無關。
/// </summary>
public static class FileTransferProtocol
{
    /// <summary>單一 Chunk 建議的資料大小（64KB），遠小於框長度上限。</summary>
    public const int ChunkSize = 64 * 1024;

    public static FileMessageKind PeekKind(ReadOnlySpan<byte> payload) => (FileMessageKind)payload[0];

    public static byte[] EncodeBegin(Guid id, long totalSize, string name)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var buf = new byte[1 + 16 + 8 + 2 + nameBytes.Length];
        buf[0] = (byte)FileMessageKind.Begin;
        id.TryWriteBytes(buf.AsSpan(1, 16));
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(17, 8), totalSize);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(25, 2), (ushort)nameBytes.Length);
        nameBytes.CopyTo(buf.AsSpan(27));
        return buf;
    }

    public static bool TryDecodeBegin(ReadOnlySpan<byte> p, out Guid id, out long totalSize, out string name)
    {
        id = Guid.Empty; totalSize = 0; name = "";
        if (p.Length < 27 || p[0] != (byte)FileMessageKind.Begin) return false;
        id = new Guid(p.Slice(1, 16));
        totalSize = BinaryPrimitives.ReadInt64BigEndian(p.Slice(17, 8));
        int nameLen = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(25, 2));
        if (p.Length < 27 + nameLen) return false;
        name = System.Text.Encoding.UTF8.GetString(p.Slice(27, nameLen));
        return true;
    }

    public static byte[] EncodeChunk(Guid id, long offset, ReadOnlySpan<byte> data)
    {
        var buf = new byte[1 + 16 + 8 + data.Length];
        buf[0] = (byte)FileMessageKind.Chunk;
        id.TryWriteBytes(buf.AsSpan(1, 16));
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(17, 8), offset);
        data.CopyTo(buf.AsSpan(25));
        return buf;
    }

    public static bool TryDecodeChunk(ReadOnlySpan<byte> p, out Guid id, out long offset, out byte[] data)
    {
        id = Guid.Empty; offset = 0; data = [];
        if (p.Length < 25 || p[0] != (byte)FileMessageKind.Chunk) return false;
        id = new Guid(p.Slice(1, 16));
        offset = BinaryPrimitives.ReadInt64BigEndian(p.Slice(17, 8));
        data = p.Slice(25).ToArray();
        return true;
    }

    public static byte[] EncodeEnd(Guid id, ReadOnlySpan<byte> sha256)
    {
        var buf = new byte[1 + 16 + 32];
        buf[0] = (byte)FileMessageKind.End;
        id.TryWriteBytes(buf.AsSpan(1, 16));
        sha256[..32].CopyTo(buf.AsSpan(17));
        return buf;
    }

    public static bool TryDecodeEnd(ReadOnlySpan<byte> p, out Guid id, out byte[] sha256)
    {
        id = Guid.Empty; sha256 = [];
        if (p.Length < 49 || p[0] != (byte)FileMessageKind.End) return false;
        id = new Guid(p.Slice(1, 16));
        sha256 = p.Slice(17, 32).ToArray();
        return true;
    }
}
