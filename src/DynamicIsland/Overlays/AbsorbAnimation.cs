using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
        Visual = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new Grid { Children = { face, Veil } },
            RenderTransform = Transform,
            CacheMode = new BitmapCache { RenderAtScale = 1 },
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

    private static BitmapSource Downscale(BitmapSource source)
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
/// The black hole's disk, seen almost edge-on: an orbit of radius r at angle θ around the hole is
/// at (r·cos θ, Tilt·r·sin θ). Positive sin θ is the near side (lower on screen, bigger, lit),
/// negative the far side (up behind the island, smaller, darker). Shared by eating and spitting.
/// </summary>
internal static class Disk
{
    public const double Tilt = 0.42;
    /// <summary>Turns around the hole on the way in (or out).</summary>
    public const double Turns = 1.5;

    /// <summary>Offset from the hole (DIPs) at spiral progress u (0 = outer edge, 1 = the horizon).</summary>
    public static Vector At(double u, double radius, double theta0, int dir)
    {
        double r = radius * Math.Pow(1 - u, 1.55);
        // Kepler: the closer it gets, the faster it goes round.
        double theta = theta0 + dir * 2 * Math.PI * Turns * Math.Pow(u, 1.7);
        return new Vector(r * Math.Cos(theta), Tilt * r * Math.Sin(theta));
    }

    /// <summary>
    /// The disk turns just below the island (the hole is at the very top of the screen, so a disk
    /// centred on it would be half off-screen); the final plunge rises up into the notch.
    /// </summary>
    public static Vector Drop(double u)
    {
        double k = Math.Clamp((u - 0.72) / 0.28, 0, 1);
        return new Vector(0, 34 * (1 - k * k * (3 - 2 * k)));
    }

    public static double Depth(double u, double theta0, int dir) =>
        Math.Sin(theta0 + dir * 2 * Math.PI * Turns * Math.Pow(u, 1.7));

    /// <summary>
    /// A tilt that follows the direction of travel, continuous all the way round (sin 2a is the
    /// same for a and a + 180°, so it never flips at the sides of the orbit).
    /// </summary>
    public static double Tilt2D(double u, double radius, double theta0, int dir)
    {
        var a = At(Math.Min(1, u + 0.003), radius, theta0, dir) - At(u, radius, theta0, dir);
        return 22 * Math.Sin(2 * Math.Atan2(a.Y, a.X));
    }

    /// <summary>The ring of debris: a hot, glowing ellipse around the hole (a sharp line over a soft haze).</summary>
    public static Grid MakeRing()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xBF, 0x5A, 0xF2), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xB0, 0x5A), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x5E, 0x5C, 0xE6), 1));
        brush.Freeze();
        var haze = new System.Windows.Shapes.Ellipse
        {
            Stroke = brush,
            Opacity = 0.45,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 14, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
        };
        var line = new System.Windows.Shapes.Ellipse
        {
            Stroke = brush,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 2, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
        };
        return new Grid { Opacity = 0, IsHitTestVisible = false, Children = { haze, line } };
    }

    public static void PlaceRing(Grid ring, Point center, double radius, double opacity)
    {
        double rx = Math.Max(2, radius), ry = Math.Max(2, Tilt * radius);
        ring.Width = rx * 2;
        ring.Height = ry * 2;
        Canvas.SetLeft(ring, center.X - rx);
        Canvas.SetTop(ring, center.Y - ry);
        double k = Math.Min(1, radius / 300);
        ((System.Windows.Shapes.Ellipse)ring.Children[0]).StrokeThickness = 6 + 12 * k;
        ((System.Windows.Shapes.Ellipse)ring.Children[1]).StrokeThickness = 1.5 + 2.5 * k;
        ring.Opacity = opacity;
    }

    /// <summary>
    /// Where on the disk something at offset (dx, dy) from the hole joins it (DIPs): the matching
    /// angle, kept on the near side, and the direction that swings it round the front first.
    /// </summary>
    public static (double Theta, int Dir, double Radius) Join(double dx, double dy)
    {
        double theta = Math.Atan2(Math.Max(dy, 0) / Tilt, dx);
        theta = Math.Clamp(theta, 0.3, Math.PI - 0.3);
        double radius = Math.Clamp(Math.Sqrt(dx * dx + Math.Pow(Math.Max(dy, 0) / Tilt, 2)), 330, 640);
        return (theta, dx >= 0 ? 1 : -1, radius);
    }

    /// <summary>Screen area (physical pixels) the disk can reach around the hole.</summary>
    public static WindowApi.RECT Extent(Point hole, double radiusPx, WindowApi.RECT window)
    {
        double half = Math.Max(window.Width, window.Height) * 0.3;
        return new WindowApi.RECT
        {
            Left = (int)(hole.X - radiusPx - half),
            Right = (int)(hole.X + radiusPx + half),
            Top = (int)(hole.Y - Tilt * radiusPx - half),
            Bottom = (int)(hole.Y + Tilt * radiusPx + half),
        };
    }


    /// <summary>Red when it's being stretched and slowed (redshift), then black at the horizon.</summary>
    public static Color Redshift(double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb((byte)(150 - 140 * k), (byte)(28 - 22 * k), (byte)(12 + 6 * k));
    }
}

