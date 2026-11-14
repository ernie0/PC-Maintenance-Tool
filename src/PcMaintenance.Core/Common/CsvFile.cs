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

    /// <summary>
    /// Reads a CSV written by <see cref="Write"/> (or saved by Excel): quoted fields may hold commas, quotes and line
    /// breaks; the apostrophe <see cref="Escape"/> puts before a formula-like value is taken off again.
    /// </summary>
    public static List<string[]> Read(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        void EndField()
        {
            var value = field.ToString();
            if (value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' or '\t' or '\r') value = value[1..];
            row.Add(value);
            field.Clear();
            fieldStarted = false;
        }
        void EndRow()
        {
            EndField();
            if (row.Count > 1 || row[0].Length > 0) rows.Add([.. row]);
            row.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') field.Append(text[++i]);
                else quoted = false;
                continue;
            }
            switch (c)
            {
                case '"' when !fieldStarted:
                    quoted = true;
                    fieldStarted = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> row) =>
        builder.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
}
