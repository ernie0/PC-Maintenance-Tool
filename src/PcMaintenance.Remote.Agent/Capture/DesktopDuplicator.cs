using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PcMaintenance.Remote.Agent.Capture;

/// <summary>
/// 用 DXGI Desktop Duplication 擷取某一個螢幕，直接把有變動的畫面轉成 NV12。
/// 支援列出螢幕、切換螢幕；螢幕解析度改變或存取遺失（ACCESS_LOST，例如切換使用者、全螢幕）時自動重建。
/// 只在有桌面的工作階段可用；服務在 session 0 要切到使用者桌面才行（P2）。
/// </summary>
public sealed unsafe class DesktopDuplicator : IDisposable
{
    private const int WaitTimeout = unchecked((int)0x887A0027); // DXGI_ERROR_WAIT_TIMEOUT
    private const int AccessLost = unchecked((int)0x887A0026);  // DXGI_ERROR_ACCESS_LOST

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIOutput1 _output = null!;
    private IDXGIOutputDuplication _duplication = null!;
    private ID3D11Texture2D _staging = null!;

    // 游標合成：DXGI 擷取不含硬體游標，必須自己把游標畫進畫面，控制端才看得到自己指到哪裡。
    private byte[] _lastBgra = [];   // 最近一張桌面（緊密排列、不含游標），游標動但畫面沒變時用它重畫
    private byte[] _composed = [];   // 畫上游標後的輸出緩衝
    private bool _hasFrame;
    private int _cursorX, _cursorY;
    private bool _cursorVisible;
    private byte[] _cursorShape = [];
    private int _curW, _curH, _curPitch, _curType;
    private const int ShapeMonochrome = 1, ShapeColor = 2, ShapeMaskedColor = 4;

    public MonitorInfo Monitor { get; private set; } = null!;
    public int Width => Monitor.Width;
    public int Height => Monitor.Height;
    public int Nv12Size => Nv12Converter.RequiredSize(Width, Height);

    public DesktopDuplicator(int monitorIndex = 0)
    {
        Initialize(monitorIndex);
    }

