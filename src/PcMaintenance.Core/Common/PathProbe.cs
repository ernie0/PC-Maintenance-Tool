using System.Runtime.InteropServices;

namespace PcMaintenance.Core.Common;

public enum PathState
{
    Exists,
    Missing,
    /// <summary>Windows would not say, usually because this account may not look inside the folder.</summary>
    Unknown,
}

/// <summary>
/// Whether a file or folder is really gone. File.Exists also answers false when access is denied, which would turn an
/// uninstaller in a protected folder (Intel's package cache, for example) into a "missing" one.
/// </summary>
public static class PathProbe
{
    public static PathState State(string path)
    {
        if (NativeMethods.GetFileAttributesW(path) != NativeMethods.INVALID_FILE_ATTRIBUTES) return PathState.Exists;
        return Marshal.GetLastPInvokeError() is NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_PATH_NOT_FOUND
            ? PathState.Missing
            : PathState.Unknown;
    }
}
