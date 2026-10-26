using System.Runtime.InteropServices;

namespace PcMaintenance.Core.Common;

public static class RecycleBin
{
    private const int EUnexpected = unchecked((int)0x8000FFFF);

    public static (long Bytes, long Items) Query()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        return NativeMethods.SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public static bool Empty()
    {
        var hr = NativeMethods.SHEmptyRecycleBinW(
            IntPtr.Zero, null,
            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
        // The shell reports E_UNEXPECTED when the bin is already empty.
        return hr == 0 || (hr == EUnexpected && Query().Items == 0);
    }

    /// <summary>
    /// Moves a file or folder to the Recycle Bin so the user can undo. On drives without a
    /// Recycle Bin (network shares, some removable media) the shell deletes permanently instead.
    /// </summary>
    public static bool Send(string path)
    {
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = path + '\0',
            fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION |
                              NativeMethods.FOF_NOERRORUI | NativeMethods.FOF_SILENT),
        };
        var rc = NativeMethods.SHFileOperationW(ref op);
        return rc == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }
}
