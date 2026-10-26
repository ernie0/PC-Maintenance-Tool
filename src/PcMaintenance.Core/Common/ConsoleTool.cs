using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PcMaintenance.Core.Common;

/// <summary>Runs a Windows console tool from System32 without a window and collects what it prints.</summary>
public static class ConsoleTool
{
    /// <summary>These tools write in the console's OEM code page (Big5 on Traditional Chinese Windows) when redirected.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string tool, string arguments, CancellationToken ct = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"無法啟動 {tool}");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var output = (await stdout + await stderr).Replace("\r\n", "\n").Replace('\r', '\n');
        return (process.ExitCode, output);
    }
}
