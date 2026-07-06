using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenWire.Service.Engine;

/// <summary>
/// Static hardware inventory for the "hardware resource usage" list: the CPU model
/// name and the list of GPU adapters (name + LUID). Live utilisation is sampled
/// separately by <see cref="HardwareMonitor"/> and matched to GPUs by LUID.
/// </summary>
internal static class HardwareInventory
{
    /// <summary>A GPU adapter as DXGI sees it: its display name and the LUID key that
    /// matches the "GPU Engine" performance-counter instances ("0x{high}_0x{low}").</summary>
    public readonly record struct GpuAdapter(string Name, string LuidKey);

    /// <summary>CPU model, e.g. "AMD Ryzen 9 9950X3D 16-Core Processor". Cached — it never changes.</summary>
    public static string CpuName { get; } = ReadCpuName();

    private static string ReadCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = k?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { /* fall through */ }
        return "CPU";
    }

    /// <summary>Enumerate GPU adapters via DXGI (name + LUID), in the same order Windows
    /// numbers them. Returns an empty list if DXGI is unavailable.</summary>
    public static List<GpuAdapter> EnumerateGpus()
    {
        var list = new List<GpuAdapter>();
        object? factoryObj = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out factoryObj) != 0 || factoryObj is not IDXGIFactory1 factory)
                return list;

            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter) != 0 || adapter is null) break;
                try
                {
                    if (adapter.GetDesc1(out var desc) == 0)
                    {
                        // "Microsoft Basic Render Driver" (software) has the software flag — skip it.
                        bool software = (desc.Flags & 2u) != 0;
                        if (!software)
                        {
                            string key = $"0x{(uint)desc.AdapterLuid.High:x8}_0x{desc.AdapterLuid.Low:x8}";
                            list.Add(new GpuAdapter(desc.Description?.Trim() ?? "GPU", key));
                        }
                    }
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
        }
        catch { /* no DXGI — leave the list empty */ }
        finally { if (factoryObj is not null) Marshal.ReleaseComObject(factoryObj); }
        return list;
    }

    // ---- minimal DXGI interop (only the vtable slots we need) ----

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppFactory);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    // Placeholder methods keep the correct vtable order; only EnumAdapters1 / GetDesc1 are ever called.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();      // IDXGIObject
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation();                       // IDXGIFactory
        void CreateSwapChain(); void CreateSoftwareAdapter();                                                 // IDXGIFactory
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 ppAdapter);                           // IDXGIFactory1
        [PreserveSig] int IsCurrent();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("29038f61-3839-4626-91fd-086879011a05")]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();       // IDXGIObject
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();                                     // IDXGIAdapter
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);                                             // IDXGIAdapter1
    }
}
