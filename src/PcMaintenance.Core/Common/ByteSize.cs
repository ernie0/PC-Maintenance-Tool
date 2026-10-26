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
}
