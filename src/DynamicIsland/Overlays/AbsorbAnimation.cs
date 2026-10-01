using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using DynamicIsland.Interop;

namespace DynamicIsland.Overlays;

/// <summary>
/// A click-through overlay that flies a window's snapshot between the screen and the notch. Driven
/// per frame from the compositor's own frame time (not storyboards, not a stopwatch), so the path,
/// squeeze, swirl and fade stay in lockstep and land exactly on the display's refresh.
/// </summary>
internal abstract class FlightOverlay : Window
{
    /// <summary>Snapshots are drawn downscaled: it looks identical in motion and keeps every frame cheap.</summary>
    private const int MaxSnapshotSide = 1100;

    private readonly WindowApi.RECT _bounds;
    private readonly TimeSpan _duration;
    private readonly string _name;
    private TimeSpan? _start;
    private TimeSpan _last;
    private IntPtr _hwnd;
    private bool _playing;
    private int _frames;
    private double _worstGapMs;

    /// <summary>The window's rectangle the snapshot is laid out at (physical pixels); motion is a transform on top.</summary>
    protected readonly WindowApi.RECT Home;
    protected readonly FrameworkElement Visual;
    protected readonly MatrixTransform Transform = new();
    /// <summary>A dark veil over the snapshot: light fading as it nears the event horizon.</summary>
    protected readonly Border Veil;
    /// <summary>The veil's color (it shifts from red to black: redshift, then nothing).</summary>
    protected readonly SolidColorBrush VeilBrush = new(Color.FromRgb(10, 6, 22));
    /// <summary>The overlay's canvas, for extra drawings (the accretion disk) under the snapshot.</summary>
    protected readonly Canvas Stage = new();
    private readonly RectangleGeometry _clip = new();

