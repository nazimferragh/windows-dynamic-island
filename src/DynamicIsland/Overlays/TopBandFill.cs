using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>
/// Fills the reserved band beside the island with the color of the maximized app's own top edge
/// (owner's pick "F" in the Top Band Lab preview, https://claude.ai/artifact/SHDEGvLL7DyyMBUWB35ZpD),
/// so the app seems to reach up to the island. Over the desktop it's hidden and the wallpaper shows.
/// Click-through, never focused, stays below the island. Cheap: one 1-pixel-high screen read
/// every ~0.4 s and only while a maximized window is on this screen; nothing at all in game mode.
/// </summary>
internal sealed class TopBandFill : Window
{
    private readonly Int32Rect _monitor;
    private readonly SolidColorBrush _fill = new(Colors.Black);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Func<(int Top, int Bottom)> _band;
    private IntPtr _hwnd;
    private bool _shown;
    private Color _color;

    /// <param name="band">The reserved band (physical pixels); the app starts at its bottom.</param>
    public TopBandFill(Int32Rect monitor, Func<(int Top, int Bottom)> band)
    {
        _monitor = monitor;
        _band = band;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        // Must be true: ShowInTaskbar=false gives the window a hidden owner, which pins it to one
        // virtual desktop. The tool-window style set below keeps it out of the taskbar instead.
        ShowInTaskbar = true;
        ShowActivated = false;
        Topmost = true;
        Width = 100;
        Height = 32;
        Opacity = 0;
        Content = new System.Windows.Controls.Border { Background = _fill };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
            Place();
        };
        _poll.Tick += (_, _) => Update();
        GameMode.Tune(_poll, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));
        Closed += (_, _) => _poll.Stop();
    }

    /// <summary>Raised after the fill appeared, so the island can put itself back on top.</summary>
    public event Action? Raised;

    private WindowApi.RECT MonitorRect => new()
    {
        Left = _monitor.X,
        Top = _monitor.Y,
        Right = _monitor.X + _monitor.Width,
        Bottom = _monitor.Y + _monitor.Height,
    };

    public void Start()
    {
        Show();
        _poll.Start();
        Update();
    }

    /// <summary>Hides the fill until <see cref="Start"/> (island hidden by the user).</summary>
    public void Stop()
    {
        _poll.Stop();
        SetShown(false);
    }

    /// <summary>Covers the band, down to where the app starts.</summary>
    public void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var (top, bottom) = _band();
        WindowApi.PlaceTopmost(_hwnd, _monitor.X, top, _monitor.Width, Math.Max(1, bottom - top));
    }

    private void Update()
    {
        // In a full-screen game the band is ignored and the island floats over the game.
        bool want = !GameMode.IsOn(_monitor) && WindowApi.HasMaximizedWindowOn(MonitorRect);
        if (want && SampleAppTop() is { } color) SetColor(color);
        SetShown(want);
    }

    /// <summary>
    /// The most common color along the app's first pixel row (its title bar / tab strip background),
    /// skipping the island's middle and the caption buttons on the right, which light up on hover.
    /// </summary>
    private Color? SampleAppTop()
    {
        try
        {
            int y = _band().Bottom + 1;
            int w = _monitor.Width;
            using var row = new System.Drawing.Bitmap(w, 1, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(row))
                g.CopyFromScreen(_monitor.X, y, 0, 0, new System.Drawing.Size(w, 1));

            var counts = new Dictionary<int, int>();
            int skipFrom = w / 2 - 420, skipTo = w / 2 + 420, end = w - 220;
            for (int x = 6; x < end; x += 24)
            {
                if (x > skipFrom && x < skipTo) continue;
                int argb = row.GetPixel(x, 0).ToArgb();
                counts[argb] = counts.TryGetValue(argb, out int n) ? n + 1 : 1;
            }
            int best = 0, bestCount = 0;
            foreach (var (argb, n) in counts)
                if (n > bestCount) { best = argb; bestCount = n; }
            if (bestCount == 0) return null;
            var c = System.Drawing.Color.FromArgb(best);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return null; // secure desktop, lock screen…: keep the last color
        }
    }

    private void SetColor(Color color)
    {
        if (color == _color) return;
        bool first = _color == default;
        _color = color;
        if (first || !_shown || GameMode.Active)
        {
            _fill.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _fill.Color = color;
        }
        else _fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(160)));
    }

    private void SetShown(bool shown)
    {
        if (shown == _shown) return;
        _shown = shown;
        if (shown)
        {
            Place();
            Raised?.Invoke();
        }
        BeginAnimation(OpacityProperty, GameMode.Active ? null : new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(160)));
        if (GameMode.Active) Opacity = shown ? 1 : 0;
    }
}
