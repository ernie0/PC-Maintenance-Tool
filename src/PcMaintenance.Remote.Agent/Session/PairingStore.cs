using System.Text.Json;

namespace PcMaintenance.Remote.Agent.Session;

/// <summary>一筆配對紀錄：哪位管理者、何時配對的。</summary>
public sealed record PairingRecord(string AdminName, string PairedUtc);

/// <summary>
/// 被控端的配對清單：記住哪些管理者身分（憑證指紋）已經雙方同意過一次，之後可進隱藏模式。
/// 存成本機 JSON 檔。主程式的設定頁可以列出並解除配對（解除後該管理者要再經一次正常同意）。
/// </summary>
public sealed class PairingStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, PairingRecord> _map;

    public PairingStore(string path)
    {
        _path = path;
        _map = Load(path);
    }

    public bool IsPaired(string fingerprint)
    {
        lock (_lock) return _map.ContainsKey(fingerprint);
    }

    public void Pair(string fingerprint, string adminName)
    {
        lock (_lock)
        {
            _map[fingerprint] = new PairingRecord(adminName, DateTime.UtcNow.ToString("o"));
            Save();
        }
    }

    public bool Unpair(string fingerprint)
    {
        lock (_lock)
        {
            if (!_map.Remove(fingerprint)) return false;
            Save();
            return true;
        }
    }

    public IReadOnlyDictionary<string, PairingRecord> List()
    {
        lock (_lock) return new Dictionary<string, PairingRecord>(_map);
    }

    private static Dictionary<string, PairingRecord> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, PairingRecord>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception) { /* 檔案壞了當作沒配對，使用者會再經正常同意重建 */ }
        return [];
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
    }
}
