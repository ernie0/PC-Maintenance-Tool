using System.Diagnostics;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Processes;

/// <summary>
/// Ending processes the way Task Manager's "End task" does, minus the ones whose loss crashes or logs off
/// Windows, and limited to the user's own sign-in session (services belong to the service list instead).
/// </summary>
public static class ProcessControl
{
    private static readonly HashSet<string> Critical = new(
    [
        "System", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "LsaIso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "LogonUI.exe", "explorer.exe", "sihost.exe",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Why a process may not be ended here, or null when it may.</summary>
    public static string? WhyNotEnd(ProcessSample process, int currentSession, int currentProcessId)
    {
        if (process.Id <= 4 || Critical.Contains(process.ImageName)) return "這是 Windows 必要的處理程序，結束後會當機或登出，不提供結束。";
        if (process.Id == currentProcessId) return "這是電腦維護工具本身。";
        if (process.SessionId != currentSession) return "這是系統服務或其他使用者的處理程序，請到「開機與背景」管理服務。";
        return null;
    }

    /// <summary>Terminates the process, checking first that the id still belongs to the same process.</summary>
    public static void End(ProcessSample process, FileLog? log = null)
    {
        using var target = Process.GetProcessById(process.Id);
        if (target.StartTime.ToFileTimeUtc() != process.CreateTime) throw new InvalidOperationException("這個程式已經結束了。");
        target.Kill();
        log?.Info($"結束工作：{process.ImageName}（PID {process.Id}）");
    }
}
