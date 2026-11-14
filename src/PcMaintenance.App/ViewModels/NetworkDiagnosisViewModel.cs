using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Core.Network;

namespace PcMaintenance.App.ViewModels;

/// <summary>One line of the network check as shown: a coloured mark, the finding and, when there is one, what to do.</summary>
public sealed class DiagnosisRow(DiagnosisStep step)
{
    private static readonly Dictionary<DiagnosisResult, (string Glyph, string Text, Color Tone)> Looks = new()
    {
        [DiagnosisResult.Pass] = ("", "正常", Color.FromRgb(0x05, 0x96, 0x69)),
        [DiagnosisResult.Info] = ("", "說明", Color.FromRgb(0x25, 0x63, 0xEB)),
        [DiagnosisResult.Warning] = ("", "注意", Color.FromRgb(0xB4, 0x53, 0x09)),
        [DiagnosisResult.Fail] = ("", "有問題", Color.FromRgb(0xDC, 0x26, 0x26)),
        [DiagnosisResult.Skipped] = ("", "略過", Color.FromRgb(0x6B, 0x72, 0x80)),
    };

    private static readonly Dictionary<DiagnosisResult, (Brush Tone, Brush Soft)> Brushes = Looks.ToDictionary(kv => kv.Key, kv =>
    {
        var tone = new SolidColorBrush(kv.Value.Tone);
        var soft = new SolidColorBrush(Color.FromArgb(0x1F, kv.Value.Tone.R, kv.Value.Tone.G, kv.Value.Tone.B));
        tone.Freeze();
        soft.Freeze();
        return ((Brush)tone, (Brush)soft);
    });

    public DiagnosisStep Step { get; } = step;
    public string Title => Step.Title;
    public string Detail => Step.Detail;
    public string? NextStep => Step.NextStep;
    public string Glyph => Looks[Step.Result].Glyph;
    public string ResultText => Looks[Step.Result].Text;
    public Brush ToneBrush => Brushes[Step.Result].Tone;
    public Brush SoftBrush => Brushes[Step.Result].Soft;
}

/// <summary>The "網路診斷" view of the network tab: runs <see cref="NetworkDiagnosis"/> on request and lists what it found.</summary>
public sealed partial class NetworkDiagnosisViewModel(AppServices services) : ObservableObject
{
    public const string Explanation =
        "只在按下「開始診斷」時檢查：會 ping 預設閘道、查詢 DNS，並連到 Microsoft 的連線測試網址與時間伺服器。";

    public ObservableCollection<DiagnosisRow> Steps { get; } = [];

    [ObservableProperty]
    private string _summaryText = "按「開始診斷」，依序檢查網路卡、IP 位址、預設閘道、DNS、能不能上網與電腦時間。";

    [ObservableProperty]
    private bool _hasRun;

    [RelayCommand]
    private async Task RunAsync()
    {
        Steps.Clear();
        HasRun = true;
        SummaryText = "正在檢查…";
        var progress = new Progress<DiagnosisStep>(step => Steps.Add(new DiagnosisRow(step)));
        var steps = await Task.Run(() => new NetworkDiagnosis().RunAsync(progress));
        var problems = steps.Count(s => s.Result == DiagnosisResult.Fail);
        var warnings = steps.Count(s => s.Result == DiagnosisResult.Warning);
        SummaryText = (problems, warnings) switch
        {
            (0, 0) => $"檢查完成（{DateTime.Now:HH:mm}）：網路正常。",
            (0, _) => $"檢查完成（{DateTime.Now:HH:mm}）：{warnings} 項需要注意，請看各項的建議。",
            _ => $"檢查完成（{DateTime.Now:HH:mm}）：{problems} 項有問題" + (warnings > 0 ? $"、{warnings} 項需要注意" : "") + "，請從第一個有問題的項目開始處理。",
        };
        services.Log.Info($"網路診斷：{SummaryText}{Environment.NewLine}" +
                          string.Join(Environment.NewLine, steps.Select(s => $"  {s.Title}：{s.Result} {s.Detail}")));
    }
}
