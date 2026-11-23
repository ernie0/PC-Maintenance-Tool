using System.Security.Cryptography;
using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Agent.Files;

/// <summary>
/// 被控端收檔：把控制端送來的 <see cref="FrameType.FileChunk"/> 片段依傳輸編號重組寫檔，
/// 用 End 的 SHA-256 核對完整性後，移到收檔資料夾（預設 exe 旁的「收到的檔案」，可用
/// 環境變數 PCM_REMOTE_DOWNLOADS 覆寫）。檔名會淨化避免路徑穿越、同名自動加序號。
/// 由接收迴圈單一執行緒呼叫。
/// </summary>
public sealed class FileReceiver : IDisposable
{
    private readonly string _dir;
    private readonly Dictionary<Guid, Incoming> _active = [];

    /// <summary>一個檔案收完並通過核對時觸發，參數為存檔完整路徑。</summary>
    public event Action<string>? FileSaved;

    private sealed class Incoming
    {
        public required string Name;
        public required FileStream Stream;
        public required string TempPath;
    }

    public FileReceiver(string? dir = null)
    {
        _dir = dir
            ?? Environment.GetEnvironmentVariable("PCM_REMOTE_DOWNLOADS")
            ?? Path.Combine(AppContext.BaseDirectory, "收到的檔案");
        Directory.CreateDirectory(_dir);
    }

    public void Handle(byte[] payload)
    {
        if (payload.Length == 0) return;
        switch (FileTransferProtocol.PeekKind(payload))
        {
            case FileMessageKind.Begin:
                if (FileTransferProtocol.TryDecodeBegin(payload, out var id, out _, out var name)) Begin(id, name);
                break;
            case FileMessageKind.Chunk:
                if (FileTransferProtocol.TryDecodeChunk(payload, out var cid, out var off, out var data)) Chunk(cid, off, data);
                break;
            case FileMessageKind.End:
                if (FileTransferProtocol.TryDecodeEnd(payload, out var eid, out var sha)) End(eid, sha);
                break;
        }
    }

    private void Begin(Guid id, string name)
    {
        string safe = SafeName(name);
        string temp = Path.Combine(_dir, $"{safe}.{id:N}.part"[..Math.Min($"{safe}.{id:N}.part".Length, 180)]);
        try
        {
            var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            _active[id] = new Incoming { Name = safe, Stream = fs, TempPath = temp };
        }
        catch { /* 無法建檔就略過這次傳輸 */ }
    }

    private void Chunk(Guid id, long offset, byte[] data)
    {
        if (!_active.TryGetValue(id, out var inc)) return;
        try
        {
            if (inc.Stream.Position != offset) inc.Stream.Seek(offset, SeekOrigin.Begin);
            inc.Stream.Write(data, 0, data.Length);
        }
        catch { Abort(id); }
    }

    private void End(Guid id, byte[] sha)
    {
        if (!_active.Remove(id, out var inc)) return;
        try
        {
            inc.Stream.Flush();
            inc.Stream.Dispose();
            byte[] actual;
            using (var f = File.OpenRead(inc.TempPath)) actual = SHA256.HashData(f);
            if (!actual.AsSpan().SequenceEqual(sha)) { TryDelete(inc.TempPath); return; } // 核對失敗丟棄
            string final = Dedup(Path.Combine(_dir, inc.Name));
            File.Move(inc.TempPath, final);
            FileSaved?.Invoke(final);
        }
        catch { TryDelete(inc.TempPath); }
    }

    private void Abort(Guid id)
    {
        if (_active.Remove(id, out var inc))
        {
            try { inc.Stream.Dispose(); } catch { }
            TryDelete(inc.TempPath);
        }
    }

    private static string SafeName(string name)
    {
        name = Path.GetFileName(name); // 去掉任何路徑成分
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    private static string Dedup(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, baseName = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public void Dispose()
    {
        foreach (var inc in _active.Values) { try { inc.Stream.Dispose(); } catch { } TryDelete(inc.TempPath); }
        _active.Clear();
    }
}
