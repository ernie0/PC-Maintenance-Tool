namespace PcMaintenance.Core.Common;

/// <summary>Daily text log. Logging must never break a cleaning run, so write failures are ignored.</summary>
public sealed class FileLog(string directory)
{
    private readonly Lock _gate = new();

    public string Directory { get; } = directory;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}：{ex.Message}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var file = Path.Combine(Directory, $"{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
