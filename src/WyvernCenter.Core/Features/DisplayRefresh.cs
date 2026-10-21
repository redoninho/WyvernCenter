using System.Runtime.InteropServices;

namespace WyvernCenter.Core.Features;

/// <summary>Refresh-rate switching for the internal panel (the display driven by the Intel iGPU on Optimus laptops).</summary>
public static class DisplayRefresh
{
    public static int[] RatesFor(IEnumerable<(int W, int H, int Hz)> modes, int width, int height) =>
        modes.Where(m => m.W == width && m.H == height && m.Hz > 1).Select(m => m.Hz).Distinct().OrderBy(h => h).ToArray();

    /// <summary>The device name (e.g. \\.\DISPLAY1) of the internal panel, or null if it can't be identified.</summary>
    public static string? InternalDisplay()
    {
        var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        string? fallback = null;
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DisplayDevice>())
        {
            var attached = (dd.StateFlags & AttachedToDesktop) != 0;
            if (!attached || dd.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            if (dd.DeviceString.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return dd.DeviceName;
            if ((dd.StateFlags & PrimaryDevice) != 0) fallback = dd.DeviceName;
        }
        return fallback;
    }

    public static (int Width, int Height, int Hz)? Current(string device)
    {
        var dm = NewMode();
        return EnumDisplaySettings(device, CurrentSettings, ref dm) ? (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : null;
    }

    public static int[] SupportedRates(string device)
    {
        if (Current(device) is not { } cur) return Array.Empty<int>();
        var modes = new List<(int, int, int)>();
        var dm = NewMode();
        for (var i = 0; EnumDisplaySettings(device, i, ref dm); i++)
            modes.Add((dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        return RatesFor(modes, cur.Width, cur.Height);
    }

    public static bool Set(string device, int hz)
    {
        var dm = NewMode();
        if (!EnumDisplaySettings(device, CurrentSettings, ref dm)) return false;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DmDisplayFrequency;
        return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == 0;
    }

    private static DevMode NewMode() => new() { dmSize = (short)Marshal.SizeOf<DevMode>() };

    private const int CurrentSettings = -1;
    private const int DmDisplayFrequency = 0x400000;
    private const int CdsUpdateRegistry = 0x01;
    private const int AttachedToDesktop = 0x1;
    private const int PrimaryDevice = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DevMode dm, IntPtr hwnd, int flags, IntPtr param);
}
