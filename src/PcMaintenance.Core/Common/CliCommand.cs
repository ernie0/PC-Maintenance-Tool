namespace PcMaintenance.Core.Common;

public enum CliAction
{
    None,
    Clean,
    AutoClean,
    ExportSoftware,
    ExportStartup,
    ExportSystem,
    RestorePoint,
}

/// <summary>Command-line actions for unattended use; no arguments opens the window.</summary>
public sealed record CliCommand(CliAction Action, bool DryRun = false, string? Path = null, string? Error = null)
{
    public const string Usage = """
        可用參數：
          --clean [--dry-run]                  立即清理預設勾選的項目（--dry-run 只列出、不刪除）
          --auto-clean [--dry-run]             距離上次自動清理超過設定天數才清理
          --export-software <檔案或資料夾>      匯出軟體清單（CSV）
          --export-startup <檔案或資料夾>       匯出開機與背景項目（CSV）
          --export-system <檔案或資料夾>        匯出電腦配備與系統版本（CSV）
          --restore-point                      建立系統還原點（需要系統管理員權限）
        """;

    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new CliCommand(CliAction.None);
        var action = CliAction.None;
        var dryRun = false;
        string? path = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i].ToLowerInvariant();
            if (arg == "--dry-run")
            {
                dryRun = true;
                continue;
            }
            var next = arg switch
            {
                "--clean" => CliAction.Clean,
                "--auto-clean" => CliAction.AutoClean,
                "--export-software" => CliAction.ExportSoftware,
                "--export-startup" => CliAction.ExportStartup,
                "--export-system" => CliAction.ExportSystem,
                "--restore-point" => CliAction.RestorePoint,
                _ => CliAction.None,
            };
            if (next == CliAction.None) return Fail($"無法辨識的參數：{args[i]}");
            if (action != CliAction.None) return Fail("一次只能指定一個動作");
            action = next;
            if (action is CliAction.ExportSoftware or CliAction.ExportStartup or CliAction.ExportSystem)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return Fail($"{args[i]} 後面要接檔案或資料夾路徑");
                path = args[++i];
            }
        }
        if (action == CliAction.None) return Fail("缺少要執行的動作");
        if (dryRun && action is not (CliAction.Clean or CliAction.AutoClean)) return Fail("--dry-run 只能搭配 --clean 或 --auto-clean");
        return new CliCommand(action, dryRun, path);
    }

    private static CliCommand Fail(string error) => new(CliAction.None, Error: error);
}
