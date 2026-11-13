using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Network;
using PcMaintenance.Core.Processes;

namespace PcMaintenance.App.ViewModels;

/// <summary>
/// The "program traffic" view of the network tab: how fast each program downloads and uploads right now, and how
/// much since the view was opened. Counting runs only while the view is on screen, and needs elevation.
/// </summary>
public sealed partial class ProgramTrafficViewModel : ObservableObject
{
    private const int HistoryLength = 60;
    private const double ChartFloor = 100_000;

    private readonly AppServices _services;
    private readonly RateHistory _totalDownload = new(HistoryLength);
    private readonly RateHistory _totalUpload = new(HistoryLength);
    private readonly Dictionary<string, ProgramTrafficRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    // A process that ended keeps counting under the program it was.
    private readonly Dictionary<int, (string Name, string? Path)> _programOf = [];
    private Dictionary<int, ProcessTraffic> _previous = [];
    private TrafficMonitor? _monitor;
    private DateTime _startedAt;
    private long _previousTimestamp;
    private string _search = "";

    public ProgramTrafficViewModel(AppServices services)
    {
        _services = services;
        ProgramView = new ListCollectionView(Programs) { Filter = item => item is ProgramTrafficRow row && row.Matches(_search) };
        ProgramView.SortDescriptions.Add(new SortDescription(nameof(ProgramTrafficRow.TotalRate), ListSortDirection.Descending));
        ProgramView.SortDescriptions.Add(new SortDescription(nameof(ProgramTrafficRow.TotalBytes), ListSortDirection.Descending));
        ProgramView.IsLiveSorting = true;
        ProgramView.LiveSortingProperties.Add(nameof(ProgramTrafficRow.TotalRate));
        ProgramView.LiveSortingProperties.Add(nameof(ProgramTrafficRow.TotalBytes));
    }

    public ObservableCollection<ProgramTrafficRow> Programs { get; } = [];
    public ListCollectionView ProgramView { get; }
    public PerfResource Download { get; } = new("download", "下載", HistoryLength);
    public PerfResource Upload { get; } = new("upload", "上傳", HistoryLength);

    [ObservableProperty]
    private ProgramTrafficRow? _selectedProgram;

    [ObservableProperty]
    private string _chartTitle = "全部程式";

    public bool IsRunning => _monitor?.IsRunning == true;

    partial void OnSelectedProgramChanged(ProgramTrafficRow? value) => ShowCharts();

    /// <summary>Starts counting; returns the reason when it cannot (not elevated, or Windows refused).</summary>
    public string? Start()
    {
        if (_monitor is not null) return null;
        if (!_services.IsElevated) return "要看每個程式用了多少流量，需要系統管理員權限：請按畫面上方的「以系統管理員重新啟動」。";
        try
        {
            _monitor = TrafficMonitor.Start();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception)
        {
            _services.Log.Warn($"無法開始統計網路流量：{UserMessage.For(ex)}");
            return $"無法開始統計網路流量：{UserMessage.For(ex)}";
        }
        _startedAt = DateTime.Now;
        _previousTimestamp = Stopwatch.GetTimestamp();
        _previous = [];
        _programOf.Clear();
        _rows.Clear();
        Programs.Clear();
        _totalDownload.Clear();
        _totalUpload.Clear();
        ShowCharts();
        return null;
    }

    public void Stop()
    {
        _monitor?.Dispose();
        _monitor = null;
    }

    public string StatusText => _monitor switch
    {
        null => "",
        { IsRunning: false } => "流量統計已被停止（可能另一個電腦維護工具視窗也在統計），請切換到別的畫面再回來。",
        _ => $"自 {_startedAt:HH:mm:ss} 起統計，不含電腦內部的流量；離開這個畫面就停止統計。",
    };

    /// <summary>The traffic counted so far by process; read on a worker thread.</summary>
    public IReadOnlyDictionary<int, ProcessTraffic>? Snapshot() => _monitor?.Snapshot();

    public void ApplySearch(string text)
    {
        _search = text.Trim();
        ProgramView.Refresh();
    }

