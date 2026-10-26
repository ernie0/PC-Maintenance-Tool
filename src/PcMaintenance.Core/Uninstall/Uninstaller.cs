using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Uninstall;

public static partial class UninstallCommand
{
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCode();

    /// <summary>
    /// Many MSI entries register "MsiExec.exe /I{code}", which opens repair/modify instead of removing;
    /// those are rewritten to "/x {code}".
    /// </summary>
    public static (string FileName, string Arguments) Parse(string uninstallString, Func<string, bool>? fileExists = null)
    {
        var (file, arguments) = CommandLine.Split(uninstallString, fileExists);
        if (Path.GetFileName(file).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase) &&
            ProductCode().Match(arguments) is { Success: true } code)
            return ("msiexec.exe", $"/x {code.Value}");
        return (file, arguments);
    }

    /// <summary>
    /// A non-MSI entry whose uninstaller file is gone, typically because the program folder was deleted by hand. Only a
    /// file Windows reports as not there counts: an uninstaller in a folder this account may not open (Intel's package
    /// cache, for example) is still in place.
    /// </summary>
    public static bool IsUninstallerMissing(InstalledApp app, Func<string, bool>? fileExists = null)
    {
        if (app.IsWindowsInstaller) return false;
        var (file, _) = Parse(app.UninstallString, fileExists ?? File.Exists);
        if (!Path.IsPathRooted(file)) return false;
        return fileExists is null ? PathProbe.State(file) == PathState.Missing : !fileExists(file);
    }
}

public sealed record UninstallRunResult(bool Started, int? ExitCode, string Message);

public static class Uninstaller
{
    private const int ErrorCancelled = 1223;

    /// <summary>Runs the program's own uninstaller and waits for it. The uninstaller may ask for elevation itself.</summary>
    public static async Task<UninstallRunResult> RunAsync(InstalledApp app, FileLog? log = null, CancellationToken ct = default)
    {
        var (file, arguments) = UninstallCommand.Parse(app.UninstallString);
        var startInfo = new ProcessStartInfo(file, arguments) { UseShellExecute = true };
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new UninstallRunResult(false, null, "無法啟動解除安裝程式");
            log?.Info($"執行解除安裝：{app.DisplayName}（{file} {arguments}）");
            await process.WaitForExitAsync(ct);
            return new UninstallRunResult(true, process.ExitCode, "解除安裝程式已結束");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new UninstallRunResult(false, null, "已取消（未同意權限提升）");
        }
        catch (Win32Exception ex)
        {
            log?.Error($"無法啟動解除安裝程式：{app.DisplayName}", ex);
            return new UninstallRunResult(false, null, $"無法啟動解除安裝程式：{UserMessage.For(ex)}");
        }
    }
}
