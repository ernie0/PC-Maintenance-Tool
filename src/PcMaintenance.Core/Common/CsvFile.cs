using System.Text;

namespace PcMaintenance.Core.Common;

/// <summary>CSV that Excel opens correctly: UTF-8 with BOM for Chinese text, CRLF line ends, RFC 4180 quoting.</summary>
public static class CsvFile
{
    public static void Write(string path, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        AppendRow(builder, header);
        foreach (var row in rows) AppendRow(builder, row);
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string Escape(string value)
    {
        // Excel would evaluate a cell starting with these as a formula.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> row) =>
        builder.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
}