    /// <summary>列出目前接在桌面上的所有螢幕。</summary>
    public static IReadOnlyList<MonitorInfo> ListMonitors()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        int index = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        if (!desc.AttachedToDesktop) continue;
                        var b = desc.DesktopCoordinates;
                        list.Add(new MonitorInfo
                        {
                            Index = index++,
                            DeviceName = desc.DeviceName,
                            AdapterName = adapter.Description1.Description,
                            Left = b.Left,
                            Top = b.Top,
                            Width = b.Right - b.Left,
                            Height = b.Bottom - b.Top,
                            IsPrimary = b.Left == 0 && b.Top == 0,
                        });
                    }
                }
            }
        }
        return list;
    }

    public void SwitchMonitor(int monitorIndex)
    {
        DisposeDeviceObjects();
        Initialize(monitorIndex);
    }

    private void Initialize(int monitorIndex)
    {
        var monitors = ListMonitors();
        if (monitors.Count == 0) throw new InvalidOperationException("找不到接在桌面上的螢幕");
        if (monitorIndex < 0 || monitorIndex >= monitors.Count) monitorIndex = 0;
        var target = monitors[monitorIndex];

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            bool used = false;
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var desc = output.Description;
                if (desc.AttachedToDesktop && desc.DeviceName == target.DeviceName)
                {
                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                        new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                        out _device!, out _context!).CheckError();
                    _output = output.QueryInterface<IDXGIOutput1>();
                    output.Dispose();
                    used = true;
                    break;
                }
                output.Dispose();
            }
            adapter.Dispose();
            if (used) break;
        }
        if (_output is null) throw new InvalidOperationException($"無法開啟螢幕 {target.DeviceName}");

        Monitor = target;
        _duplication = _output.DuplicateOutput(_device);
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _lastBgra = new byte[Width * Height * 4];
        _composed = new byte[Width * Height * 4];
        _hasFrame = false;
        _cursorVisible = false;
    }

    /// <summary>
    /// 擷取下一張。有新畫面時轉成 NV12 寫進 nv12（長度須 >= <see cref="Nv12Size"/>），updated=true。
    /// 逾時（畫面沒變）或剛從存取遺失復原時 updated=false。回傳 false 代表這次要重建、請呼叫端再試。
    /// </summary>
    public bool TryGetNv12(int timeoutMs, Span<byte> nv12, out bool updated)
    {
        updated = false;
        if (nv12.Length < Nv12Size) throw new ArgumentException("nv12 緩衝區太小", nameof(nv12));

        var r = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var resource);
        if (r.Code == WaitTimeout) return true;
        if (r.Code == AccessLost)
        {
            Rebuild();
            return false;
        }
        r.CheckError();

        using var _ = resource; // 不論桌面有沒有變，取得的資源都要釋放
        try
        {
            // 游標形狀更新（滑鼠換圖示時 PointerShapeBufferSize>0）
            if (frameInfo.PointerShapeBufferSize > 0) UpdateCursorShape(frameInfo.PointerShapeBufferSize);
            // 游標位置／可見度更新
            if (frameInfo.LastMouseUpdateTime != 0)
            {
                _cursorVisible = frameInfo.PointerPosition.Visible;
                _cursorX = frameInfo.PointerPosition.Position.X;
                _cursorY = frameInfo.PointerPosition.Position.Y;
            }

            bool desktopChanged = frameInfo.LastPresentTime != 0;
            bool mouseChanged = frameInfo.LastMouseUpdateTime != 0;

            if (desktopChanged)
            {
                using (var tex = resource.QueryInterface<ID3D11Texture2D>())
                    _context.CopyResource(_staging, tex);

                var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    // 存成緊密排列的桌面快照（之後游標單獨移動時可直接重畫）
                    byte* src = (byte*)map.DataPointer;
                    int pitch = (int)map.RowPitch, rowBytes = Width * 4;
                    fixed (byte* dst = _lastBgra)
                        for (int y = 0; y < Height; y++)
                            Buffer.MemoryCopy(src + (long)y * pitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
                    _hasFrame = true;
                }
                finally { _context.Unmap(_staging, 0); }
            }

            if (!_hasFrame) return true;                       // 還沒有任何畫面可送
            if (!desktopChanged && !mouseChanged) return true; // 什麼都沒變

            Array.Copy(_lastBgra, _composed, _lastBgra.Length);
            if (_cursorVisible && _curW > 0 && _curH > 0) DrawCursor();
            Nv12Converter.Convert(_composed, Width * 4, Width, Height, nv12);
            updated = true;
        }
        finally
        {
            _duplication.ReleaseFrame();
        }
        return true;
    }

    private void UpdateCursorShape(uint bufferSize)
    {
        if (_cursorShape.Length < bufferSize) _cursorShape = new byte[bufferSize];
        fixed (byte* p = _cursorShape)
        {
            _duplication.GetFramePointerShape(bufferSize, (nint)p, out _, out var info);
            _curW = (int)info.Width;
            _curH = (int)info.Height;
            _curPitch = (int)info.Pitch;
            _curType = (int)info.Type;
        }
    }

    /// <summary>把快取的游標形狀合成到 _composed 的目前游標位置（支援彩色、遮罩彩色、單色三種）。</summary>
    private void DrawCursor()
    {
        // 單色游標的高度是實際的兩倍（上半 AND 遮罩、下半 XOR 遮罩）
        int shapeH = _curType == ShapeMonochrome ? _curH / 2 : _curH;
        fixed (byte* shape = _cursorShape)
        fixed (byte* frame = _composed)
        {
            for (int row = 0; row < shapeH; row++)
            {
                int fy = _cursorY + row;
                if (fy < 0 || fy >= Height) continue;
                for (int col = 0; col < _curW; col++)
                {
                    int fx = _cursorX + col;
                    if (fx < 0 || fx >= Width) continue;
                    byte* d = frame + ((long)fy * Width + fx) * 4;

                    if (_curType == ShapeColor)
                    {
                        byte* s = shape + (long)row * _curPitch + col * 4;
                        int a = s[3];
                        if (a == 0) continue;
                        d[0] = (byte)((s[0] * a + d[0] * (255 - a)) / 255);
                        d[1] = (byte)((s[1] * a + d[1] * (255 - a)) / 255);
                        d[2] = (byte)((s[2] * a + d[2] * (255 - a)) / 255);
                    }
                    else if (_curType == ShapeMaskedColor)
                    {
                        byte* s = shape + (long)row * _curPitch + col * 4;
                        if (s[3] == 0) { d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; }   // 不透明：直接蓋
                        else { d[0] ^= s[0]; d[1] ^= s[1]; d[2] ^= s[2]; }          // 遮罩：XOR 反色
                    }
                    else // 單色
                    {
                        int bit = 7 - (col & 7);
                        byte andMask = shape[(long)row * _curPitch + (col >> 3)];
                        byte xorMask = shape[(long)(row + shapeH) * _curPitch + (col >> 3)];
                        int and = (andMask >> bit) & 1, xor = (xorMask >> bit) & 1;
                        if (and == 0 && xor == 0) { d[0] = d[1] = d[2] = 0; }        // 黑
                        else if (and == 0 && xor == 1) { d[0] = d[1] = d[2] = 255; } // 白
                        else if (and == 1 && xor == 1) { d[0] = (byte)~d[0]; d[1] = (byte)~d[1]; d[2] = (byte)~d[2]; } // 反色
                        // (and=1,xor=0) 透明：保留
                    }
                }
            }
        }
    }

    private void Rebuild()
    {
        try { _duplication?.Dispose(); } catch { }
        // 解析度可能已改變，依新的螢幕尺寸重建。
        SwitchMonitor(Monitor.Index);
    }

    private void DisposeDeviceObjects()
    {
        _staging?.Dispose();
        _duplication?.Dispose();
        _output?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _output = null!;
    }

    public void Dispose() => DisposeDeviceObjects();
}
