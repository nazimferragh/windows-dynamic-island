using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Interop;

/// <summary>
/// Large, alpha-correct icons for anything the shell can name: a file path, or an installed app
/// ("shell:AppsFolder\&lt;id&gt;", which covers both desktop programs and Store apps). Uses
/// IShellItemImageFactory, available since Windows Vista, so it behaves the same on 10 and 11.
/// Call from an STA thread.
/// </summary>
public static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx, Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    /// <summary>The icon for a parsing name (path or shell:AppsFolder\id), or null.</summary>
    public static BitmapSource? Get(string parsingName, int size)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var obj) != 0) return null;
            var factory = (IShellItemImageFactory)obj;
            if (factory.GetImage(new SIZE { Cx = size, Cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0) return null;
            Marshal.ReleaseComObject(factory);
            return ToBitmapSource(hbm);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
        }
    }

    /// <summary>Copies the HBITMAP's pixels top-down with its alpha channel (CreateBitmapSourceFromHBitmap drops alpha).</summary>
    private static BitmapSource? ToBitmapSource(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.Width <= 0 || bm.Height <= 0) return null;
        var header = new BITMAPINFOHEADER
        {
            Size = Marshal.SizeOf<BITMAPINFOHEADER>(),
            Width = bm.Width,
            Height = -bm.Height, // negative: top-down rows
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[bm.Width * bm.Height * 4];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)bm.Height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
        // Some icons come without an alpha channel (all zero): treat those as fully opaque.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = BitmapSource.Create(bm.Width, bm.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, bm.Width * 4);
        source.Freeze();
        return source;
    }
}
