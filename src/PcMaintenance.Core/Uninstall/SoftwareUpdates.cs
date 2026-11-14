using System.Diagnostics;
using System.Text;

namespace PcMaintenance.Core.Uninstall;

/// <summary>An installed program winget has a newer version of.</summary>
public sealed record SoftwareUpdate(string Name, string Id, string Version, string Available)
{
    /// <summary>winget cuts long ids with "…"; such an id cannot be passed to --id.</summary>
    public bool IsIdComplete => Id.Length > 0 && !Id.EndsWith('…');
}

/// <param name="WingetFound">False where Windows' package manager is not installed (some Windows 10 and LTSC editions).</param>
/// <param name="Problem">Why the check failed, in words for the user; null when it worked.</param>
public sealed record UpdateCheck(bool WingetFound, IReadOnlyList<SoftwareUpdate> Updates, string? Problem);

/// <summary>
/// Asks Windows' package manager (winget) which installed programs have newer versions in its community catalogue, and
/// builds the commands that update them. Checking contacts the catalogue on the internet, so it runs only when asked;
/// updating runs winget in a console window the user can see and answer, never silently.
/// </summary>
public static class Winget
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// winget is an app execution alias in the user's WindowsApps folder, which is not on the PATH of every process.
    /// </summary>
    public static string? FindExecutable()
    {
        var alias = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(folder.Trim(), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        if (FindExecutable() is not { } winget) return new UpdateCheck(false, [], "這台電腦沒有 Windows 套件管理員（winget）");
        var start = new ProcessStartInfo(winget)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Only the community catalogue: the Microsoft Store source asks to accept its terms first.
        foreach (var arg in new[] { "upgrade", "--source", "winget", "--disable-interactivity" }) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("無法啟動 winget");
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                if (ct.IsCancellationRequested) throw;
                return new UpdateCheck(true, [], "winget 超過 3 分鐘沒有回應，請確認網路後再試一次");
            }
            var text = await output;
            var updates = Parse(text);
            // A table means it worked; without one, a non-zero exit code means it could not reach the catalogue.
            return updates.Count == 0 && process.ExitCode != 0 && !text.Contains("---", StringComparison.Ordinal) && !NoUpdates(text)
                ? new UpdateCheck(true, [], $"winget 無法查詢（代碼 0x{process.ExitCode:X8}），請確認網路，或公司網路是否擋住 winget 的軟體來源")
                : new UpdateCheck(true, updates, null);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new UpdateCheck(true, [], $"無法執行 winget：{ex.Message}");
        }
    }

    // "No installed package found matching input criteria." and its translations all carry the word for "no updates".
    private static bool NoUpdates(string output) =>
        output.Contains("找不到", StringComparison.Ordinal) || output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("No installed package", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads winget's table: a header line, a line of dashes, then one line per program, all padded to columns that
    /// winget measures in console cells, where a Chinese character takes two. Progress drawn before the header with
    /// carriage returns is dropped; the table ends at the first line too short to reach the last column (the totals).
    /// </summary>
    public static IReadOnlyList<SoftwareUpdate> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(Visible).ToList();
        var separator = lines.FindIndex(l => l.Length >= 10 && l.Trim().All(c => c == '-'));
        if (separator < 1) return [];
        var columns = ColumnStarts(lines[separator - 1]);
        if (columns.Count < 4) return [];
        var updates = new List<SoftwareUpdate>();
        foreach (var line in lines.Skip(separator + 1))
        {
            var cells = Cells(line);
            if (cells.Count <= columns[3]) break;
            string Field(int i) => string.Concat(cells.Skip(columns[i]).Take((i + 1 < columns.Count ? columns[i + 1] : cells.Count) - columns[i])).Trim();
            var update = new SoftwareUpdate(Field(0), Field(1), Field(2), Field(3));
            if (update.Id.Length > 0 && update.Available.Length > 0) updates.Add(update);
        }
        return updates;
    }

    /// <summary>What is left on screen of a line that was redrawn with carriage returns, without control characters.</summary>
    private static string Visible(string line)
    {
        var trimmed = line.TrimEnd('\r');
        var last = trimmed.LastIndexOf('\r');
        if (last >= 0) trimmed = trimmed[(last + 1)..];
        return new string(trimmed.Where(c => c >= ' ' || c == '\t').ToArray());
    }

    /// <summary>The cell where each header word starts; header words never contain spaces.</summary>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        var cell = 0;
        var previousSpace = true;
        foreach (var c in header)
        {
            var space = c == ' ';
            if (!space && previousSpace) starts.Add(cell);
            previousSpace = space;
            cell += CellWidth(c);
        }
        return starts;
    }

    /// <summary>The line as console cells: a wide character fills its cell and leaves the next one empty.</summary>
    private static List<string> Cells(string line)
    {
        var cells = new List<string>();
        for (var i = 0; i < line.Length; i++)
        {
            var text = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? line.Substring(i++, 2) : line[i].ToString();
            cells.Add(text);
            if (CellWidth(text[0]) == 2 || text.Length == 2) cells.Add("");
        }
        return cells;
    }

    /// <summary>East Asian wide and fullwidth characters take two console cells.</summary>
    internal static int CellWidth(char c) => c switch
    {
        >= 'ᄀ' and <= 'ᅟ' => 2,
        >= '⺀' and <= '〾' => 2,
        >= 'ぁ' and <= '㏿' => 2,
        >= '㐀' and <= '䶿' => 2,
        >= '一' and <= '鿿' => 2,
        >= 'ꀀ' and <= '꓏' => 2,
        >= '가' and <= '힣' => 2,
        >= '豈' and <= '﫿' => 2,
        >= '︰' and <= '﹏' => 2,
        >= '＀' and <= '｠' => 2,
        >= '￠' and <= '￦' => 2,
        _ => 1,
    };

    /// <summary>The console command that updates one program; a cut id falls back to a search winget can still narrow.</summary>
    public static string UpgradeCommand(SoftwareUpdate update) => update.IsIdComplete
        ? $"winget upgrade --id {Quote(update.Id)} --exact --source winget"
        : $"winget upgrade {Quote(update.Id.TrimEnd('…'))} --source winget";

    public const string UpgradeAllCommand = "winget upgrade --all --source winget";

    // Ids and names reach cmd.exe; quotes are not allowed in them, and & | < > ^ are harmless inside quotes.
    private static string Quote(string value) => $"\"{value.Replace("\"", "")}\"";
}
