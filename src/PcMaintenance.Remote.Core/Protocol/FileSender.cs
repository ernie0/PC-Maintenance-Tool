using System.Security.Cryptography;
using PcMaintenance.Remote.Core.Transport;

namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>把一個檔案分塊（附 SHA-256）送到連線另一端的共用函式，控制端與被控端都用它。</summary>
public static class FileSender
{
    public static async Task SendAsync(ISecureLink link, string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        long size = new FileInfo(path).Length;
        await link.SendAsync(FrameType.FileChunk, FileTransferProtocol.EncodeBegin(id, size, Path.GetFileName(path)), ct).ConfigureAwait(false);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fs = File.OpenRead(path);
        byte[] buf = new byte[FileTransferProtocol.ChunkSize];
        long offset = 0;
        int read, chunkNo = 0;
        while ((read = await fs.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buf, 0, read);
            await link.SendAsync(FrameType.FileChunk, FileTransferProtocol.EncodeChunk(id, offset, buf.AsSpan(0, read)), ct).ConfigureAwait(false);
            offset += read;
            progress?.Report(size > 0 ? (double)offset / size : 1);
            // 每約 1MB 讓出一下，讓影像／輸入等即時流量有空檔送出，傳大檔時不卡畫面與操作。
            if (++chunkNo % 16 == 0) await Task.Delay(1, ct).ConfigureAwait(false);
        }
        await link.SendAsync(FrameType.FileChunk, FileTransferProtocol.EncodeEnd(id, sha.GetHashAndReset()), ct).ConfigureAwait(false);
    }
}
