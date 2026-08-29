using System.Runtime.InteropServices;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Capture;

/// <summary>A DXGI output (monitor) addressable by ddagrab.</summary>
public sealed record DisplayTarget(
    int AdapterIndex,
    int OutputIndex,
    string AdapterName,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary,
    uint VendorId)
{
    public string DisplayLabel =>
        $"Monitor {OutputIndex + 1} — {Width}×{Height}{(IsPrimary ? " (Primary)" : "")}";

    public string DetailLabel => $"{DeviceName}  •  {AdapterName}";
}

/// <summary>
/// Enumerates adapters/outputs through DXGI itself rather than guessing from
/// EnumDisplayMonitors. ddagrab addresses monitors by DXGI output index, so the mapping
/// has to come from the same source or multi-monitor selection silently grabs the wrong
/// screen.
/// </summary>
public static class DisplayEnumerator
{
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorAmd = 0x1002;
    public const uint VendorIntel = 0x8086;

    public static IReadOnlyList<DisplayTarget> Enumerate()
    {
        var results = new List<DisplayTarget>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factoryObj) != 0 || factoryObj is null)
                return FallbackEnumerate();

            var factory = (IDXGIFactory1)factoryObj;
            for (uint a = 0; ; a++)
            {
                if (factory.EnumAdapters1(a, out var adapter) != 0 || adapter is null) break;
                try
                {
                    adapter.GetDesc1(out var adesc);
                    var adapterName = (adesc.Description ?? string.Empty).TrimEnd('\0');

                    for (uint o = 0; ; o++)
                    {
                        if (adapter.EnumOutputs(o, out var output) != 0 || output is null) break;
                        try
                        {
                            output.GetDesc(out var odesc);
                            if (!odesc.AttachedToDesktop) continue;

                            var r = odesc.DesktopCoordinates;
                            results.Add(new DisplayTarget(
                                (int)a, (int)o,
                                adapterName,
                                (odesc.DeviceName ?? string.Empty).TrimEnd('\0'),
                                r.left, r.top,
                                r.right - r.left, r.bottom - r.top,
                                r.left == 0 && r.top == 0,
                                adesc.VendorId));
                        }
                        finally { Marshal.ReleaseComObject(output); }
                    }
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }

            Marshal.ReleaseComObject(factory);
        }
        catch (Exception ex)
        {
            Log.Error("DXGI enumeration failed", ex);
        }

        return results.Count > 0 ? results : FallbackEnumerate();
    }

    /// <summary>
    /// The adapter that owns the desktop. On hybrid laptops this is normally the iGPU,
    /// and it is the only adapter whose Desktop Duplication actually returns frames.
    /// </summary>
    public static int PrimaryAdapterIndex(IReadOnlyList<DisplayTarget> displays)
    {
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        return primary?.AdapterIndex ?? 0;
    }

    public static DisplayTarget? FindContaining(IReadOnlyList<DisplayTarget> displays, int x, int y)
    {
        foreach (var d in displays)
            if (x >= d.Left && x < d.Left + d.Width && y >= d.Top && y < d.Top + d.Height)
                return d;
        return displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
    }

    /// <summary>
    /// Last resort if DXGI is unavailable: report a single primary display from GDI metrics
    /// so the app still runs instead of showing an empty monitor list.
    /// </summary>
    private static IReadOnlyList<DisplayTarget> FallbackEnumerate()
    {
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        Log.Warn($"Falling back to GDI display metrics ({w}x{h})");
        return new[]
        {
            new DisplayTarget(0, 0, "Primary adapter", @"\\.\DISPLAY1", 0, 0, w, h, true, 0)
        };
    }

    public static string VendorName(uint vendorId) => vendorId switch
    {
        VendorNvidia => "NVIDIA",
        VendorAmd => "AMD",
        VendorIntel => "Intel",
        _ => "GPU"
    };

    // ---- interop --------------------------------------------------------

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    // Vtable order matters: every inherited method must be declared, in order.
    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput? output);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid name, out long umdVersion);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr hwnd);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
        [PreserveSig] bool IsCurrent();
    }
}
