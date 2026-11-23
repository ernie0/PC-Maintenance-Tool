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
}
