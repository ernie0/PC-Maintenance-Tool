using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Processes;

/// <summary>
/// One process at one moment. <see cref="PrivateWorkingSet"/> is Task Manager's "Memory" column; times are in
/// 100-nanosecond units, and <see cref="CreateTime"/> together with <see cref="Id"/> identifies the process even
/// after Windows reuses the id.
/// </summary>
public sealed record ProcessSample(
    int Id, string ImageName, int SessionId, long PrivateWorkingSet, long WorkingSet, long CreateTime, long CpuTime, int Threads, int Handles)
{
    public (int Id, long CreateTime) Key => (Id, CreateTime);
}

public static class ProcessSnapshot
{
    // SYSTEM_PROCESS_INFORMATION field offsets on 64-bit Windows; the app ships as win-x64 only.
    private const int NextEntryOffset = 0x00;
    private const int NumberOfThreadsOffset = 0x04;
    private const int WorkingSetPrivateSizeOffset = 0x08;
    private const int CreateTimeOffset = 0x20;
    private const int UserTimeOffset = 0x28;
    private const int KernelTimeOffset = 0x30;
    private const int ImageNameLengthOffset = 0x38;
    private const int ImageNameBufferOffset = 0x40;
    private const int UniqueProcessIdOffset = 0x50;
    private const int HandleCountOffset = 0x60;
    private const int SessionIdOffset = 0x64;
    private const int WorkingSetSizeOffset = 0x90;

    /// <summary>Every process at once, read without opening any of them, so it works without elevation.</summary>
    public static IReadOnlyList<ProcessSample> ReadAll()
    {
        var size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessInformation, buffer, size, out var needed);
                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, needed + (64 << 10));
                    continue;
                }
                if (status != 0) throw new InvalidOperationException($"無法讀取處理程序資訊（0x{status:X8}）");
                return Parse(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static List<ProcessSample> Parse(IntPtr buffer)
    {
        var processes = new List<ProcessSample>();
        var entry = buffer;
        while (true)
        {
            var nameBuffer = Marshal.ReadIntPtr(entry, ImageNameBufferOffset);
            var nameLength = (ushort)Marshal.ReadInt16(entry, ImageNameLengthOffset);
            processes.Add(new ProcessSample(
                (int)Marshal.ReadInt64(entry, UniqueProcessIdOffset),
                nameBuffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(nameBuffer, nameLength / 2),
                Marshal.ReadInt32(entry, SessionIdOffset),
                Marshal.ReadInt64(entry, WorkingSetPrivateSizeOffset),
                Marshal.ReadInt64(entry, WorkingSetSizeOffset),
                Marshal.ReadInt64(entry, CreateTimeOffset),
                Marshal.ReadInt64(entry, UserTimeOffset) + Marshal.ReadInt64(entry, KernelTimeOffset),
                Marshal.ReadInt32(entry, NumberOfThreadsOffset),
                Marshal.ReadInt32(entry, HandleCountOffset)));
            var next = Marshal.ReadInt32(entry, NextEntryOffset);
            if (next == 0) return processes;
            entry += next;
        }
    }

    /// <summary>Full path of a process this user is allowed to query; null for protected and system processes.</summary>
    public static string? ExecutablePath(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var builder = new StringBuilder(1024);
            var size = (uint)builder.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(handle, 0, builder, ref size) ? builder.ToString(0, (int)size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>The program's own description ("Google Chrome"), or null when it has none or cannot be read.</summary>
    public static string? Describe(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return null;
        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).FileDescription?.Trim() is { Length: > 0 } description ? description : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
