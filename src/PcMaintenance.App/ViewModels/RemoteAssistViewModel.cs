using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcMaintenance.App.Services;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Viewer.Net;
using Vortice.MediaFoundation;

namespace PcMaintenance.App.ViewModels;

/// <summary>
/// 遠端協助分頁：連到員工或工地電腦，看畫面並協助操作。沿用已驗證的 <see cref="ViewerClient"/>
/// （解碼與收送），這裡只管連線狀態與畫面上的操作。實際畫面繪製與滑鼠鍵盤擷取在 View 的程式碼後置，
/// 因為那綁在畫面元件上。兩種連法：經 NAS 伺服器挑一台線上電腦，或直接填區網位址。
/// </summary>
public sealed partial class RemoteAssistViewModel : PageViewModel
{
    public RemoteAssistViewModel(AppServices services) => _services = services;

    private readonly AppServices _services;
    private static bool _mediaFoundationStarted;

    public override string Title => "遠端協助";
    public override string Subtitle => "連到員工或工地電腦，看畫面並協助操作";

    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _adminName = Environment.UserName;
    [ObservableProperty] private string _directHost = "";
    [ObservableProperty] private string _directPort = "18010";
    [ObservableProperty] private string _status = "尚未連線";
    [ObservableProperty] private string _verificationCode = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private AgentSummary? _selectedAgent;
    [ObservableProperty] private MonitorDescriptor? _selectedMonitor;

    public ObservableCollection<AgentSummary> Agents { get; } = [];
    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];

    /// <summary>目前連線（已連時非 null），View 用它掛畫面與輸入。</summary>
    public ViewerClient? Client { get; private set; }

    /// <summary>連線建立、Client 可用時觸發（View 掛畫面與輸入）。</summary>
    public event Action? ClientConnected;
    /// <summary>連線結束、Client 要收掉時觸發（View 卸載畫面與輸入）。</summary>
    public event Action? ClientDisconnected;

    private CancellationTokenSource? _cts;

    [RelayCommand]
    private async Task RefreshAgentsAsync()
    {
        if (!TryServerUri(out var uri)) { Status = "伺服器網址格式錯誤（例：https://remote.example.com）"; return; }
        try
        {
            var list = await ViewerClient.ListAgentsAsync(uri);
            Agents.Clear();
            foreach (var a in list) Agents.Add(a);
            SelectedAgent = Agents.FirstOrDefault();
            Status = $"線上電腦 {Agents.Count} 台";
        }
        catch (Exception ex) { Status = "查詢失敗：" + ex.Message; }
    }

    [RelayCommand]
    private void ConnectServer()
    {
        if (!TryServerUri(out var uri)) { Status = "伺服器網址格式錯誤"; return; }
        if (SelectedAgent is null) { Status = "請先選一台線上電腦"; return; }
        var agent = SelectedAgent;
        Start(client => client.RunViaServerAsync(uri, agent.AgentId, AdminName.Trim(), ct: _cts!.Token));
    }

    [RelayCommand]
    private void ConnectDirect()
    {
        if (string.IsNullOrWhiteSpace(DirectHost)) { Status = "請填被控端位址"; return; }
        if (!int.TryParse(DirectPort, out int port)) { Status = "port 格式錯誤"; return; }
        var host = DirectHost.Trim();
        Start(client => client.RunAsync(host, port, _cts!.Token));
    }

    [RelayCommand]
    private void Disconnect() => _cts?.Cancel();

    private void Start(Func<ViewerClient, Task> run)
    {
        Disconnect();
        EnsureMediaFoundation();
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsConnected = true;
        _ = ReconnectLoopAsync(run, cts.Token);
    }

    /// <summary>連線並在非使用者主動中斷的斷線時自動重連（約每 2 秒一次），連成功會把重試次數歸零；被拒則不重連。</summary>
    private async Task ReconnectLoopAsync(Func<ViewerClient, Task> run, CancellationToken ct)
    {
        int attempt = 0;
        const int maxAttempts = 20;
        while (!ct.IsCancellationRequested)
        {
            var client = new ViewerClient();
            bool gotCode = false;
            client.VerificationCodeReady += code => { gotCode = true; Ui(() => VerificationCode = code); };
            client.MonitorListReceived += list => Ui(() =>
            {
                Monitors.Clear();
                foreach (var m in list) Monitors.Add(m);
                if (Monitors.Count > 0 && SelectedMonitor is null) SelectedMonitor = Monitors[0];
            });
            Client = client;
            Ui(() =>
            {
                VerificationCode = "";
                Status = attempt == 0 ? "連線中…" : $"重連中…（{attempt}）";
                ClientConnected?.Invoke();
            });

            try { await run(client).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SessionRejectedException ex) { Ui(() => Status = ex.Message); break; } // 被拒不重連
            catch (Exception ex) { Ui(() => Status = "連線失敗：" + ex.Message); }
            finally
            {
                Ui(() => ClientDisconnected?.Invoke());
                client.Dispose();
                Client = null;
                Ui(() => { Monitors.Clear(); SelectedMonitor = null; });
            }

            if (ct.IsCancellationRequested) break;
            attempt = gotCode ? 0 : attempt + 1;
            if (attempt >= maxAttempts) { Ui(() => Status = "多次重連失敗，請重新連線"); break; }
            Ui(() => Status = $"連線中斷，2 秒後重連…（{attempt}）");
            try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
        }
        Ui(() => { IsConnected = false; if (ct.IsCancellationRequested) Status = "已中斷"; });
    }

    partial void OnSelectedMonitorChanged(MonitorDescriptor? value)
    {
        if (value is not null && Client is not null) _ = Client.SwitchMonitorAsync(value.Index);
    }

    public override void OnNavigatedFrom() { } // 切頁不中斷連線，維持在背景

    private bool TryServerUri(out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(ServerUrl?.Trim(), UriKind.Absolute, out uri!)
            && uri.Scheme is "http" or "https";
    }

    private static void EnsureMediaFoundation()
    {
        if (_mediaFoundationStarted) return;
        MediaFactory.MFStartup(false);
        _mediaFoundationStarted = true;
    }

    private static void Ui(Action action) => Application.Current?.Dispatcher.Invoke(action);
}
