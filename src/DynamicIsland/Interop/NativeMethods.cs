using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Interop;

internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_APPWINDOW = 0x00040000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>
    /// Hide from Alt+Tab and the taskbar, never steal focus, and don't get tied to one virtual desktop.
    /// WPF adds WS_EX_APPWINDOW for ShowInTaskbar=true, which makes Windows treat it as a regular app
    /// window (taskbar button, single desktop), so it's removed here before the window is first shown.
    /// </summary>
    public static void MakeOverlayWindow(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = (style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    public static void PlaceTopmost(IntPtr hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);

    public static void BringToTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    /// <summary>
    /// True when the window in front (e.g. a game that made itself topmost) sits above this one.
    /// Only walks the few windows stacked above us, so it's cheap enough to run while gaming.
    /// </summary>
    public static bool IsCoveredByForeground(IntPtr hwnd)
    {
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero || front == hwnd) return false;
        var above = GetWindow(hwnd, 3 /* GW_HWNDPREV */);
        for (int i = 0; i < 256 && above != IntPtr.Zero; i++, above = GetWindow(above, 3))
            if (above == front) return true;
        return false;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>
    /// True when some visible window is stacked above this one. Lets the watchdog skip re-raising
    /// (a z-order change every second, per island) when the island is already on top.
    /// </summary>
    public static bool IsBelowVisibleWindow(IntPtr hwnd)
    {
        var above = GetWindow(hwnd, 3 /* GW_HWNDPREV */);
        for (int i = 0; i < 512 && above != IntPtr.Zero; i++, above = GetWindow(above, 3))
            if (IsWindowVisible(above)) return true;
        return false;
    }

    /// <summary>Exclusive-fullscreen games and presentation mode (affects every monitor).</summary>
    public static bool IsExclusiveFullscreenOrPresenting()
    {
        if (SHQueryUserNotificationState(out int state) != 0) return false;
        return state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    /// <summary>True when the focused window covers this whole monitor (fullscreen video, borderless game…).</summary>
    public static bool IsFullscreenWindowOn(int left, int top, int width, int height)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsZoomed(hwnd)) return false;

        var className = new StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        if (!GetWindowRect(hwnd, out var r)) return false;
        return r.Left <= left && r.Top <= top && r.Right >= left + width && r.Bottom >= top + height;
    }

    /// <summary>Battery percent and charging state, or null on machines without a battery.</summary>
    public static (int Percent, bool Charging)? GetBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        bool noBattery = (s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100;
        return noBattery ? null : (s.BatteryLifePercent, s.ACLineStatus == 1);
    }
}
