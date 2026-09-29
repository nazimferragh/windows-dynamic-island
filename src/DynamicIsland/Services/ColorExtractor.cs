using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

/// <summary>Picks a vivid accent color from album art, tuned to read well on a black island.</summary>
public static class ColorExtractor
{
    public static readonly Color DefaultAccent = Color.FromRgb(48, 209, 88);

    public static Color Extract(BitmapSource? source)
    {
        if (source == null || source.PixelWidth == 0 || source.PixelHeight == 0) return DefaultAccent;
        try
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform(16.0 / source.PixelWidth, 16.0 / source.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            // Weight each pixel by how saturated and bright it is so muddy/dark pixels don't win.
            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double pb = pixels[i] / 255.0, pg = pixels[i + 1] / 255.0, pr = pixels[i + 2] / 255.0;
                RgbToHsv(pr, pg, pb, out _, out double s, out double v);
                double weight = s * s * v + 0.002;
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
            r /= total; g /= total; b /= total;

            RgbToHsv(r, g, b, out double hue, out double sat, out double val);
            if (sat < 0.12) return Color.FromRgb(235, 235, 240); // Greyscale art: use a soft white.
            sat = Math.Clamp(sat * 1.15, 0.45, 0.9);
            val = Math.Max(val, 0.9);
            HsvToRgb(hue, sat, val, out r, out g, out b);
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
        catch (Exception ex)
        {
            Log.Error("Accent extraction failed", ex);
            return DefaultAccent;
        }
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d + 6) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
    }

    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        r += m; g += m; b += m;
    }
}
