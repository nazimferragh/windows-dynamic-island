using System;
using System.Runtime.InteropServices;

namespace DynamicIsland.Interop;

/// <summary>
/// Blurs whatever is behind a window (the frosted glass of the dock, its menus and the Apps grid).
/// Uses the accent "blur behind" that Windows' own shell surfaces use; it exists from Windows 10
/// 1507 to Windows 11. A window region gives it rounded corners. If Windows refuses, the window
/// simply shows its tint without blur.
/// </summary>
public static class GlassBlur
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State, Flags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }

    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    public static void Enable(IntPtr hwnd)
    {
        var accent = new AccentPolicy { State = 3 /* ACCENT_ENABLE_BLURBEHIND */ };
        int size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new CompositionData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, Size = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Clips the window (and its blur) to a rounded rectangle, in physical pixels.</summary>
    public static void SetRoundedRegion(IntPtr hwnd, int width, int height, int radius)
    {
        var rgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn); // on success Windows owns it
    }
}
