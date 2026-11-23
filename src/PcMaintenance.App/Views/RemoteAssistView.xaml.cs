using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcMaintenance.App.ViewModels;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Viewer.Net;
using CoreMouseButton = PcMaintenance.Remote.Core.Protocol.MouseButton;

namespace PcMaintenance.App.Views;

/// <summary>
/// 遠端協助分頁的畫面與操作。畫面繪製（WriteableBitmap）與滑鼠鍵盤擷取綁在這裡的元件上，
/// 透過 ViewModel 的 <see cref="RemoteAssistViewModel.Client"/> 把本機操作送到遠端。
/// 連線邏輯、解碼都在已驗證的 <see cref="ViewerClient"/>，這裡不重覆。
/// </summary>
public partial class RemoteAssistView : UserControl
{
    private RemoteAssistViewModel? _vm;
    private ViewerClient? _client;
    private WriteableBitmap? _bitmap;

    public RemoteAssistView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        Screen.MouseMove += OnMouseMove;
        Screen.MouseLeftButtonDown += (_, e) => OnButton(e, CoreMouseButton.Left, true);
        Screen.MouseLeftButtonUp += (_, e) => OnButton(e, CoreMouseButton.Left, false);
        Screen.MouseRightButtonDown += (_, e) => OnButton(e, CoreMouseButton.Right, true);
        Screen.MouseRightButtonUp += (_, e) => OnButton(e, CoreMouseButton.Right, false);
        Screen.MouseWheel += OnWheel;
        Screen.MouseDown += (_, _) => Screen.Focus();
        Screen.KeyDown += (_, e) => OnKey(e, true);
        Screen.KeyUp += (_, e) => OnKey(e, false);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) { _vm.ClientConnected -= OnConnected; _vm.ClientDisconnected -= OnDisconnected; }
        _vm = DataContext as RemoteAssistViewModel;
        if (_vm is not null) { _vm.ClientConnected += OnConnected; _vm.ClientDisconnected += OnDisconnected; }
    }

    private void OnConnected()
    {
        _client = _vm?.Client;
        if (_client is not null) _client.FrameRendered += OnFrame;
    }

    private void OnDisconnected()
    {
        if (_client is not null) _client.FrameRendered -= OnFrame;
        _client = null;
        _bitmap = null;
        Screen.Source = null;
    }

    private void OnFrame(RenderedFrame f) => Dispatcher.BeginInvoke(() =>
    {
        if (_bitmap is null || _bitmap.PixelWidth != f.Width || _bitmap.PixelHeight != f.Height)
        {
            _bitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
            Screen.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
    }, DispatcherPriority.Render);

    // ---- 本機輸入 → 遠端桌面的 0..65535 比例值 ----

    private bool TryNormalize(Point p, out ushort nx, out ushort ny)
    {
        nx = ny = 0;
        if (_bitmap is null) return false;
        double cw = Screen.ActualWidth, ch = Screen.ActualHeight;
        if (cw <= 0 || ch <= 0) return false;

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
        if (TryNormalize(e.GetPosition(Screen), out var nx, out var ny))
            _client.QueueMouseMove(nx, ny); // 合流：只保留最新位置、定頻送出
    }

    private void OnButton(MouseButtonEventArgs e, CoreMouseButton button, bool down)
    {
        if (_client is null) return;
        Screen.Focus();
        if (down) Screen.CaptureMouse(); else Screen.ReleaseMouseCapture();
        if (TryNormalize(e.GetPosition(Screen), out var nx, out var ny))
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
        uint scan = MapVirtualKey((uint)vk, 0);
        if (scan == 0) return;
        _ = _client.SendInputAsync(down
            ? InputEvent.KeyDown((ushort)scan, IsExtended(key))
            : InputEvent.KeyUp((ushort)scan, IsExtended(key)));
        e.Handled = true;
    }

    private static bool IsExtended(Key k) => k is Key.Right or Key.Left or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Insert or Key.Delete
        or Key.RightCtrl or Key.RightAlt or Key.NumLock or Key.PrintScreen or Key.Apps;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
