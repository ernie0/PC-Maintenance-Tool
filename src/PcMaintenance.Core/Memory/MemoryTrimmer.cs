using PcMaintenance.Core.Common;
using PcMaintenance.Core.Processes;

namespace PcMaintenance.Core.Memory;

public sealed record TrimResult(int Trimmed, int Skipped, ulong AvailableBefore, ulong AvailableAfter)
{
    public long Gained => (long)AvailableAfter - (long)AvailableBefore;
}

/// <summary>
/// Asks Windows to take back the memory programs are not using at the moment (EmptyWorkingSet). Nothing is
/// closed or lost; a program reloads what it needs, so switching back to it may take a moment.
/// </summary>
public static class MemoryTrimmer
{
    // Trimming these makes the desktop itself stutter while it pages back in.
    private static readonly HashSet<string> Skip = new(["dwm.exe", "csrss.exe", "winlogon.exe", "fontdrvhost.exe", "LogonUI.exe"], StringComparer.OrdinalIgnoreCase);

    public static bool Trim(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_QUOTA, false, processId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return NativeMethods.K32EmptyWorkingSet(handle);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Trims the programs in one sign-in session only, never services or other users' sessions.</summary>
    public static TrimResult TrimSession(int sessionId, FileLog? log = null)
    {
        var before = MemoryInfo.Read().AvailablePhysical;
        int trimmed = 0, skipped = 0;
        foreach (var process in ProcessSnapshot.ReadAll().Where(p => p.SessionId == sessionId && p.Id > 4 && !Skip.Contains(p.ImageName)))
        {
            if (Trim(process.Id)) trimmed++;
            else skipped++;
        }
        // Dirty pages reach the standby list only after they are written out, so give that a moment.
        Thread.Sleep(TimeSpan.FromSeconds(1));
        var result = new TrimResult(trimmed, skipped, before, MemoryInfo.Read().AvailablePhysical);
        log?.Info($"釋放記憶體：整理 {trimmed} 個程式（略過 {skipped} 個），可用記憶體 {ByteSize.Format((long)before)} → {ByteSize.Format((long)result.AvailableAfter)}");
        return result;
    }
}
