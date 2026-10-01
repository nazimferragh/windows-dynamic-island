using System;
using System.Runtime.InteropServices;
using DynamicIsland.Services;

namespace DynamicIsland.Interop;

/// <summary>A zone of a snap layout, as fractions of the monitor's usable area.</summary>
public sealed record SnapCell(double X, double Y, double W, double H, string Name)
{
    public bool IsFullScreen => X == 0 && Y == 0 && W >= 1 && H >= 1;
}

/// <summary>
/// Places a window in a snap zone of a monitor's usable area (below the island's strip, above the
/// taskbar). Works the same on Windows 10 and 11, which lets Windows 10 have snap layouts at all.
/// </summary>
public static class WindowSnapper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public WindowApi.RECT Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WindowApi.RECT rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out WindowApi.RECT rect, int size);

    private const int SW_RESTORE = 9, SW_MAXIMIZE = 3;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_ASYNCWINDOWPOS = 0x4000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>The usable area (work area) of the monitor containing the point, physical pixels.</summary>
    public static WindowApi.RECT WorkAreaAt(int x, int y)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* nearest */), ref info);
        return info.Work;
    }

    /// <summary>Where a zone lands on screen (the visible window edges), physical pixels.</summary>
    public static WindowApi.RECT ZoneRect(WindowApi.RECT work, SnapCell cell)
    {
        int w = work.Width, h = work.Height;
        int left = work.Left + (int)Math.Round(cell.X * w), top = work.Top + (int)Math.Round(cell.Y * h);
        return new WindowApi.RECT
        {
            Left = left,
            Top = top,
            Right = work.Left + (int)Math.Round((cell.X + cell.W) * w),
            Bottom = work.Top + (int)Math.Round((cell.Y + cell.H) * h),
        };
    }

    /// <summary>The window rectangle (with its invisible resize borders) whose visible edges match the zone.</summary>
    private static WindowApi.RECT OuterRectFor(IntPtr hwnd, WindowApi.RECT zone)
    {
        // Windows 10/11 windows have invisible resize borders around what you see. Grow the target
        // by those so the visible edges line up exactly with the zone.
        if (GetWindowRect(hwnd, out var outer) &&
            DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<WindowApi.RECT>()) == 0)
        {
            zone.Left -= visible.Left - outer.Left;
            zone.Top -= visible.Top - outer.Top;
            zone.Right += outer.Right - visible.Right;
            zone.Bottom += outer.Bottom - visible.Bottom;
        }
        return zone;
    }

    /// <summary>Puts the window into the zone at once. Full screen maximizes it, like Windows does.</summary>
    public static bool Snap(IntPtr hwnd, WindowApi.RECT work, SnapCell cell)
    {
        try
        {
            if (cell.IsFullScreen)
            {
                ShowWindow(hwnd, SW_MAXIMIZE);
                return true;
            }
            if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            var zone = OuterRectFor(hwnd, ZoneRect(work, cell));
            bool ok = SetWindowPos(hwnd, IntPtr.Zero, zone.Left, zone.Top, zone.Width, zone.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            if (!ok) Log.Info($"Couldn't snap a window ({Marshal.GetLastWin32Error()}); it may be running with higher rights");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("Snapping a window failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Like <see cref="Snap"/>, but the window glides from where it is into the zone (ease-out,
    /// ~0.26 s) instead of jumping. Each step is posted to the window's own thread (async), so a
    /// slow app can't stall the island; the last step lands exactly on the zone.
    /// </summary>
    public static bool Glide(IntPtr hwnd, WindowApi.RECT work, SnapCell cell)
    {
        try
        {
            if (cell.IsFullScreen) return Snap(hwnd, work, cell); // Windows animates maximizing itself
            if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            if (!GetWindowRect(hwnd, out var from)) return Snap(hwnd, work, cell);
            var to = OuterRectFor(hwnd, ZoneRect(work, cell));

            // A first, synchronous step tells us whether Windows lets us move it at all.
            if (!SetWindowPos(hwnd, IntPtr.Zero, from.Left, from.Top, from.Width, from.Height, SWP_NOZORDER | SWP_NOACTIVATE))
            {
                Log.Info($"Couldn't snap a window ({Marshal.GetLastWin32Error()}); it may be running with higher rights");
                return false;
            }

            const double durationMs = 260;
            TimeSpan? start = null;
            TimeSpan last = TimeSpan.Zero;
            EventHandler? frame = null;
            frame = (_, e) =>
            {
                var now = ((System.Windows.Media.RenderingEventArgs)e).RenderingTime;
                if (now == last) return;
                last = now;
                start ??= now;
                double t = Math.Min(1, (now - start.Value).TotalMilliseconds / durationMs);
                if (!WindowApi.IsWindow(hwnd)) t = 1;
                if (t >= 1)
                {
                    System.Windows.Media.CompositionTarget.Rendering -= frame;
                    SetWindowPos(hwnd, IntPtr.Zero, to.Left, to.Top, to.Width, to.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                    return;
                }
                double k = 1 - Math.Pow(1 - t, 3.5);
                int L(int a, int b) => (int)Math.Round(a + (b - a) * k);
                int left = L(from.Left, to.Left), top = L(from.Top, to.Top);
                SetWindowPos(hwnd, IntPtr.Zero, left, top, L(from.Right, to.Right) - left, L(from.Bottom, to.Bottom) - top,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            };
            System.Windows.Media.CompositionTarget.Rendering += frame;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Snapping a window failed", ex);
            return false;
        }
    }
}
