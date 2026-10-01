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
            Background = new SolidColorBrush(Color.FromRgb(10, 6, 22)),
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
        Content = new Canvas { Children = { Visual } };

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
/// Pulls a window into the notch like gravity: it's caught (a tiny lift and squeeze away from the
/// hole), then falls in faster and faster, swirling, stretching into a stream and darkening as the
/// light can't get back out, and dissolves as it crosses the event horizon.
/// </summary>
internal sealed class AbsorbAnimation : FlightOverlay
{
    private readonly Point _target;

    /// <param name="windowRect">Visible bounds of the window, physical pixels.</param>
    /// <param name="target">Point to swallow it into (the notch), physical pixels.</param>
    /// <param name="dpiScale">Scale of the monitor it's on, used for the initial size.</param>
    public AbsorbAnimation(BitmapSource? snapshot, WindowApi.RECT windowRect, Point target, double dpiScale)
        : base("Absorb", snapshot, windowRect, PointRect(target), dpiScale, TimeSpan.FromMilliseconds(640))
    {
        _target = target;
    }

    protected override void Apply(double t)
    {
        double w = W, h = H;
        var to = Local(_target.X, _target.Y);
        double toX = to.X - w / 2, toY = to.Y - h / 2;
        double side = toX == 0 ? 1 : Math.Sign(toX);

        // Caught: during the first beat it eases back a hair (away from the hole) and tightens,
        // like something being grabbed, then gravity takes over with no corner in between.
        double catchK = Math.Sin(Math.Min(t / 0.26, 1) * Math.PI);
        double lift = catchK * 0.035;
        double pull = Math.Pow(t, 2.5);

        // Size falls with the pull; near the hole the width narrows faster than the height, so it
        // stretches into a stream as it goes in.
        double size = 1 - 0.988 * Math.Pow(t, 1.65) - catchK * 0.02;
        double sx = size * (1 - 0.5 * Math.Pow(t, 1.4) * Math.Sin(Math.PI * Math.Min(1, t * 1.1)));
        double sy = size * (1 + 0.22 * Math.Sin(Math.PI * t) * t);

        // It swirls a little as it falls (towards the side it comes from), and curves rather than
        // travelling a flat straight line.
        double degrees = -side * 32 * Math.Pow(t, 2.2);
        double curve = Math.Sin(Math.PI * pull) * Math.Min(Math.Abs(toX) * 0.09, 70) * -side;

        double cx = w / 2 + toX * (pull - lift) + curve;
        double cy = h / 2 + toY * (pull - lift);
        Place(cx, cy, sx, sy, degrees);

        // Darkens as it nears the horizon, then dissolves as it crosses.
        Veil.Opacity = 0.82 * Smoothstep(0.3, 0.92, t);
        Visual.Opacity = 1 - Smoothstep(0.66, 1, t);
    }
}

/// <summary>
/// The reverse: a window comes back out of the island. It's spat out of the notch (or grows from
/// the card the user dragged out), unswirling and brightening, and lands on its real spot with a
/// soft spring. The overlay then holds for a moment over the real window (so there's never a blank
/// frame while the app repaints) and fades away.
/// </summary>
internal sealed class EmergeAnimation : FlightOverlay
{
    private readonly WindowApi.RECT _from;
    private readonly bool _fromNotch;

    /// <param name="from">Where it starts: the notch (a point-sized rect) or the dragged card, physical pixels.</param>
    /// <param name="to">The window's final visible bounds, physical pixels.</param>
    public EmergeAnimation(BitmapSource? snapshot, WindowApi.RECT from, WindowApi.RECT to, bool fromNotch, double dpiScale)
        : base("Emerge", snapshot, to, from, dpiScale, TimeSpan.FromMilliseconds(fromNotch ? 560 : 380))
    {
        _from = from;
        _fromNotch = fromNotch;
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
        var start = Local(_from.Left + _from.Width / 2.0, _from.Top + _from.Height / 2.0);
        double s = Scale;

        if (_fromNotch)
        {
            // Position glides out fast and settles; size springs open a touch past full and back.
            double move = 1 - Math.Pow(1 - t, 3.2);
            double grow = Spring(t, 7.5, 13);
            double stream = 1 - Smoothstep(0, 0.55, t); // starts as a narrow stream, opens up
            double sx = Math.Max(0.02, grow) * (1 - 0.45 * stream);
            double sy = Math.Max(0.02, grow) * (1 + 0.25 * stream);
            double degrees = 28 * Math.Pow(1 - Smoothstep(0, 0.75, t), 1.6) * (start.X < w / 2 ? 1 : -1);
            Place(Lerp(start.X, w / 2, move), Lerp(start.Y, h / 2, move), sx, sy, degrees);
            Veil.Opacity = 0.8 * (1 - Smoothstep(0.05, 0.55, t));
            Visual.Opacity = Smoothstep(0, 0.22, t);
        }
        else
        {
            // From the dragged card: it expands from the card into the full window.
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