/// <summary>
/// How a black hole eats: the window is caught by the hole's pull, falls into its tilted disk and
/// spirals around it, faster on every turn (passing behind the island on the far side), while
/// the tide stretches it into a thin streak (spaghettification), its light reddens and dies, and
/// it winks out at the event horizon. A ring of debris glows around the hole while it goes round.
/// </summary>
internal sealed class AbsorbAnimation : FlightOverlay
{
    private readonly Point _hole;
    private readonly double _radius;
    private readonly double _theta0;
    private readonly int _dir;
    private readonly Grid _ring = Disk.MakeRing();

    /// <param name="windowRect">Visible bounds of the window, physical pixels.</param>
    /// <param name="target">The hole (the notch), physical pixels.</param>
    /// <param name="dpiScale">Scale of the monitor it's on.</param>
    public AbsorbAnimation(BitmapSource? snapshot, WindowApi.RECT windowRect, Point target, double dpiScale)
        : base("Absorb", snapshot, windowRect,
            Disk.Extent(target, JoinOf(windowRect, target, dpiScale).Radius * dpiScale, windowRect),
            dpiScale, TimeSpan.FromMilliseconds(1250))
    {
        _hole = target;
        // It joins the disk where it is (on the near half) and swings round the front first.
        (_theta0, _dir, _radius) = JoinOf(windowRect, target, dpiScale);
        Stage.Children.Insert(0, _ring);
    }

    private static (double Theta, int Dir, double Radius) JoinOf(WindowApi.RECT window, Point hole, double dpiScale) =>
        Disk.Join((window.Left + window.Width / 2.0 - hole.X) / dpiScale, (window.Top + window.Height / 2.0 - hole.Y) / dpiScale);

    protected override void Apply(double t)
    {
        double w = W, h = H;
        var hole = Local(_hole.X, _hole.Y);

        // Caught: it lets go of where it was and is drawn into the disk.
        double caught = Smoothstep(0.02, 0.32, t);
        double u = Math.Clamp((t - 0.05) / 0.88, 0, 1);
        var orbit = hole + Disk.Drop(u) + Disk.At(u, _radius, _theta0, _dir);
        double depth = Disk.Depth(u, _theta0, _dir);
        double cx = Lerp(w / 2, orbit.X, caught), cy = Lerp(h / 2, orbit.Y, caught);

        // Size: far smaller once in orbit, bigger on the near side, smaller on the far side.
        double orbitScale = 0.5 * Math.Pow(1 - u, 1.15) * (1 + 0.3 * depth);
        double size = Lerp(1, orbitScale, caught);

        // Spaghettification: stretched along the orbit, squeezed across it, worse the deeper it goes.
        double tide = Math.Pow(u, 1.35);
        double sx = size * (1 + 2.8 * tide);
        double sy = size / (1 + 4.5 * tide);
        double degrees = Disk.Tilt2D(u, _radius, _theta0, _dir) * caught;
        Place(cx, cy, sx, sy, degrees);

        // Its light reddens, then goes out; the far side is in shadow.
        VeilBrush.Color = Disk.Redshift(Smoothstep(0.4, 0.97, t));
        Veil.Opacity = Math.Clamp(0.85 * Smoothstep(0.2, 0.92, t) + 0.3 * Math.Max(0, -depth) * caught, 0, 0.95);
        Visual.Opacity = 1 - Smoothstep(0.9, 1, t);

        // The debris ring: brightest while it's going round, collapsing into the hole with it.
        double ringR = _radius * Math.Pow(1 - u, 1.55) + 16;
        Disk.PlaceRing(_ring, ToStage(_hole.X, _hole.Y) + Disk.Drop(u), ringR, 0.95 * Math.Sin(Math.PI * Smoothstep(0.04, 0.99, t)));
    }
}

/// <summary>
/// The reverse, how it's let back out: the window is thrown out of the hole as a red-hot streak,
/// spiralling outward through the disk and slowing down, un-stretching and regaining its color,
/// then it breaks away and lands on its spot with a soft spring. Or, when the user dragged it out,
/// the card they're holding grows into the window. The overlay then holds over the real window
/// (so there's never a blank frame while the app repaints) and fades away.
/// </summary>
internal sealed class EmergeAnimation : FlightOverlay
{
    private readonly WindowApi.RECT _from;
    private readonly bool _fromNotch;
    private readonly double _radius;
    private readonly double _theta0;
    private readonly int _dir;
    private readonly Grid? _ring;

