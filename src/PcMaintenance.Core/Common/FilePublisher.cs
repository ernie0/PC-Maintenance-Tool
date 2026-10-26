using System.Diagnostics;
using System.Text;

namespace PcMaintenance.Core.Common;

public static class FilePublisher
{
    public static string Get(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
        try
        {
            return FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    public static bool IsMicrosoft(string publisher) =>
        publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resolves "@%SystemRoot%\system32\foo.dll,-101" style registry strings to display text.</summary>
public static class IndirectString
{
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '@') return value;
        var buffer = new StringBuilder(1024);
        if (NativeMethods.SHLoadIndirectString(value, buffer, buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0 && buffer[0] != '@')
            return buffer.ToString();
        // Driver-package services ("@oem12.inf,%Key%;Default name") carry their display text after the last ';'.
        var semicolon = value.LastIndexOf(';');
        return semicolon >= 0 && semicolon < value.Length - 1 ? value[(semicolon + 1)..] : value;
    }
}
