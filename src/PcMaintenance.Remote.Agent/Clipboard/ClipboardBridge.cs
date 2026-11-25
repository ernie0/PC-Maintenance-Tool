using System.Runtime.InteropServices;

namespace PcMaintenance.Remote.Agent.Clipboard;

/// <summary>
/// 被控端的剪貼簿橋接：在一條專用 STA 執行緒上輪詢本機剪貼簿文字，
/// 有新文字就以 <see cref="TextCopied"/> 丟出；收到遠端文字時用 <see cref="SetText"/> 寫入本機剪貼簿。
/// 用 Win32（不相依 WPF）；剛寫入的文字會記成「已看過」避免回送造成迴圈。只處理純文字。
/// </summary>
public sealed class ClipboardBridge : IDisposable
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int MaxChars = 1_000_000; // 約 1MB 文字上限，避免超大剪貼簿塞爆連線

    private readonly Thread _thread;
    private readonly object _lock = new();
    private volatile bool _running = true;
    private string? _lastSeen;
    private string? _pendingSet;

    public event Action<string>? TextCopied;

    public ClipboardBridge()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "agent-clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>把遠端送來的文字排入，由 STA 執行緒寫進本機剪貼簿。</summary>
    public void SetText(string text) { lock (_lock) { _pendingSet = text; } }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                string? toSet = null;
                lock (_lock) { if (_pendingSet is not null) { toSet = _pendingSet; _pendingSet = null; } }
                if (toSet is not null && TrySet(toSet)) _lastSeen = toSet; // 記成已看過 → 不會被當成本機變更回送

                string? cur = TryGet();
                if (cur is not null && cur.Length <= MaxChars && cur != _lastSeen)
                {
                    _lastSeen = cur;
                    TextCopied?.Invoke(cur);
                }
            }
            catch { /* 剪貼簿被別的程式鎖住等，下一輪再試 */ }
            Thread.Sleep(400);
        }
    }

    private static string? TryGet()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    private static bool TrySet(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (hMem == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(hMem);
            if (p == IntPtr.Zero) return false;
            Marshal.Copy(bytes, 0, p, bytes.Length);
            GlobalUnlock(hMem);
            // 成功後剪貼簿系統接管 hMem，不可自行釋放
            return SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero;
        }
        finally { CloseClipboard(); }
    }

    public void Dispose() => _running = false;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
}