    protected FlightOverlay(string name, BitmapSource? snapshot, WindowApi.RECT home, WindowApi.RECT extent, double dpiScale, TimeSpan duration)
    {
        _name = name;
        _duration = duration;
        Home = home;
        int pad = (int)(70 * dpiScale);
        _bounds = new WindowApi.RECT
        {
            Left = Math.Min(home.Left, extent.Left) - pad,
            Top = Math.Min(home.Top, extent.Top) - pad,
            Right = Math.Max(home.Right, extent.Right) + pad,
            Bottom = Math.Max(home.Bottom, extent.Bottom) + pad,
        };

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true; // no hidden owner, so it appears on the current virtual desktop; the tool-window style hides the button
        ShowActivated = false;
        Topmost = true;
        Width = _bounds.Width / dpiScale;
        Height = _bounds.Height / dpiScale;

        Veil = new Border
        {
            Background = VeilBrush,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        UIElement face;
        if (snapshot != null)
        {
            var image = new Image { Source = Downscale(snapshot), Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
            face = image;
        }
        else
        {
            face = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)) };
        }
        // Only the snapshot is cached as a bitmap: the veil, clip, shadow and transform change every
        // frame without re-rendering it.
        face.CacheMode = new BitmapCache { RenderAtScale = 1 };
        Visual = new Grid
        {
            Children = { face, Veil },
            RenderTransform = Transform,
            // No shadow effect: a blur over a window-sized surface every frame was what made big
            // windows stutter. Windows' own shadow is back the moment the real window shows.
            Clip = _clip,
        };
        Stage.Children.Add(Visual);
        Content = Stage;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
            Layout();
            Apply(0);
        };
        Closed += (_, _) => Stop();
    }

    public event Action? Finished;

    protected double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>The snapshot's laid-out size, DIPs.</summary>
    protected double W => Home.Width / Scale;
    protected double H => Home.Height / Scale;

    /// <summary>A physical screen point in the overlay canvas' coordinates (DIPs).</summary>
    protected Point ToStage(double x, double y) => new((x - _bounds.Left) / Scale, (y - _bounds.Top) / Scale);

    /// <summary>A physical screen point in the snapshot's own coordinates (DIPs, relative to its top-left).</summary>
    protected Point Local(double x, double y) => new((x - Home.Left) / Scale, (y - Home.Top) / Scale);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Layout();
    }

    private void Layout()
    {
        if (_hwnd == IntPtr.Zero) return;
        WindowApi.PlaceTopmost(_hwnd, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height);
        double s = Scale;
        Canvas.SetLeft(Visual, (Home.Left - _bounds.Left) / s);
        Canvas.SetTop(Visual, (Home.Top - _bounds.Top) / s);
        Visual.Width = Home.Width / s;
        Visual.Height = Home.Height / s;
    }

    public void Play()
    {
        if (_playing) return;
        _playing = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_playing) return;
        _playing = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // RenderingTime is when this frame will be shown; WPF can raise Rendering more than once per
        // frame, so only real new frames count.
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
        if (_start == null)
        {
            _start = now;
            _last = now;
        }
        else if (now == _last)
        {
            return;
        }
        if (_frames++ > 0) _worstGapMs = Math.Max(_worstGapMs, (now - _last).TotalMilliseconds);
        _last = now;

        double ms = (now - _start.Value).TotalMilliseconds;
        double t = Math.Clamp(ms / _duration.TotalMilliseconds, 0, 1);
        Apply(t);
        if (t < 1) return;

        Stop();
        DynamicIsland.Services.Log.Info($"{_name} animation: {_frames} frames in {ms:0} ms, worst gap {_worstGapMs:0} ms");
        Finished?.Invoke();
        OnLanded();
    }

    /// <summary>Called once the motion is done. Closes the overlay unless a subclass hands off differently.</summary>
    protected virtual void OnLanded() => Close();

    /// <summary>Places the snapshot for progress t (0..1).</summary>
    protected abstract void Apply(double t);

    /// <summary>The island's live outline (physical pixels), for the morph's target.</summary>
    internal Func<Rect>? IslandRect { get; set; }

    /// <summary>Draws the morph at progress k (see <see cref="Morph.At"/>).</summary>
    internal void DrawMorph(double k)
    {
        var island = IslandRect?.Invoke() ?? Rect.Empty;
        if (island.IsEmpty) return;
        var home = new Rect(Home.Left, Home.Top, Home.Width, Home.Height);
        var (rect, radius, black) = Morph.At(k, home, island, Scale);
        PlaceRect(rect, radius);
        Veil.Opacity = black;
    }

    /// <summary>
    /// Shows the snapshot filling a screen rectangle (physical pixels) with rounded corners of the
    /// given radius (DIPs on screen; kept round whatever the scale), via one transform.
    /// </summary>
    protected void PlaceRect(Rect target, double radius)
    {
        double s = Scale, w = W, h = H;
        double sx = Math.Max(1e-4, target.Width / s / w), sy = Math.Max(1e-4, target.Height / s / h);
        double x = (target.X - Home.Left) / s, y = (target.Y - Home.Top) / s;
        Transform.Matrix = new Matrix(sx, 0, 0, sy, x, y);
        _clip.Rect = new Rect(0, 0, w, h);
        _clip.RadiusX = radius / sx;
        _clip.RadiusY = radius / sy;
    }

    /// <summary>Scale (sx, sy) and rotation (degrees) around the snapshot's center, then its center moved to (cx, cy).</summary>
    protected void Place(double cx, double cy, double sx, double sy, double degrees)
    {
        double w = W, h = H;
        var m = Matrix.Identity;
        m.Translate(-w / 2, -h / 2);
        m.Scale(Math.Max(sx, 0.0005), Math.Max(sy, 0.0005));
        if (degrees != 0) m.Rotate(degrees);
        m.Translate(cx, cy);
        Transform.Matrix = m;
    }

    protected static double Smoothstep(double a, double b, double x)
    {
        double k = Math.Clamp((x - a) / (b - a), 0, 1);
        return k * k * (3 - 2 * k);
    }

    protected static double Lerp(double a, double b, double t) => a + (b - a) * t;

    protected static WindowApi.RECT PointRect(Point p) => new() { Left = (int)p.X, Top = (int)p.Y, Right = (int)p.X + 1, Bottom = (int)p.Y + 1 };

    protected static BitmapSource Downscale(BitmapSource source)
    {
        int longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= MaxSnapshotSide) return source;
        double k = (double)MaxSnapshotSide / longest;
        var scaled = new TransformedBitmap(source, new ScaleTransform(k, k));
        var frozen = new WriteableBitmap(scaled);
        frozen.Freeze();
        return frozen;
    }
}

