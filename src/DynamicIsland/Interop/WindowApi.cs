using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Interop;

/// <summary>Win32 calls for inspecting, capturing, hiding and restoring other apps' windows.</summary>
public static class WindowApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNA = 8;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const uint GA_ROOT = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint PW_RENDERFULLCONTENT = 2;
    private const uint WM_GETICON = 0x007F;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    private const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// True when a maximized window is shown on this monitor (on the current virtual desktop:
    /// windows on other desktops are "cloaked").
    /// </summary>
    public static bool HasMaximizedWindowOn(RECT monitor)
    {
        bool found = false;
        uint self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || !IsZoomed(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (GetProcessId(hwnd) == self || !GetWindowRect(hwnd, out var r)) return true;
            if (!monitor.Contains(r.Left + r.Width / 2, r.Top + r.Height / 2)) return true;
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);

    public static IntPtr GetRootWindow(IntPtr hwnd) => GetAncestor(hwnd, GA_ROOT);

    public static uint GetProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>Hidden by Windows (on another virtual desktop, or a suspended Store app).</summary>
    public static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Top-level windows from front to back, i.e. most recently used first.</summary>
    public static System.Collections.Generic.List<IntPtr> TopLevelWindowsInZOrder()
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string GetTitle(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static bool IsHung(IntPtr hwnd) => IsHungAppWindow(hwnd);

    public static bool IsChildOrToolWindow(IntPtr hwnd) =>
        (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CHILD) != 0
        || (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0;

    /// <summary>The rectangle you actually see (without the invisible resize borders).</summary>
    public static RECT GetVisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>()) == 0) return rect;
        GetWindowRect(hwnd, out rect);
        return rect;
    }

    public static WINDOWPLACEMENT GetPlacement(IntPtr hwnd)
    {
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref placement);
        return placement;
    }

    public static void RestorePlacement(IntPtr hwnd, WINDOWPLACEMENT placement)
    {
        placement.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        placement.showCmd = placement.showCmd == SW_SHOWMAXIMIZED ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
        placement.flags = 0;
        SetWindowPlacement(hwnd, ref placement);
        ShowWindow(hwnd, SW_SHOW);
    }

    /// <summary>Moves a (hidden) window so its title bar sits under the given point, kept inside the work area.</summary>
    public static void MoveTitleBarTo(IntPtr hwnd, int x, int y)
    {
        GetWindowRect(hwnd, out var r);
        var work = GetWorkArea(x, y);
        int w = r.Width, h = r.Height;
        int left = Math.Clamp(x - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        int top = Math.Clamp(y - 16, work.Top, Math.Max(work.Top, work.Bottom - 48));
        SetWindowPos(hwnd, HWND_TOP, left, top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static RECT GetWorkArea(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        return info.rcWork;
    }

    public static void Activate(IntPtr hwnd)
    {
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
    }

    /// <summary>
    /// Lets mouse input fall through one of our overlay windows, and keeps it out of the taskbar and
    /// off any single virtual desktop (WS_EX_APPWINDOW, added by WPF, is removed).
    /// </summary>
    /// <summary>Turns mouse click-through on or off (the window never takes focus either way).</summary>
    public static void SetClickThrough(IntPtr hwnd, bool through)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = through ? style | WS_EX_TRANSPARENT : style & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    public static void MakeClickThrough(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = (style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | 0x08000000 /* NOACTIVATE */) & ~0x00040000L /* APPWINDOW */;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    public static void PlaceTopmost(IntPtr hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, SWP_NOACTIVATE);

    /// <summary>Puts a topmost window back on top of the other topmost windows, without moving it.</summary>
    public static void RaiseTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | 0x0002 /* NOMOVE */ | SWP_NOACTIVATE);

    public static void MoveTopmost(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

    public static IntPtr GetWindowIcon(IntPtr hwnd)
    {
        foreach (int type in new[] { 1 /* ICON_BIG */, 2 /* ICON_SMALL2 */, 0 /* ICON_SMALL */ })
        {
            SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(type), IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var icon);
            if (icon != IntPtr.Zero) return icon;
        }
        var classIcon = GetClassLongPtr(hwnd, GCLP_HICON);
        return classIcon != IntPtr.Zero ? classIcon : GetClassLongPtr(hwnd, GCLP_HICONSM);
    }

    public static string? GetProcessPath(uint processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// Renders a window into a BGRA pixel buffer with PrintWindow (works for GPU-rendered apps too).
    /// Returns the pixels of the visible area only.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height)? CaptureWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var full) || full.Width <= 0 || full.Height <= 0) return null;
        var visible = GetVisibleBounds(hwnd);
        int w = full.Width, h = full.Height;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDc, dib);
        try
        {
            if (dib == IntPtr.Zero || !PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT)) return null;

            // Crop to the visible frame.
            int cx = Math.Clamp(visible.Left - full.Left, 0, w - 1);
            int cy = Math.Clamp(visible.Top - full.Top, 0, h - 1);
            int cw = Math.Clamp(visible.Width, 1, w - cx);
            int ch = Math.Clamp(visible.Height, 1, h - cy);
            var pixels = new byte[cw * ch * 4];
            for (int row = 0; row < ch; row++)
                Marshal.Copy(bits + ((cy + row) * w + cx) * 4, pixels, row * cw * 4, cw * 4);
            return (pixels, cw, ch);
        }
        finally
        {
            SelectObject(memDc, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