    /// <param name="from">Where it starts: the notch (a point-sized rect) or the dragged card, physical pixels.</param>
    /// <param name="to">The window's final visible bounds, physical pixels.</param>
    public EmergeAnimation(BitmapSource? snapshot, WindowApi.RECT from, WindowApi.RECT to, bool fromNotch, double dpiScale)
        : base("Emerge", snapshot, to,
            fromNotch ? Disk.Extent(new Point(from.Left, from.Top), JoinOf(to, from, dpiScale).Radius * dpiScale, to) : from,
            dpiScale, TimeSpan.FromMilliseconds(fromNotch ? 1000 : 380))
    {
        _from = from;
        _fromNotch = fromNotch;
        // Spiralling out backwards along the disk, so it leaves on the side where it's going.
        (_theta0, _dir, _radius) = JoinOf(to, from, dpiScale);
        if (fromNotch)
        {
            _ring = Disk.MakeRing();
            Stage.Children.Insert(0, _ring);
        }
    }

    private static (double Theta, int Dir, double Radius) JoinOf(WindowApi.RECT window, WindowApi.RECT hole, double dpiScale)
    {
        var j = Disk.Join((window.Left + window.Width / 2.0 - hole.Left) / dpiScale, (window.Top + window.Height / 2.0 - hole.Top) / dpiScale);
        return (j.Theta, j.Dir, j.Radius * 0.85);
    }

    /// <summary>A lightly underdamped spring from 0 to 1: fast out, a whisper of overshoot, settled at t = 1.</summary>
    private static double Spring(double t, double damping, double omega)
    {
        if (t >= 1) return 1;
        double v = 1 - Math.Exp(-damping * t) * (Math.Cos(omega * t) + damping / omega * Math.Sin(omega * t));
        // Blend the tiny remaining error out over the last stretch, so the end is exact.
        return Lerp(v, 1, Smoothstep(0.85, 1, t));
    }

    protected override void Apply(double t)
    {
        double w = W, h = H;
        double s = Scale;

        if (_fromNotch)
        {
            var hole = Local(_from.Left, _from.Top);
            // Out through the disk: the spiral runs backwards (horizon → outer edge), slowing down.
            double u = 1 - Smoothstep(0, 0.62, t);
            var orbit = hole + Disk.Drop(u) + Disk.At(u, _radius, _theta0, _dir);
            double depth = Disk.Depth(u, _theta0, _dir);
            // Then it breaks away and lands, with a little spring.
            double land = Spring(Math.Clamp((t - 0.32) / 0.68, 0, 1), 7, 12);
            double cx = Lerp(orbit.X, w / 2, land), cy = Lerp(orbit.Y, h / 2, land);

            double orbitScale = 0.5 * Math.Pow(1 - u, 1.15) * (1 + 0.3 * depth);
            double size = Lerp(orbitScale, 1, land);
            double tide = Math.Pow(u, 1.35) * (1 - land);
            double sx = size * (1 + 2.8 * tide);
            double sy = size / (1 + 4.5 * tide);
            double degrees = Disk.Tilt2D(Math.Max(0, u - 0.003), _radius, _theta0, _dir) * (1 - land);
            Place(cx, cy, Math.Max(0.004, sx), Math.Max(0.004, sy), degrees);

            VeilBrush.Color = Disk.Redshift(1 - Smoothstep(0, 0.55, t));
            Veil.Opacity = Math.Clamp(0.9 * (1 - Smoothstep(0.15, 0.7, t)) + 0.25 * Math.Max(0, -depth) * (1 - land), 0, 0.95);
            Visual.Opacity = Smoothstep(0, 0.07, t);

            double ringR = _radius * Math.Pow(1 - u, 1.55) + 16;
            if (_ring != null) Disk.PlaceRing(_ring, ToStage(_from.Left, _from.Top) + Disk.Drop(u), ringR, 0.9 * Math.Sin(Math.PI * Smoothstep(0, 0.7, t)));
        }
        else
        {
            // From the dragged card: it expands from the card into the full window.
            var start = Local(_from.Left + _from.Width / 2.0, _from.Top + _from.Height / 2.0);
            double fromW = _from.Width / s, fromH = _from.Height / s;
            double k = Spring(t, 9, 14);
            double sx = Lerp(fromW / Math.Max(1, w), 1, k);
            double sy = Lerp(fromH / Math.Max(1, h), 1, k);
            Place(Lerp(start.X, w / 2, k), Lerp(start.Y, h / 2, k), sx, sy, 0);
            Veil.Opacity = 0;
            Visual.Opacity = Lerp(0.92, 1, t);
        }
    }

    protected override void OnLanded()
    {
        if (_ring != null) _ring.Opacity = 0;
        // The real window has just been shown underneath; give it a beat to paint, then fade away.
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170))
        {
            BeginTime = TimeSpan.FromMilliseconds(90),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => Close();
        Visual.BeginAnimation(OpacityProperty, fade);
    }
}
