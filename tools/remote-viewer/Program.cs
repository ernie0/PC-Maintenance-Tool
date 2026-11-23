// P1 控制端視窗：連到被控端、顯示遠端畫面、把本機滑鼠鍵盤送過去、下拉切換螢幕。
// 純程式碼 WPF（無 XAML），方便當作日後整併進主程式分頁的雛形。
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Viewer.Net;
using Vortice.MediaFoundation;
using CoreMouseButton = PcMaintenance.Remote.Core.Protocol.MouseButton;

internal static class Program
{
    [STAThread]
    private static void Main(string[] cmdArgs)
    {
        MediaFactory.MFStartup(false).CheckError();
        var app = new Application();
        var window = new ViewerWindow(cmdArgs);
        app.Run(window);
        try { MediaFactory.MFShutdown(); } catch { }
    }
}

internal sealed class ViewerWindow : Window
{
    private readonly TextBox _host = new() { Width = 130, Text = "127.0.0.1", VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _port = new() { Width = 60, Text = "18010", VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _connect = new() { Content = "連線", Width = 64, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBlock _code = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), FontWeight = FontWeights.Bold };
    private readonly ComboBox _monitors = new() { Width = 200, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stats = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly Image _screen = new() { Stretch = Stretch.Uniform, Focusable = true, SnapsToDevicePixels = true };
    private int _fpsFrames;

    private ViewerClient? _client;
    private CancellationTokenSource? _cts;
    private WriteableBitmap? _bitmap;
    private bool _switchingList;
    private string? _snapPath; private int _snapAfter = 60, _frameCount; private bool _snapped;
    private string? _clipLastSeen;

    public ViewerWindow(string[] cmdArgs)
    {
        Title = "遠端協助 — 控制端（P1）";
        Width = 1280; Height = 760;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));

        if (cmdArgs.Length >= 1) _host.Text = cmdArgs[0];
        if (cmdArgs.Length >= 2) _port.Text = cmdArgs[1];

        // 測試用：帶 --auto 時載入後自動按「連線」，方便本機重現。
        if (Array.IndexOf(cmdArgs, "--auto") >= 0)
            Loaded += async (_, _) => { await Task.Delay(500); ToggleConnect(); };

        // 測試用：--snap <png路徑> [第幾張] 把收到的第 N 張畫面存成 PNG（驗證游標合成）後關閉。
        int si = Array.IndexOf(cmdArgs, "--snap");
        if (si >= 0 && si + 1 < cmdArgs.Length)
        {
            _snapPath = cmdArgs[si + 1];
            if (si + 2 < cmdArgs.Length && int.TryParse(cmdArgs[si + 2], out int n)) _snapAfter = n;
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        bar.Children.Add(new TextBlock { Text = "位址", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        bar.Children.Add(_host);
        bar.Children.Add(new TextBlock { Text = "：", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_port);
        bar.Children.Add(_connect);
        bar.Children.Add(new TextBlock { Text = "驗證碼", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        bar.Children.Add(_code);
        bar.Children.Add(new TextBlock { Text = "螢幕", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        bar.Children.Add(_monitors);
        bar.Children.Add(_stats);

        // 每秒更新一次 FPS 與解析度
        var fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        fpsTimer.Tick += (_, _) =>
        {
            int fps = _fpsFrames; _fpsFrames = 0;
            _stats.Text = _client is null ? "" : $"FPS {fps}" + (_bitmap is not null ? $" · {_bitmap.PixelWidth}×{_bitmap.PixelHeight}" : "");
        };
        fpsTimer.Start();

        // 剪貼簿雙向同步：輪詢本機剪貼簿有變更就送到被控端
        var clipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        clipTimer.Tick += (_, _) => PollLocalClipboard();
        clipTimer.Start();

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var host = new Border { Background = Brushes.Black, Child = _screen };
        root.Children.Add(host);
        Content = root;

        _connect.Click += (_, _) => ToggleConnect();
        _monitors.SelectionChanged += OnMonitorPicked;

        _screen.MouseMove += OnMouseMove;
        _screen.MouseLeftButtonDown += (_, e) => OnButton(e, CoreMouseButton.Left, true);
        _screen.MouseLeftButtonUp += (_, e) => OnButton(e, CoreMouseButton.Left, false);
        _screen.MouseRightButtonDown += (_, e) => OnButton(e, CoreMouseButton.Right, true);
        _screen.MouseRightButtonUp += (_, e) => OnButton(e, CoreMouseButton.Right, false);
        _screen.MouseWheel += OnWheel;
        _screen.MouseDown += (_, _) => _screen.Focus();
        KeyDown += (_, e) => OnKey(e, true);
        KeyUp += (_, e) => OnKey(e, false);

        Closed += (_, _) => _cts?.Cancel();
    }

    private void ToggleConnect()
    {
        if (_cts is not null) { _cts.Cancel(); return; } // 連線中／重連中 → 使用者要求中斷

        if (!int.TryParse(_port.Text, out int port)) { _code.Text = "port 格式錯誤"; return; }
        string host = _host.Text.Trim();   // 必須在 UI 執行緒讀 TextBox，不能在下面的 Task 裡讀
        var cts = new CancellationTokenSource();
        _cts = cts;
        _connect.Content = "中斷";
        _ = ConnectLoopAsync(host, port, cts.Token);
    }

    /// <summary>連線並在非使用者主動中斷的斷線時自動重連（約每 2 秒一次）；連成功會重置重試次數。</summary>
    private async Task ConnectLoopAsync(string host, int port, CancellationToken userCt)
    {
        int attempt = 0;
        const int maxAttempts = 20;
        while (!userCt.IsCancellationRequested)
        {
            var client = new ViewerClient();
            bool gotCode = false;
            client.VerificationCodeReady += code => { gotCode = true; Dispatch(() => { _code.Foreground = Brushes.LightGreen; _code.Text = code; }); };
            client.MonitorListReceived += list => Dispatch(() => PopulateMonitors(list));
            client.FrameRendered += OnFrame;
            client.ClipboardReceived += text => Dispatch(() => SetLocalClipboard(text));
            Dispatch(() =>
            {
                _client = client;
                _code.Foreground = Brushes.Orange;
                _code.Text = attempt == 0 ? "連線中…" : $"重連中…（{attempt}）";
            });

            try { await client.RunAsync(host, port, userCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "viewer-error.txt"), ex.ToString()); } catch { }
                Dispatch(() => { _code.Foreground = Brushes.IndianRed; _code.Text = "連線失敗：" + ex.Message; });
            }
            finally
            {
                client.FrameRendered -= OnFrame;
                client.Dispose();
                Dispatch(() => { _client = null; _monitors.Items.Clear(); });
            }

            if (userCt.IsCancellationRequested) break;
            attempt = gotCode ? 0 : attempt + 1;          // 連成功過就把重試次數歸零
            if (attempt >= maxAttempts)
            {
                Dispatch(() => { _code.Foreground = Brushes.IndianRed; _code.Text = "多次重連失敗，請重新連線"; });
                break;
            }
            Dispatch(() => { _code.Foreground = Brushes.Orange; _code.Text = $"連線中斷，2 秒後重連…（{attempt}）"; });
            try { await Task.Delay(2000, userCt).ConfigureAwait(false); } catch { break; }
        }
        Dispatch(ResetConnectionUi);
    }

    private void ResetConnectionUi()
    {
        _client = null; _cts = null;
        _connect.Content = "連線";
        _monitors.Items.Clear();
        _stats.Text = "";
    }

    // ---- 剪貼簿雙向同步 ----

    private void PollLocalClipboard()
    {
        var client = _client;
        if (client is null) return;
        string? text = SafeGetClipboardText();
        if (text is null || text == _clipLastSeen) return;
        _clipLastSeen = text;
        _ = client.SendClipboardAsync(text);
    }

    private static string? SafeGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }

    private void SetLocalClipboard(string text)
    {
        _clipLastSeen = text; // 避免又被輪詢當成本機變更回送
        try { Clipboard.SetText(text); } catch { }
    }

    private void PopulateMonitors(IReadOnlyList<MonitorDescriptor> list)
    {
        _switchingList = true;
        _monitors.Items.Clear();
        foreach (var m in list) _monitors.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Index });
        if (_monitors.Items.Count > 0) _monitors.SelectedIndex = 0;
        _switchingList = false;
    }

    private void OnMonitorPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingList || _client is null) return;
        if (_monitors.SelectedItem is ComboBoxItem { Tag: int idx })
            _ = _client.SwitchMonitorAsync(idx);
    }

    private void OnFrame(RenderedFrame f) => Dispatch(() =>
    {
        if (_bitmap is null || _bitmap.PixelWidth != f.Width || _bitmap.PixelHeight != f.Height)
        {
            _bitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
            _screen.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
        _fpsFrames++;

        if (_snapPath is not null && !_snapped && ++_frameCount >= _snapAfter)
        {
            _snapped = true;
            try
            {
                using var snapFs = System.IO.File.Create(_snapPath);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(_bitmap));
                enc.Save(snapFs);
            }
            catch { }
            Close();
        }
    });

    // ---- 把本機輸入映射成遠端桌面的 0..65535 比例值 ----

    private bool TryNormalize(System.Windows.Point p, out ushort nx, out ushort ny)
    {
        nx = ny = 0;
        if (_bitmap is null) return false;
        double cw = _screen.ActualWidth, ch = _screen.ActualHeight;
        if (cw <= 0 || ch <= 0) return false;

        // Uniform 置中：等比縮放後置中，先扣掉黑邊再換算
        double scale = Math.Min(cw / _bitmap.PixelWidth, ch / _bitmap.PixelHeight);
        double dispW = _bitmap.PixelWidth * scale, dispH = _bitmap.PixelHeight * scale;
        double offX = (cw - dispW) / 2, offY = (ch - dispH) / 2;
        double u = (p.X - offX) / dispW, v = (p.Y - offY) / dispH;
        if (u < 0 || u > 1 || v < 0 || v > 1) return false;

        nx = (ushort)Math.Round(u * 65535);
        ny = (ushort)Math.Round(v * 65535);
        return true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_client is null) return;
        if (TryNormalize(e.GetPosition(_screen), out var nx, out var ny))
            _client.QueueMouseMove(nx, ny); // 合流：只保留最新位置、定頻送出
    }

