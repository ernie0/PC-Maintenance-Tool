using System.ComponentModel;
using System.Runtime.InteropServices;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Memory;

/// <summary>Physical memory and commit charge (memory plus page file) as Windows reports them.</summary>
public sealed record MemoryStatus(ulong TotalPhysical, ulong AvailablePhysical, ulong CommitLimit, ulong CommitAvailable, ulong InstalledPhysical)
{
    public ulong UsedPhysical => TotalPhysical - AvailablePhysical;
    public ulong CommitUsed => CommitLimit - CommitAvailable;
    public double UsedPercent => TotalPhysical == 0 ? 0 : 100.0 * UsedPhysical / TotalPhysical;
}

public static class MemoryInfo
{
    public static MemoryStatus Read()
    {
        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Installed can exceed what Windows may use (memory reserved for hardware and integrated graphics).
        var installed = NativeMethods.GetPhysicallyInstalledSystemMemory(out var kilobytes) ? kilobytes * 1024 : status.ullTotalPhys;
        return new MemoryStatus(status.ullTotalPhys, status.ullAvailPhys, status.ullTotalPageFile, status.ullAvailPageFile, installed);
    }
}
