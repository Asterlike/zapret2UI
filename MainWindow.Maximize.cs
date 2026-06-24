using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ZapretUI;

/// <summary>
/// Fix for a borderless (<c>WindowStyle=None</c>) window: when maximized it would
/// otherwise cover the taskbar and overflow the screen edges. We handle
/// WM_GETMINMAXINFO and clamp the maximized size/position to the monitor's
/// <em>work area</em> (the screen minus the taskbar).
/// </summary>
public partial class MainWindow
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            AdjustMaximizedBounds(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void AdjustMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        RECT work = mi.rcWork, full = mi.rcMonitor;

        // Position/size are expressed relative to the monitor's top-left.
        mmi.ptMaxPosition.X = work.left - full.left;
        mmi.ptMaxPosition.Y = work.top - full.top;
        mmi.ptMaxSize.X = work.right - work.left;
        mmi.ptMaxSize.Y = work.bottom - work.top;

        // Keep the user-defined minimum window size on maximize-tracking too. WM_GETMINMAXINFO is in
        // physical pixels, so scale the logical (DIP) minimum by the monitor DPI — otherwise on a
        // >100% display the enforced minimum is smaller than the XAML MinWidth/MinHeight.
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        mmi.ptMinTrackSize.X = (int)(880 * scale);
        mmi.ptMinTrackSize.Y = (int)(580 * scale);

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    // ---- interop -----------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
