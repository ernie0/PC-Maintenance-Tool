using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Core.Tests;

public class FileTransferProtocolTests
{
    [Fact]
    public void Begin_round_trips()
    {
        var id = Guid.NewGuid();
        byte[] p = FileTransferProtocol.EncodeBegin(id, 123456789, "報告 2026.pdf");
        Assert.Equal(FileMessageKind.Begin, FileTransferProtocol.PeekKind(p));
        Assert.True(FileTransferProtocol.TryDecodeBegin(p, out var id2, out long size, out string name));
        Assert.Equal(id, id2);
        Assert.Equal(123456789, size);
        Assert.Equal("報告 2026.pdf", name);
    }

    [Fact]
    public void Chunk_round_trips()
    {
        var id = Guid.NewGuid();
        byte[] data = [1, 2, 3, 250, 0, 99];
        byte[] p = FileTransferProtocol.EncodeChunk(id, 65536, data);
        Assert.Equal(FileMessageKind.Chunk, FileTransferProtocol.PeekKind(p));
        Assert.True(FileTransferProtocol.TryDecodeChunk(p, out var id2, out long offset, out var back));
        Assert.Equal(id, id2);
        Assert.Equal(65536, offset);
        Assert.Equal(data, back);
    }

    [Fact]
    public void End_round_trips()
    {
        var id = Guid.NewGuid();
        byte[] sha = new byte[32];
        for (int i = 0; i < 32; i++) sha[i] = (byte)(i * 7);
        byte[] p = FileTransferProtocol.EncodeEnd(id, sha);
        Assert.Equal(FileMessageKind.End, FileTransferProtocol.PeekKind(p));
        Assert.True(FileTransferProtocol.TryDecodeEnd(p, out var id2, out var back));
        Assert.Equal(id, id2);
        Assert.Equal(sha, back);
    }

    [Fact]
    public void Decoders_reject_wrong_kind_or_short_input()
    {
        var id = Guid.NewGuid();
        byte[] begin = FileTransferProtocol.EncodeBegin(id, 1, "a");
        Assert.False(FileTransferProtocol.TryDecodeChunk(begin, out _, out _, out _)); // 種類不符
        Assert.False(FileTransferProtocol.TryDecodeEnd([(byte)FileMessageKind.End, 1, 2], out _, out _)); // 太短
    }

    [Fact]
    public void RemoteFiles_list_request_and_pull_round_trip()
    {
        byte[] lr = RemoteFilesProtocol.EncodeListRequest(@"C:\資料夾\x");
        Assert.Equal(RemoteFilesKind.ListRequest, RemoteFilesProtocol.PeekKind(lr));
        Assert.True(RemoteFilesProtocol.TryDecodeListRequest(lr, out var p1));
        Assert.Equal(@"C:\資料夾\x", p1);

        byte[] pr = RemoteFilesProtocol.EncodePullRequest(@"D:\a b\檔.bin");
        Assert.True(RemoteFilesProtocol.TryDecodePullRequest(pr, out var p2));
        Assert.Equal(@"D:\a b\檔.bin", p2);
        Assert.False(RemoteFilesProtocol.TryDecodeListRequest(pr, out _)); // 種類不符
    }

    [Fact]
    public void RemoteFiles_list_response_round_trips_entries()
    {
        var entries = new List<RemoteEntry>
        {
            new(true, 0, "子資料夾"),
            new(false, 3146049, "doc.bin"),
            new(false, 7, "readme.txt"),
        };
        byte[] resp = RemoteFilesProtocol.EncodeListResponse(@"C:\base", entries);
        Assert.Equal(RemoteFilesKind.ListResponse, RemoteFilesProtocol.PeekKind(resp));
        Assert.True(RemoteFilesProtocol.TryDecodeListResponse(resp, out var path, out var back));
        Assert.Equal(@"C:\base", path);
        Assert.Equal(3, back.Count);
        Assert.True(back[0].IsDirectory);
        Assert.Equal("子資料夾", back[0].Name);
        Assert.False(back[1].IsDirectory);
        Assert.Equal(3146049, back[1].Size);
        Assert.Equal("readme.txt", back[2].Name);
    }
}