    private void OnButton(MouseButtonEventArgs e, CoreMouseButton button, bool down)
    {
        if (_client is null) return;
        _screen.Focus();
        if (down) _screen.CaptureMouse(); else _screen.ReleaseMouseCapture();
        // 先把游標移到正確位置再按，點擊才會落在對的地方
        if (TryNormalize(e.GetPosition(_screen), out var nx, out var ny))
            _ = _client.SendInputAsync(InputEvent.Move(nx, ny));
        _ = _client.SendInputAsync(down ? InputEvent.MouseDown(button) : InputEvent.MouseUp(button));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_client is null) return;
        _ = _client.SendInputAsync(InputEvent.Wheel((short)e.Delta));
    }

    private void OnKey(KeyEventArgs e, bool down)
    {
        if (_client is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        uint scan = MapVirtualKey((uint)vk, 0 /*MAPVK_VK_TO_VSC*/);
        if (scan == 0) return;
        bool extended = IsExtended(key);
        _ = _client.SendInputAsync(down ? InputEvent.KeyDown((ushort)scan, extended) : InputEvent.KeyUp((ushort)scan, extended));
        e.Handled = true;
    }

    private static bool IsExtended(Key k) => k is Key.Right or Key.Left or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Insert or Key.Delete
        or Key.RightCtrl or Key.RightAlt or Key.NumLock or Key.PrintScreen or Key.Apps;

    private void Dispatch(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action, DispatcherPriority.Render);
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
