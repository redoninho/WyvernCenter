using System.Runtime.InteropServices;

namespace WyvernCenter.UI;

/// <summary>
/// Where Wyvern Center's windows may go on a screen. Like <see cref="Screen.WorkingArea"/>, but an auto-hidden taskbar
/// also keeps its space: Windows gives that space to the working area, so a window placed by the tray would sit under
/// the taskbar whenever it slides up.
/// </summary>
public static class ScreenArea
{
    public static Rectangle Usable(Screen screen)
    {
        var area = screen.WorkingArea;
        try
        {
            var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>() };
            if (((int)SHAppBarMessage(AbmGetState, ref data) & AbsAutoHide) == 0) return area;
            if (SHAppBarMessage(AbmGetTaskbarPos, ref data) == IntPtr.Zero) return area;

            var bar = Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
            // The hidden taskbar sits (almost) off the screen edge; its centre still says which screen it belongs to.
            var centre = new Point(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2);
            if (!Screen.FromPoint(centre).Bounds.Equals(screen.Bounds)) return area;

            return data.uEdge switch
            {
                EdgeBottom => Rectangle.FromLTRB(area.Left, area.Top, area.Right, Math.Min(area.Bottom, screen.Bounds.Bottom - bar.Height)),
                EdgeTop => Rectangle.FromLTRB(area.Left, Math.Max(area.Top, screen.Bounds.Top + bar.Height), area.Right, area.Bottom),
                EdgeLeft => Rectangle.FromLTRB(Math.Max(area.Left, screen.Bounds.Left + bar.Width), area.Top, area.Right, area.Bottom),
                EdgeRight => Rectangle.FromLTRB(area.Left, area.Top, Math.Min(area.Right, screen.Bounds.Right - bar.Width), area.Bottom),
                _ => area,
            };
        }
        catch
        {
            return area; // shell not answering: plain working area
        }
    }

    public static Rectangle AtCursor() => Usable(Screen.FromPoint(Cursor.Position));

    private const uint AbmGetState = 4, AbmGetTaskbarPos = 5;
    private const int AbsAutoHide = 1;
    private const uint EdgeLeft = 0, EdgeTop = 1, EdgeRight = 2, EdgeBottom = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);
}
