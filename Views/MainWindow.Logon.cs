using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Zapret2UI.Views;

/// <summary>
/// Two things that go wrong only when the app starts together with Windows: a window drawn behind the
/// logon screen comes up black, and the tray icon of an elevated process does not come back when the
/// taskbar is (re)created.
/// </summary>
public partial class MainWindow
{
    // ---- the black window ---------------------------------------------------

    private DispatcherTimer? _desktopWait;

    /// <summary>
    /// A window drawn while the user's desktop is not the one on screen — at logon, while the welcome
    /// screen still covers it, or behind a UAC prompt — has its frames refused (<c>S_PRESENT_OCCLUDED</c>).
    /// WPF paints once more and then waits for an unlock or a wake-up before it tries again (HwndTarget,
    /// the <c>NeedsRePresentOnWake</c> handling), and logging on is neither: the window stays black until
    /// something repaints all of it — hiding and showing it, or a resize. That is the bug as it was
    /// reported: the window «opens itself into a black screen», and closing and reopening it helps.
    /// So when the window becomes visible while the desktop is not ours, wait for the desktop and then
    /// invalidate the whole window — the call WPF itself makes on unlock. An ordinary launch pays one check.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || _desktopWait is not null || InputDesktopIsOurs()) return;

        DateTime since = DateTime.UtcNow;
        _desktopWait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _desktopWait.Tick += (_, _) =>
        {
            // Hidden again: the next show repaints anyway, and checks again. A desktop that never comes
            // back is not worth polling for ever.
            if (IsVisible && !InputDesktopIsOurs() && DateTime.UtcNow - since < TimeSpan.FromMinutes(10))
                return;
            _desktopWait?.Stop();
            _desktopWait = null;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (IsVisible && hwnd != IntPtr.Zero) InvalidateRect(hwnd, IntPtr.Zero, true);
        };
        _desktopWait.Start();
    }

    /// <summary>Is the desktop on screen the one this window lives on? Not while the logon screen, the lock
    /// screen or a UAC prompt is up: they sit on the secure desktop, which an ordinary process cannot even
    /// open — so a failed <c>OpenInputDesktop</c> is itself the answer.</summary>
    private static bool InputDesktopIsOurs()
    {
        IntPtr input = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (input == IntPtr.Zero) return false;
        try
        {
            IntPtr own = GetThreadDesktop(GetCurrentThreadId());   // not ours to close
            return own != IntPtr.Zero
                && string.Equals(DesktopName(input), DesktopName(own), StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(input); }
    }

    private static string DesktopName(IntPtr desktop)
    {
        var name = new char[256];
        if (!GetUserObjectInformation(desktop, UOI_NAME, name, name.Length * sizeof(char), out _)) return "";
        int end = Array.IndexOf(name, '\0');
        return new string(name, 0, end < 0 ? name.Length : end);
    }

    // ---- the tray icon --------------------------------------------------------

    /// <summary>
    /// Let Explorer's «TaskbarCreated» broadcast through. The app runs elevated, Windows drops messages
    /// coming from a lower integrity level (UIPI) — Explorer's included — and WinForms' NotifyIcon puts its
    /// icon back only when that message arrives, after marking even a failed first add as done. Without
    /// this the icon is gone for good once Explorer restarts, and a logon start that beats the taskbar
    /// never shows one at all: the app would be running in a tray it has no icon in.
    /// </summary>
    private static void AllowTaskbarCreated()
    {
        try { ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), MSGFLT_ADD); }
        catch { /* best-effort: without it only the icon's comeback is lost */ }
    }

    // ---- interop ----------------------------------------------------------------

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;
    private const uint MSGFLT_ADD = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, [Out] char[] pvInfo,
                                                        int nLength, out int lpnLengthNeeded);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);
}
