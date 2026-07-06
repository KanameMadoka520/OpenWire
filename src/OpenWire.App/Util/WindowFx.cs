using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenWire.App.Util;

/// <summary>Small DWM helpers for custom-chrome windows.</summary>
public static class WindowFx
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Ask DWM for Windows-11 rounded corners (no-op on older systems).
    /// Call from SourceInitialized (the HWND must exist).</summary>
    public static void ApplyRoundedCorners(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            int pref = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* purely cosmetic */ }
    }
}
