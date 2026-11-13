using System.Globalization;

namespace PcMaintenance.Core.Common;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 ? "0" : "0.0";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    /// <summary>Network speed the way Task Manager and internet providers state it: 12.3 Mbps.</summary>
    public static string FormatBitsPerSecond(double bitsPerSecond) => bitsPerSecond switch
    {
        < 1_000 => $"{bitsPerSecond.ToString("0", CultureInfo.InvariantCulture)} bps",
        < 1_000_000 => $"{(bitsPerSecond / 1_000).ToString("0.#", CultureInfo.InvariantCulture)} Kbps",
        < 1_000_000_000 => $"{(bitsPerSecond / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture)} Mbps",
        _ => $"{(bitsPerSecond / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture)} Gbps",
    };
}
