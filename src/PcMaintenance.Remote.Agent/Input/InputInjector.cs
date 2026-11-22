using System.Runtime.InteropServices;
using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Agent.Input;

/// <summary>
/// 把控制端送來的 <see cref="InputEvent"/> 用 Win32 SendInput 打進被控端。
/// 滑鼠座標是「相對目前擷取螢幕」的 0..65535 比例值，這裡依該螢幕在虛擬桌面的位置
/// 映射成整個虛擬桌面的絕對座標再送，才會落在正確的螢幕上。
/// 由接收迴圈單一執行緒呼叫即可。
/// </summary>
public sealed class InputInjector
{
    private int _monLeft, _monTop, _monWidth = 1, _monHeight = 1;

    /// <summary>切換螢幕時更新目前擷取螢幕在虛擬桌面的像素範圍。</summary>
    public void SetMonitorBounds(int left, int top, int width, int height)
    {
        _monLeft = left; _monTop = top;
        _monWidth = Math.Max(1, width); _monHeight = Math.Max(1, height);
    }

    public void Inject(in InputEvent ev)
    {
        switch (ev.Kind)
        {
            case InputKind.MouseMove: MouseMove(ev.X, ev.Y); break;
            case InputKind.MouseButton: MouseButton(ev.Button, ev.IsDown); break;
            case InputKind.MouseWheel: MouseWheel(ev.WheelDelta, ev.WheelHorizontal); break;
            case InputKind.Key: Key(ev.ScanCode, ev.IsDown, ev.Extended); break;
        }
    }

    private void MouseMove(ushort normX, ushort normY)
    {
        // 擷取螢幕內的像素
        int px = _monLeft + (int)(normX / 65535.0 * (_monWidth - 1));
        int py = _monTop + (int)(normY / 65535.0 * (_monHeight - 1));

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1);
        int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);

        int absX = (int)Math.Round((px - vx) * 65535.0 / vw);
        int absY = (int)Math.Round((py - vy) * 65535.0 / vh);

        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, absX, absY, 0);
    }

    private void MouseButton(Core.Protocol.MouseButton button, bool down)
    {
        uint flags = button switch
        {
            Core.Protocol.MouseButton.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            Core.Protocol.MouseButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            Core.Protocol.MouseButton.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
        };
        uint mouseData = button is Core.Protocol.MouseButton.X1 ? XBUTTON1
            : button is Core.Protocol.MouseButton.X2 ? XBUTTON2 : 0;
        SendMouse(flags, 0, 0, mouseData);
    }

    private void MouseWheel(short delta, bool horizontal) =>
        SendMouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta));

    private void Key(ushort scanCode, bool down, bool extended)
    {
        uint flags = KEYEVENTF_SCANCODE;
        if (!down) flags |= KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = scanCode, dwFlags = flags } },
        };
        SendOne(input);
    }

    private void SendMouse(uint flags, int dx, int dy, uint mouseData)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = mouseData, dwFlags = flags } },
        };
        SendOne(input);
    }

    private static void SendOne(INPUT input)
    {
        var arr = new[] { input };
        SendInput(1, arr, Marshal.SizeOf<INPUT>());
    }

    // ---- Win32 ----

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nuint dwExtraInfo;
    }
}
