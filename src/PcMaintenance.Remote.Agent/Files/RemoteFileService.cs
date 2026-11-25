using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Agent.Files;

/// <summary>被控端的遠端檔案瀏覽：列出磁碟或資料夾內容，供控制端瀏覽與挑檔下載。只讀不寫。</summary>
public static class RemoteFileService
{
    private const int MaxEntries = 2000;

    /// <summary>列出一個資料夾（路徑空＝列磁碟根目錄）。資料夾在前、檔案在後，皆依名稱排序。</summary>
    public static List<RemoteEntry> List(string path)
    {
        var result = new List<RemoteEntry>();
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try { if (d.IsReady) result.Add(new RemoteEntry(true, 0, d.Name)); }
                    catch { }
                }
                return result;
            }

            var dirs = new List<RemoteEntry>();
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                try { dirs.Add(new RemoteEntry(true, 0, Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)))); }
                catch { }
                if (dirs.Count >= MaxEntries) break;
            }
            var files = new List<RemoteEntry>();
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try { files.Add(new RemoteEntry(false, new FileInfo(file).Length, Path.GetFileName(file))); }
                catch { files.Add(new RemoteEntry(false, 0, Path.GetFileName(file))); }
                if (files.Count >= MaxEntries) break;
            }
            dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            result.AddRange(dirs);
            result.AddRange(files);
        }
        catch { /* 無權限等 → 回目前已收集到的（可能為空） */ }
        return result;
    }
}