/// <summary>
/// The motion the owner picked in the Black Hole Lab preview (style A "Morph", speed 1.05,
/// bounce 0.60): the window slides and shrinks into the island's own black pill, its colour
/// draining to black on the way, so it becomes part of the island. Coming out is the same path
/// backwards on a spring, with a little overshoot. Keep these curves in step with the preview.
/// </summary>
internal static class Morph
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(520 / 1.05);
    private const double Bounce = 0.60;

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    public static double Smooth(double a, double b, double x)
    {
        double k = Clamp01((x - a) / (b - a));
        return k * k * (3 - 2 * k);
    }

    /// <summary>Into the island: a critically damped spring, normalised to land exactly at t = 1.</summary>
    public static double Settle(double t)
    {
        const double w = 8;
        static double F(double x) => 1 - (1 + w * x) * Math.Exp(-w * x);
        return Clamp01(F(t) / F(1));
    }

    /// <summary>Out of the island: an underdamped spring (the bounce), exact at t = 1.</summary>
    public static double Springy(double t)
    {
        if (t >= 1) return 1;
        double z = 1 + (0.55 - 1) * Bounce, w = 11;
        double wd = w * Math.Sqrt(Math.Max(1e-4, 1 - z * z));
        double v = 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + z * w / wd * Math.Sin(wd * t));
        return v + (1 - v) * Smooth(0.82, 1, t);
    }

    /// <summary>
    /// Where the window is at morph progress k (0 = its own spot, 1 = the island's pill; slightly
    /// below 0 while it overshoots on the way out). Rects in physical pixels; radius in DIPs.
    /// </summary>
    public static (Rect Rect, double Radius, double Black) At(double k, Rect window, Rect island, double scale)
    {
        // Position travels a touch ahead of size, so it reads as being drawn in, not just shrinking.
        double kp = Math.Min(1, k * 1.08), ks = k;
        var r = new Rect(
            window.X + (island.X - window.X) * kp,
            window.Y + (island.Y - window.Y) * kp,
            Math.Max(1, window.Width + (island.Width - window.Width) * ks),
            Math.Max(1, window.Height + (island.Height - window.Height) * ks));
        double islandH = island.Height / scale;
        double radius = 9 + (islandH * 0.45 - 9) * Smooth(0, 0.8, k);
        return (r, radius, Smooth(0.12, 0.62, k));
    }
}

/// <summary>The island eats a window: it morphs into the island's black pill and merges with it.</summary>
internal sealed class AbsorbAnimation : FlightOverlay
{
    /// <param name="windowRect">Visible bounds of the window, physical pixels.</param>
    /// <param name="island">The island's live outline (physical pixels); it opens up while eating.</param>
    public AbsorbAnimation(BitmapSource? snapshot, WindowApi.RECT windowRect, Func<Rect> island, double dpiScale)
        : base("Absorb", snapshot, windowRect, ToRect(island()), dpiScale, Morph.Duration)
    {
        IslandRect = island;
        VeilBrush.Color = Colors.Black;
    }

    internal static WindowApi.RECT ToRect(Rect r) => new()
    {
        Left = (int)r.Left - 200, Top = (int)r.Top, Right = (int)r.Right + 200, Bottom = (int)r.Bottom + 60,
    };

    protected override void Apply(double t) => DrawMorph(Morph.Settle(t));
}

/// <summary>
/// The island lets a window back out: the black pill grows out of the island on a spring, its
/// colour coming back, and lands on the window's spot with a little overshoot. Or, when the user
/// dragged it out, the card they're holding grows into the window. The overlay then holds over
/// the real window (so there's never a blank frame while the app repaints) and fades away.
/// </summary>
internal sealed class EmergeAnimation : FlightOverlay
{
    private readonly WindowApi.RECT _from;
    private readonly bool _fromNotch;

    /// <param name="from">The dragged card (physical pixels); ignored when it comes out of the island.</param>
    /// <param name="to">The window's final visible bounds, physical pixels.</param>
    /// <param name="island">The island's live outline (physical pixels).</param>
    public EmergeAnimation(BitmapSource? snapshot, WindowApi.RECT from, WindowApi.RECT to, bool fromNotch, Func<Rect> island, double dpiScale)
        : base("Emerge", snapshot, to, fromNotch ? AbsorbAnimation.ToRect(island()) : from, dpiScale,
            fromNotch ? Morph.Duration : TimeSpan.FromMilliseconds(380))
    {
        _from = from;
        _fromNotch = fromNotch;
        IslandRect = island;
        VeilBrush.Color = Colors.Black;
    }

    protected override void Apply(double t)
    {
        if (_fromNotch)
        {
            DrawMorph(Math.Max(-0.2, 1 - Morph.Springy(t)));
            return;
        }

        // From the dragged card: it expands from the card into the full window.
        double k = Morph.Springy(t);
        var card = new Rect(_from.Left, _from.Top, _from.Width, _from.Height);
        var home = new Rect(Home.Left, Home.Top, Home.Width, Home.Height);
        var r = new Rect(
            card.X + (home.X - card.X) * k, card.Y + (home.Y - card.Y) * k,
            Math.Max(1, card.Width + (home.Width - card.Width) * k), Math.Max(1, card.Height + (home.Height - card.Height) * k));
        PlaceRect(r, 9);
        Veil.Opacity = 0;
    }

    protected override void OnLanded()
    {
        // The real window has just been shown underneath; give it a beat to paint, then fade away.
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
        {
            BeginTime = TimeSpan.FromMilliseconds(80),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => Close();
        Stage.BeginAnimation(OpacityProperty, fade);
    }
}
