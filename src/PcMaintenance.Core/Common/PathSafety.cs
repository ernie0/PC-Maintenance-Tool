namespace PcMaintenance.Core.Common;

public static class PathSafety
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsSameOrUnder(string root, string path)
    {
        try
        {
            var r = Normalize(root);
            var p = Normalize(path);
            if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
            return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the root or any folder between the root and the file is a junction or symbolic
    /// link, or cannot be inspected, or the file is not under the root at all. Deleting through such
    /// a path could reach outside the intended folder (for example a junction planted in %TEMP%
    /// pointing at System32 while the cleaner runs elevated), so callers must skip the file.
    /// </summary>
    public static bool HasReparsePointOnPath(string root, string filePath)
    {
        string r;
        string? dir;
        try
        {
            r = Normalize(root);
            dir = Path.GetDirectoryName(Normalize(filePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        while (dir is not null)
        {
            if (dir.Length < r.Length) return true;
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
            if (dir.Equals(r, StringComparison.OrdinalIgnoreCase)) return false;
            dir = Path.GetDirectoryName(dir);
        }
        return true;
    }

    /// <summary>OneDrive and other cloud placeholders report a size but use no local space; reading them triggers a download.</summary>
    public static bool IsCloudPlaceholder(FileAttributes attributes)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        return (attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) != 0;
    }
}
