using System.Text.RegularExpressions;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.DiskTools;

public enum DiskJobOutcome
{
    Ok,
    Fixed,
    NeedsRepair,
    Failed,
}

public sealed record DiskJobResult(DiskJobOutcome Outcome, string Summary, string Output);

/// <summary>
/// Runs Windows' own disk tools: chkdsk for checking, defrag /O for optimising (TRIM on SSDs, defragmenting
/// hard disks) and fsutil to have Windows repair a volume at the next start. All of them need elevation.
/// </summary>
public static partial class DiskMaintenance
{
    [GeneratedRegex(@"\(0x[0-9A-Fa-f]{8}\)")]
    private static partial Regex ErrorCode();

    public static bool CanCheck(string fileSystem) => fileSystem.ToUpperInvariant() is "NTFS" or "FAT32" or "FAT" or "EXFAT";

    /// <summary>NTFS gets the online scan Explorer's "Check" button runs; FAT volumes a read-only check. Nothing is repaired.</summary>
    public static async Task<DiskJobResult> CheckAsync(VolumeInfo volume, FileLog? log = null, CancellationToken ct = default)
    {
        var arguments = volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ? $"{volume.Letter}: /scan" : $"{volume.Letter}:";
        var (exitCode, output) = await RunAsync("chkdsk.exe", arguments, ct);
        var result = DescribeCheck(exitCode, output);
        log?.Info($"檢查磁碟 {volume.Letter}:：{result.Summary}（chkdsk 結束代碼 {exitCode}）");
        return result;
    }

    internal static DiskJobResult DescribeCheck(int exitCode, string output) => exitCode switch
    {
        // 2 only means optional clean-up was skipped because nothing was allowed to change.
        0 or 2 => new DiskJobResult(DiskJobOutcome.Ok, "沒有發現問題", output),
        1 => new DiskJobResult(DiskJobOutcome.Fixed, "發現問題並已修正", output),
        3 => new DiskJobResult(DiskJobOutcome.NeedsRepair, "發現需要修復的問題，或無法完成檢查", output),
        _ => new DiskJobResult(DiskJobOutcome.Failed, $"檢查失敗（代碼 {exitCode}）", output),
    };

    public static async Task<DiskJobResult> OptimizeAsync(VolumeInfo volume, FileLog? log = null, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunAsync("defrag.exe", $"{volume.Letter}: /O /U", ct);
        var result = DescribeOptimize(exitCode, output);
        log?.Info($"最佳化磁碟 {volume.Letter}:：{result.Summary}（defrag 結束代碼 {exitCode}）");
        return result;
    }

    // defrag reports some failures with exit code 0 and an error code such as "(0x89000024)" in its text.
    internal static DiskJobResult DescribeOptimize(int exitCode, string output) =>
        exitCode == 0 && !ErrorCode().IsMatch(output)
            ? new DiskJobResult(DiskJobOutcome.Ok, "最佳化完成", output)
            : new DiskJobResult(DiskJobOutcome.Failed, "無法最佳化，詳細原因請看下方訊息", output);

    /// <summary>Marks the volume so Windows checks and repairs it at the next start, before any file is open.</summary>
    public static async Task<DiskJobResult> ScheduleRepairAsync(VolumeInfo volume, FileLog? log = null)
    {
        var (exitCode, output) = await RunAsync("fsutil.exe", $"dirty set {volume.Letter}:", CancellationToken.None);
        var result = exitCode == 0
            ? new DiskJobResult(DiskJobOutcome.Ok, "已排定在下次開機時修復", output)
            : new DiskJobResult(DiskJobOutcome.Failed, "無法排定修復", output);
        log?.Info($"排定開機修復 {volume.Letter}:：{result.Summary}（fsutil 結束代碼 {exitCode}）");
        return result;
    }

    private static Task<(int ExitCode, string Output)> RunAsync(string tool, string arguments, CancellationToken ct) =>
        ConsoleTool.RunAsync(tool, arguments, ct);

    /// <summary>The last meaningful lines of a tool's output, for showing under the result.</summary>
    public static string Tail(string output, int lines = 4) =>
        string.Join("\n", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(lines));
}
