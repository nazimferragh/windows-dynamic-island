using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>An icon for the dock, and how much of its square it should fill.</summary>
public sealed record DockIcon(ImageSource Source, double Fill);

/// <summary>
/// Icons for the dock. First choice: a picture the user put in the DockIcons folder (e.g. Apple's
/// real icons: finder.png, trash.png, chrome.png… matched by item, program or app name; kept on the
/// user's PC, never shipped). Then the dock's own macOS-style drawings for Finder, Apps, Settings,
/// Downloads and Trash, then the app's own Windows icon.
/// </summary>
public static class DockIcons
{
    public static readonly string Folder = Path.Combine(Log.LogDirectory, "DockIcons");
    private static readonly Dictionary<string, DockIcon?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static DockIcon? For(DockEntry e, bool trashFull = false)
    {
        string special = e.Special == DockModel.Trash && trashFull ? "trash-full" : e.Special;
        string key = special.Length > 0 ? "special:" + special : e.Id;
        if (Cache.TryGetValue(key, out var hit)) return hit;

        var names = new List<string>();
        if (special.Length > 0) names.Add(special);
        if (e.ExePath.Length > 0) names.Add(Path.GetFileNameWithoutExtension(e.ExePath));
        if (e.Pin != null) names.Add(e.Pin.Name);
        names.Add(e.Name);
        var icon = FromFolder(names);
        if (icon == null && special.Length > 0) icon = Builtin(special);
        if (icon == null)
        {
            string parsing = e.Pin is { Kind: "app" } p ? @"shell:AppsFolder\" + p.Target
                : e.Pin is { Kind: "file" } f ? f.Target
                : e.Aumid.Length > 0 && !e.Aumid.Contains('\\') && e.ExePath.Length == 0 ? @"shell:AppsFolder\" + e.Aumid
                : e.ExePath;
            var bmp = parsing.Length > 0 ? ShellIcons.Get(parsing, 256) : null;
            if (bmp == null && e.Aumid.Length > 0) bmp = ShellIcons.Get(@"shell:AppsFolder\" + e.Aumid, 256);
            if (bmp != null) icon = new DockIcon(bmp, 0.84);
        }
        Cache[key] = icon;
        return icon;
    }

    /// <summary>For the Apps grid.</summary>
    public static DockIcon? ForInstalled(InstalledApp app)
    {
        string key = "installed:" + app.Id;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var icon = FromFolder(new[] { app.Name, app.ExePath.Length > 0 ? Path.GetFileNameWithoutExtension(app.ExePath) : "" });
        if (icon == null && ShellIcons.Get(@"shell:AppsFolder\" + app.Id, 256) is { } bmp) icon = new DockIcon(bmp, 0.84);
        Cache[key] = icon;
        return icon;
    }

    public static void ClearCache() => Cache.Clear();

    private static DockIcon? FromFolder(IEnumerable<string> names)
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
                foreach (var ext in new[] { ".png", ".ico", ".jpg" })
                {
                    var path = Path.Combine(Folder, name.Trim() + ext);
                    if (!File.Exists(path)) continue;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(path);
                    img.DecodePixelWidth = 256;
                    img.EndInit();
                    img.Freeze();
                    return new DockIcon(img, 1.0);
                }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read a dock icon", ex);
        }
        return null;
    }

    // ---------------------------------------------------------------- built-in drawings (100×100, macOS grid)

    private static readonly Geometry Squircle = MakeSquircle(6, 6, 88);

    private static Geometry MakeSquircle(double x0, double y0, double s, double n = 5, int pts = 160)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pts; i++)
        {
            double a = i / (double)pts * Math.PI * 2, c = Math.Cos(a), si = Math.Sin(a);
            double x = Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / n), y = Math.Sign(si) * Math.Pow(Math.Abs(si), 2 / n);
            sb.Append(i == 0 ? "M" : "L").Append(F(x0 + s / 2 + x * s / 2)).Append(' ').Append(F(y0 + s / 2 + y * s / 2));
        }
        var g = Geometry.Parse(sb.Append('Z').ToString());
        g.Freeze();
        return g;
    }

    private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Brush V(string a, string b) => new LinearGradientBrush(C(a), C(b), new Point(0, 0), new Point(0, 1));
    private static Brush S(string hex) => new SolidColorBrush(C(hex));

    private static DockIcon? Builtin(string id)
    {
        var g = new DrawingGroup();
        // Keeps the drawing's bounds at 0..100 so every icon lines up the same.
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
        void Add(Brush? fill, string path, Pen? pen = null) => g.Children.Add(new GeometryDrawing(fill, pen, Geometry.Parse(path)));
        void Tile(Brush fill, Action body)
        {
            g.Children.Add(new GeometryDrawing(fill, null, Squircle));
            var inner = new DrawingGroup { ClipGeometry = Squircle };
            var outer = g;
            g = inner;
            body();
            g = outer;
            g.Children.Add(inner);
            g.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x1F, 0, 0, 0)), 0.6), Squircle));
        }

        switch (id)
        {
            case DockModel.Finder:
                Tile(V("#6FD0FF", "#1F6FE6"), () =>
                {
                    Add(V("#FBFCFE", "#D5DFEB"), "M55 0C54 18 48 30 44 43C43 47 44 49 49 50C47 62 49 78 57 100L100 100L100 0Z");
                    Add(S("#17202D"), "M32 32.7A2.75 2.75 0 0 1 37.5 32.7V40.3A2.75 2.75 0 0 1 32 40.3Z");
                    Add(S("#17202D"), "M64 32.7A2.75 2.75 0 0 1 69.5 32.7V40.3A2.75 2.75 0 0 1 64 40.3Z");
                    Add(null, "M26 65Q50 82 76 63", new Pen(S("#17202D"), 3.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                });
                break;
            case DockModel.AppsGrid:
                Tile(V("#FDFDFE", "#D9D9E0"), () =>
                {
                    string[] colors = { "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#64D2FF", "#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F" };
                    for (int i = 0; i < 9; i++)
                        g.Children.Add(new GeometryDrawing(S(colors[i]), null,
                            new RectangleGeometry(new Rect(23 + i % 3 * 19.5, 23 + i / 3 * 19.5, 15, 15), 4.5, 4.5)));
                });
                break;
            case DockModel.Settings:
                Tile(V("#C9C9CE", "#77777D"), () =>
                {
                    Add(V("#55555B", "#2A2A2E"), Gear(14, 35, 30));
                    Add(S("#8B8B92"), "M26 50A24 24 0 1 0 74 50A24 24 0 1 0 26 50Z");
                    Add(V("#55555B", "#2A2A2E"), "M29 50A21 21 0 1 0 71 50A21 21 0 1 0 29 50Z");
                    Add(S("#9A9AA1"), Gear(8, 15, 12));
                    Add(S("#3A3A3E"), "M44 50A6 6 0 1 0 56 50A6 6 0 1 0 44 50Z");
                });
                break;
            case DockModel.Downloads:
                Add(S("#4AA3EE"), "M10 30Q10 23 17 23L38 23L44 29L83 29Q90 29 90 36L90 78Q90 85 83 85L17 85Q10 85 10 78Z");
                Add(V("#94D6FF", "#55B0F6"), "M10 41Q10 35 16 35L84 35Q90 35 90 41L90 78Q90 85 83 85L17 85Q10 85 10 78Z");
                Add(null, "M50 46V69M40.5 60L50 69.5L59.5 60", new Pen(S("#2A84D6"), 4.5)
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                break;
            case DockModel.Trash:
            case "trash-full":
                var body = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                body.GradientStops.Add(new GradientStop(Color.FromArgb(0xEB, 0xCF, 0xD3, 0xDA), 0));
                body.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), 0.45));
                body.GradientStops.Add(new GradientStop(Color.FromArgb(0xEB, 0xB5, 0xBA, 0xC3), 1));
                if (id == "trash-full")
                {
                    Add(S("#F4F4F2"), "M30 26L40 12L52 20L58 9L70 18L72 26Z", new Pen(S("#C8C8C4"), 0.8));
                    Add(S("#E1E6EE"), "M38 26L46 15L60 22L56 26Z");
                }
                Add(body, "M26 25L74 25L68.5 88Q67.5 93.5 62 93.5L38 93.5Q32.5 93.5 31.5 88Z", new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), 0.8));
                Add(null, "M37.5 31L40 88M45.5 31L46.7 88M54.5 31L53.3 88M62.5 31L60 88", new Pen(new SolidColorBrush(Color.FromArgb(0x21, 0, 0, 0)), 1.6));
                Add(S("#ECEEF2"), "M25.5 25A24.5 4.2 0 1 0 74.5 25A24.5 4.2 0 1 0 25.5 25Z", new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 0.8));
                break;
            default:
                return null;
        }
        g.Freeze();
        var img = new DrawingImage(g);
        img.Freeze();
        return new DockIcon(img, 1.0);
    }

    private static string Gear(int teeth, double ro, double ri)
    {
        var sb = new StringBuilder();
        int n = teeth * 4;
        for (int i = 0; i < n; i++)
        {
            double a = i / (double)n * Math.PI * 2 + Math.PI / n, r = i % 4 < 2 ? ro : ri;
            sb.Append(i == 0 ? "M" : "L").Append(F(50 + r * Math.Cos(a))).Append(' ').Append(F(50 + r * Math.Sin(a)));
        }
        return sb.Append('Z').ToString();
    }
}
