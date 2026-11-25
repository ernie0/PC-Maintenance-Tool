using System.Text.Json;

namespace PcMaintenance.Remote.Server;

/// <summary>
/// 連線稽核：誰在何時連了哪一台、用什麼模式、是否隱藏、同意或拒絕。
/// 伺服器看不到畫面內容（端對端加密），但保留可查紀錄——隱藏模式有法規風險，紀錄是當責依據。
/// 用附加式 JSON Lines（每行一筆），避免引入有已知漏洞的原生 sqlite 依賴；要查詢可事後匯入資料庫。
/// 開始與結束各寫一筆（同一 requestId），保持純附加、不回頭改寫。
/// </summary>
public sealed class AuditLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AuditLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void LogStart(string requestId, string agentId, string adminName, string mode, bool hidden, string result) =>
        Write(new { ev = "start", requestId, agentId, adminName, mode, hidden, result, utc = DateTime.UtcNow.ToString("o") });

    public void LogEnd(string requestId) =>
        Write(new { ev = "end", requestId, utc = DateTime.UtcNow.ToString("o") });

    private void Write(object record)
    {
        string line = JsonSerializer.Serialize(record, Options);
        lock (_lock) _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}
