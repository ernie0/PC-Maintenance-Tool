using System.Buffers.Binary;

namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>遠端檔案瀏覽的子訊息種類（<see cref="FrameType.RemoteFiles"/> 框內容的第 1 個 byte）。</summary>
public enum RemoteFilesKind : byte
{
    ListRequest = 1,   // 控制端→被控端：要某資料夾清單（路徑空＝磁碟根目錄）
    ListResponse = 2,  // 被控端→控制端：清單
    PullRequest = 3,    // 控制端→被控端：要某檔（被控端以 FileChunk 回傳）
}

/// <summary>一筆遠端項目（資料夾或檔案）。</summary>
public readonly record struct RemoteEntry(bool IsDirectory, long Size, string Name);

/// <summary>
/// 遠端檔案瀏覽的二進位編碼。路徑與檔名用 UTF-8；清單回應帶項目型別、大小、名稱。
/// 純二進位、與加密層無關。
/// </summary>
public static class RemoteFilesProtocol
{
    public static RemoteFilesKind PeekKind(ReadOnlySpan<byte> p) => (RemoteFilesKind)p[0];

    // ---- ListRequest ----
    public static byte[] EncodeListRequest(string path) => EncodeKindPath(RemoteFilesKind.ListRequest, path);
    public static bool TryDecodeListRequest(ReadOnlySpan<byte> p, out string path) => TryDecodeKindPath(p, RemoteFilesKind.ListRequest, out path);

    // ---- PullRequest ----
    public static byte[] EncodePullRequest(string path) => EncodeKindPath(RemoteFilesKind.PullRequest, path);
    public static bool TryDecodePullRequest(ReadOnlySpan<byte> p, out string path) => TryDecodeKindPath(p, RemoteFilesKind.PullRequest, out path);

    // ---- ListResponse ----
    public static byte[] EncodeListResponse(string path, IReadOnlyList<RemoteEntry> entries)
    {
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
        int size = 1 + 2 + pathBytes.Length + 2;
        var nameBytes = new byte[entries.Count][];
        for (int i = 0; i < entries.Count; i++)
        {
            nameBytes[i] = System.Text.Encoding.UTF8.GetBytes(entries[i].Name);
            size += 1 + 8 + 2 + nameBytes[i].Length;
        }

        var buf = new byte[size];
        int o = 0;
        buf[o++] = (byte)RemoteFilesKind.ListResponse;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o, 2), (ushort)pathBytes.Length); o += 2;
        pathBytes.CopyTo(buf.AsSpan(o)); o += pathBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o, 2), (ushort)entries.Count); o += 2;
        for (int i = 0; i < entries.Count; i++)
        {
            buf[o++] = (byte)(entries[i].IsDirectory ? 0 : 1);
            BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(o, 8), entries[i].Size); o += 8;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o, 2), (ushort)nameBytes[i].Length); o += 2;
            nameBytes[i].CopyTo(buf.AsSpan(o)); o += nameBytes[i].Length;
        }
        return buf;
    }

    public static bool TryDecodeListResponse(ReadOnlySpan<byte> p, out string path, out List<RemoteEntry> entries)
    {
        path = ""; entries = [];
        if (p.Length < 5 || p[0] != (byte)RemoteFilesKind.ListResponse) return false;
        int o = 1;
        int pathLen = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(o, 2)); o += 2;
        if (p.Length < o + pathLen + 2) return false;
        path = System.Text.Encoding.UTF8.GetString(p.Slice(o, pathLen)); o += pathLen;
        int count = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(o, 2)); o += 2;
        for (int i = 0; i < count; i++)
        {
            if (p.Length < o + 11) return false;
            bool isDir = p[o++] == 0;
            long sz = BinaryPrimitives.ReadInt64BigEndian(p.Slice(o, 8)); o += 8;
            int nameLen = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(o, 2)); o += 2;
            if (p.Length < o + nameLen) return false;
            string name = System.Text.Encoding.UTF8.GetString(p.Slice(o, nameLen)); o += nameLen;
            entries.Add(new RemoteEntry(isDir, sz, name));
        }
        return true;
    }

    private static byte[] EncodeKindPath(RemoteFilesKind kind, string path)
    {
        byte[] b = System.Text.Encoding.UTF8.GetBytes(path);
        var buf = new byte[1 + 2 + b.Length];
        buf[0] = (byte)kind;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1, 2), (ushort)b.Length);
        b.CopyTo(buf.AsSpan(3));
        return buf;
    }

    private static bool TryDecodeKindPath(ReadOnlySpan<byte> p, RemoteFilesKind kind, out string path)
    {
        path = "";
        if (p.Length < 3 || p[0] != (byte)kind) return false;
        int len = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(1, 2));
        if (p.Length < 3 + len) return false;
        path = System.Text.Encoding.UTF8.GetString(p.Slice(3, len));
        return true;
    }
}