    /// <summary>Turns per-process byte counts into per-program speeds and totals; called every 2 seconds on the UI thread.</summary>
    public void Apply(IReadOnlyDictionary<int, ProcessTraffic> counts, IReadOnlyDictionary<int, (string Name, string? Path)> identities)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.5, Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalSeconds);
        _previousTimestamp = now;
        foreach (var (pid, identity) in identities) _programOf[pid] = identity;

        var byProgram = new Dictionary<string, (string Name, string? Path, long Sent, long Received, long SentNow, long ReceivedNow)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, traffic) in counts)
        {
            var (name, path) = _programOf.TryGetValue(pid, out var known) ? known
                : pid is 0 or 4 ? ("Windows 系統", null) : ($"處理程序 {pid}（已結束）", null);
            var before = _previous.GetValueOrDefault(pid);
            var key = path ?? name;
            var sum = byProgram.GetValueOrDefault(key, (name, path, 0, 0, 0, 0));
            byProgram[key] = (name, path, sum.Sent + traffic.Sent, sum.Received + traffic.Received,
                sum.SentNow + traffic.Sent - before.Sent, sum.ReceivedNow + traffic.Received - before.Received);
        }
        _previous = counts.ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var (key, program) in byProgram)
        {
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new ProgramTrafficRow(program.Name, program.Path, HistoryLength);
                _rows[key] = row;
                Programs.Add(row);
            }
            row.Update(program.ReceivedNow * 8 / seconds, program.SentNow * 8 / seconds, program.Received, program.Sent);
        }
        // Programs quiet this round still move their chart along.
        foreach (var row in _rows.Values.Where(r => !byProgram.ContainsKey(r.Key))) row.Update(0, 0, row.Downloaded, row.Uploaded);

        _totalDownload.Push(_rows.Values.Sum(r => r.DownloadRate));
        _totalUpload.Push(_rows.Values.Sum(r => r.UploadRate));
        ShowCharts();
        OnPropertyChanged(nameof(StatusText));
    }

    public string SummaryText =>
        $"程式 {Programs.Count:N0} 個：下載 {ByteSize.FormatBitsPerSecond(_totalDownload.Last)}、上傳 {ByteSize.FormatBitsPerSecond(_totalUpload.Last)}";

    private void ShowCharts()
    {
        var (download, upload) = SelectedProgram is { } row ? (row.DownloadHistory, row.UploadHistory) : (_totalDownload, _totalUpload);
        ChartTitle = SelectedProgram?.Name ?? "全部程式";
        Show(Download, "下載", download);
        Show(Upload, "上傳", upload);
        OnPropertyChanged(nameof(SummaryText));
    }

    private static void Show(PerfResource chart, string label, RateHistory history)
    {
        chart.History = history.Values;
        chart.Maximum = Math.Max(history.Values.DefaultIfEmpty().Max() * 1.25, ChartFloor);
        chart.ScaleText = ByteSize.FormatBitsPerSecond(chart.Maximum);
        chart.Summary = $"{label} {ByteSize.FormatBitsPerSecond(history.Last)}";
    }
}

/// <summary>The last readings of a speed, oldest first.</summary>
public sealed class RateHistory(int capacity)
{
    private readonly List<double> _values = [];

    public double[] Values => [.. _values];
    public double Last => _values.Count > 0 ? _values[^1] : 0;

    public void Push(double value)
    {
        _values.Add(value);
        if (_values.Count > capacity) _values.RemoveAt(0);
    }

    public void Clear() => _values.Clear();
}

public sealed partial class ProgramTrafficRow(string name, string? path, int historyLength) : ObservableObject
{
    public string Key => Path ?? Name;
    public string Name { get; } = name;
    public string? Path { get; } = path;
    public string Tooltip => Path ?? Name;
    public RateHistory DownloadHistory { get; } = new(historyLength);
    public RateHistory UploadHistory { get; } = new(historyLength);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadRateText), nameof(TotalRate))]
    private double _downloadRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadRateText), nameof(TotalRate))]
    private double _uploadRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadedText), nameof(TotalBytes))]
    private long _downloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadedText), nameof(TotalBytes))]
    private long _uploaded;

    public double TotalRate => DownloadRate + UploadRate;
    public long TotalBytes => Downloaded + Uploaded;
    public string DownloadRateText => ByteSize.FormatBitsPerSecond(DownloadRate);
    public string UploadRateText => ByteSize.FormatBitsPerSecond(UploadRate);
    public string DownloadedText => ByteSize.Format(Downloaded);
    public string UploadedText => ByteSize.Format(Uploaded);

    public void Update(double downloadRate, double uploadRate, long downloaded, long uploaded)
    {
        DownloadRate = downloadRate;
        UploadRate = uploadRate;
        Downloaded = downloaded;
        Uploaded = uploaded;
        DownloadHistory.Push(downloadRate);
        UploadHistory.Push(uploadRate);
    }

    public bool Matches(string text) => text.Length == 0 || Name.Contains(text, StringComparison.CurrentCultureIgnoreCase);
}
