using System.Text.RegularExpressions;

namespace PcMaintenance.Core.Common;

public static partial class CommandLine
{
    [GeneratedRegex(@"\.(exe|com|bat|cmd)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutableEnd();

    /// <summary>
    /// Splits a Windows command line into the program and its arguments. Unquoted paths may contain
    /// spaces ("C:\Program Files\Foo\foo.exe /x"), so every prefix ending in an executable extension
    /// is tried until one exists.
    /// </summary>
    public static (string FileName, string Arguments) Split(string command, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        if (cmd.Length == 0) return ("", "");

        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            return end > 0 ? (cmd[1..end], cmd[(end + 1)..].Trim()) : (cmd.Trim('"'), "");
        }

        string? firstCandidate = null;
        foreach (Match m in ExecutableEnd().Matches(cmd))
        {
            var candidate = cmd[..(m.Index + m.Length)];
            firstCandidate ??= candidate;
            if (fileExists(candidate)) return (candidate, cmd[candidate.Length..].Trim());
        }
        if (firstCandidate is not null) return (firstCandidate, cmd[firstCandidate.Length..].Trim());

        var space = cmd.IndexOf(' ');
        return space < 0 ? (cmd, "") : (cmd[..space], cmd[(space + 1)..].Trim());
    }

    /// <summary>Best-effort path of the program a command line runs, used to look up its publisher.</summary>
    public static string? ExtractExecutable(string? command, SystemFolders? folders = null, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        fileExists ??= File.Exists;
        var windows = (folders ?? SystemFolders.Current).WindowsDir;
        var cmd = command.Trim();

        // Service ImagePath values use NT-style prefixes.
        if (cmd.StartsWith(@"\??\", StringComparison.Ordinal)) cmd = cmd[4..];
        if (cmd.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) cmd = Path.Combine(windows, cmd[12..]);
        else if (cmd.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase)) cmd = Path.Combine(windows, cmd);

        var (file, _) = Split(cmd, fileExists);
        if (file.Length == 0) return null;
        if (!Path.IsPathRooted(file))
        {
            var inSystem32 = Path.Combine(windows, "System32", file);
            if (fileExists(inSystem32)) return inSystem32;
        }
        return file;
    }
}
