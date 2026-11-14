using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Junk;

/// <summary>
/// Runs one handler of Windows' own Disk Cleanup (cleanmgr /sagerun) for folders that only Windows should remove, such as
/// a previous installation: those are owned by TrustedInstaller and hold junctions into the current C:\Users, so taking
/// them over file by file is neither possible nor safe. Only the given handler is selected for the run.
/// </summary>
public static class WindowsDiskCleanup
{
    private const string HandlersKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
    // A profile number of our own, so the user's own cleanmgr /sageset choices are left alone.
    private const int Profile = 4242;
    private static readonly string Flag = $"StateFlags{Profile:D4}";
    private static readonly TimeSpan MaxRun = TimeSpan.FromHours(1);

    /// <returns>Null when Disk Cleanup ran, otherwise why it could not.</returns>
    public static string? Run(string handler, FileLog? log = null)
    {
        RegistryKey? selected = null;
        try
        {
            using var handlers = Registry.LocalMachine.OpenSubKey(HandlersKey, writable: true);
            if (handlers is null) return "這台電腦沒有 Windows 磁碟清理";
            // A run that was cut short could have left other handlers selected under our profile.
            foreach (var name in handlers.GetSubKeyNames())
            {
                using var other = handlers.OpenSubKey(name, writable: true);
                other?.DeleteValue(Flag, throwOnMissingValue: false);
            }
            selected = handlers.OpenSubKey(handler, writable: true);
            if (selected is null) return "這台電腦的 Windows 磁碟清理沒有這個項目";
            selected.SetValue(Flag, 2, RegistryValueKind.DWord);

            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), $"/sagerun:{Profile}")
            {
                UseShellExecute = false,
            });
            if (process is null) return "無法啟動 Windows 磁碟清理";
            if (!process.WaitForExit(MaxRun)) return "Windows 磁碟清理超過一小時還沒完成，請稍後重新掃描查看結果";
            log?.Info($"Windows 磁碟清理「{handler}」結束，代碼 {process.ExitCode}");
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            log?.Error($"Windows 磁碟清理「{handler}」", ex);
            return $"無法執行 Windows 磁碟清理：{ex.Message}";
        }
        finally
        {
            try
            {
                selected?.DeleteValue(Flag, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
            selected?.Dispose();
        }
    }
}
