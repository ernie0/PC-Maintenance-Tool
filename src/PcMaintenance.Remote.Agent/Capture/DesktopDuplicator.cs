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

        try
        {
            if (frameInfo.LastPresentTime == 0) return true; // 只有游標動，畫面沒變
            using (resource)
            using (var tex = resource.QueryInterface<ID3D11Texture2D>())
                _context.CopyResource(_staging, tex);

            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bgra = new ReadOnlySpan<byte>((void*)map.DataPointer, (int)map.RowPitch * Height);
                Nv12Converter.Convert(bgra, (int)map.RowPitch, Width, Height, nv12);
                updated = true;
            }
            finally { _context.Unmap(_staging, 0); }
        }
        finally
        {
            _duplication.ReleaseFrame();
        }
        return true;
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
