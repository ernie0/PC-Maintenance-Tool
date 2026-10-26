using System.Runtime.InteropServices;

namespace PcMaintenance.Core.SystemInfo;

/// <summary>
/// Graphics adapters as DirectX sees them: name, dedicated memory and the LUID that the GPU performance counters
/// use in their instance names. Methods before the ones we call only hold their vtable slots.
/// </summary>
internal static class Dxgi
{
    private const int NotFound = unchecked((int)0x887A0002);
    private const uint SoftwareAdapter = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    internal sealed record Adapter(string Name, string Luid, ulong DedicatedBytes, ulong SharedBytes);

    /// <summary>Hardware adapters only; the "Microsoft Basic Render Driver" software adapter is left out.</summary>
    public static IReadOnlyList<Adapter> Adapters()
    {
        var adapters = new List<Adapter>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0) return adapters;
        try
        {
            for (uint i = 0; ; i++)
            {
                // NotFound marks the end of the list; any other failure ends it too.
                if (factory.EnumAdapters1(i, out var adapter) is NotFound or not 0) break;
                try
                {
                    if (adapter.GetDesc1(out var desc) != 0 || (desc.Flags & SoftwareAdapter) != 0) continue;
                    adapters.Add(new Adapter(desc.Description.Trim(), LuidKey(desc.LuidHighPart, desc.LuidLowPart),
                        desc.DedicatedVideoMemory, desc.SharedSystemMemory));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
        return adapters;
    }

    /// <summary>The form the GPU counters use, e.g. "0x00000000_0x0000D1A3".</summary>
    internal static string LuidKey(int high, uint low) => $"0x{high:X8}_0x{low:X8}";
}
