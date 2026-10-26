using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PcMaintenance.Core.Shortcuts;

/// <summary>What a .lnk file points at: the expanded path, the path as stored (it may hold %variables%), and whether
/// it is a Windows Installer "advertised" shortcut, whose target is resolved by the installer instead.</summary>
internal sealed record ShellLinkInfo(string Path, string RawPath, bool IsAdvertised);

/// <summary>Reads and writes shortcuts through the shell's own ShellLink object.</summary>
internal static class ShellLink
{
    private const uint SlgpRawPath = 0x4;
    private const uint SldfHasDarwinId = 0x1000;
    private const int StgmRead = 0;
    private const int BufferLength = 1024;

    public static ShellLinkInfo? TryRead(string file)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((IPersistFile)link).Load(file, StgmRead);
            var shellLink = (IShellLinkW)link;
            var path = new StringBuilder(BufferLength);
            shellLink.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var raw = new StringBuilder(BufferLength);
            shellLink.GetPath(raw, raw.Capacity, IntPtr.Zero, SlgpRawPath);
            ((IShellLinkDataList)link).GetFlags(out var flags);
            return new ShellLinkInfo(path.ToString(), raw.ToString(), (flags & SldfHasDarwinId) != 0);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
        }
    }

    public static void Create(string file, string target)
    {
        var link = new ShellLinkObject();
        try
        {
            ((IShellLinkW)link).SetPath(target);
            ((IPersistFile)link).Save(file, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1")]
    private interface IShellLinkDataList
    {
        void AddDataBlock(IntPtr pDataBlock);
        void CopyDataBlock(uint dwSig, out IntPtr ppDataBlock);
        void RemoveDataBlock(uint dwSig);
        void GetFlags(out uint pdwFlags);
        void SetFlags(uint dwFlags);
    }
}
