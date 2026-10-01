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
/// The island sucking a window in like a vacuum (the "genie" effect, into the notch). The snapshot
/// is cut into thin horizontal strips; the strips nearest the island are pulled in first and the
/// rest follow a moment later, each accelerating as it goes. So the window narrows into a neck at
/// the island's mouth, the rest of it pours up through it, and it disappears into the black.
/// Run backwards, the same funnel pours a window back out of the island.
/// </summary>
internal sealed class Funnel
{
    private const int Strips = 72;

    private readonly System.Windows.Shapes.Rectangle[] _strips = new System.Windows.Shapes.Rectangle[Strips];
    private readonly MatrixTransform[] _transforms = new MatrixTransform[Strips];
    private readonly double[] _y = new double[Strips + 1], _w = new double[Strips + 1], _x = new double[Strips + 1], _p = new double[Strips + 1];

    public Funnel(Canvas stage, BitmapSource? snapshot)
    {
        Brush? whole = snapshot == null ? new SolidColorBrush(Color.FromRgb(28, 28, 30)) : null;
        for (int i = 0; i < Strips; i++)
        {
            Brush fill = whole ?? new ImageBrush(snapshot)
            {
                Viewbox = new Rect(0, (double)i / Strips, 1, 1.0 / Strips),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.Fill,
            };
            if (fill.CanFreeze) fill.Freeze();
            _transforms[i] = new MatrixTransform();
            // A fixed 100×100 rectangle placed by its transform only: no layout pass per frame.
            _strips[i] = new System.Windows.Shapes.Rectangle { Width = 100, Height = 100, Fill = fill, RenderTransform = _transforms[i], IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(_strips[i], BitmapScalingMode.Linear);
            stage.Children.Add(_strips[i]);
        }
    }

    /// <summary>
    /// Draws the funnel at suction progress t (0 = the window untouched, 1 = all of it inside).
    /// Window and mouth in stage DIPs; the mouth is the island's center, its width the opening.
    /// </summary>
    public void Apply(double t, Rect window, Point mouth, double mouthWidth)
    {
        // Each row has its own clock: the top row (nearest the island) goes first, the bottom row
        // starts last. Rows accelerate (a vacuum pull), and narrow faster than they rise, which
        // forms the neck.
        const double lag = 0.45;
        for (int e = 0; e <= Strips; e++)
        {
            double v = (double)e / Strips;
            double p = Math.Clamp((t - lag * Math.Pow(v, 0.85)) / (1 - lag), 0, 1);
            double rise = Math.Pow(p, 2.1);
            double narrow = 1 - Math.Pow(1 - p, 2.2);
            double deep = Math.Clamp((rise - 0.8) / 0.2, 0, 1); // inside the island, squeeze to a thread
            _p[e] = rise;
            _y[e] = window.Top + v * window.Height + (mouth.Y - (window.Top + v * window.Height)) * rise;
            _w[e] = (window.Width + (mouthWidth - window.Width) * narrow) * (1 - 0.8 * deep);
            _x[e] = window.Left + window.Width / 2 + (mouth.X - (window.Left + window.Width / 2)) * (1 - Math.Pow(1 - p, 1.6));
        }
        for (int i = 0; i < Strips; i++)
        {
            double top = _y[i], height = Math.Max(0.6, _y[i + 1] - _y[i] + 0.6); // a hair of overlap: no seams
            double width = Math.Max(0.5, (_w[i] + _w[i + 1]) / 2), cx = (_x[i] + _x[i + 1]) / 2;
            _transforms[i].Matrix = new Matrix(width / 100, 0, 0, height / 100, cx - width / 2, top);
            // Light dies as it goes into the black.
            double gone = (_p[i] + _p[i + 1]) / 2;
            _strips[i].Opacity = 1 - Math.Clamp((gone - 0.82) / 0.18, 0, 1);
        }
    }
}

/// <summary>The island eats a window: sucked up through its mouth like a vacuum, into the black.</summary>
internal sealed class AbsorbAnimation : FlightOverlay
{
    private readonly Point _mouth;
    private readonly Funnel _funnel;

    /// <param name="windowRect">Visible bounds of the window, physical pixels.</param>
    /// <param name="target">The island's mouth (center of the notch), physical pixels.</param>
    /// <param name="dpiScale">Scale of the monitor it's on.</param>
    public AbsorbAnimation(BitmapSource? snapshot, WindowApi.RECT windowRect, Point target, double dpiScale)
        : base("Absorb", snapshot, windowRect, PointRect(target), dpiScale, TimeSpan.FromMilliseconds(720))
    {
        _mouth = target;
        Visual.Visibility = Visibility.Hidden;
        _funnel = new Funnel(Stage, snapshot == null ? null : Downscale(snapshot));
    }

    /// <summary>How wide the island's mouth opens while it eats (DIPs).</summary>
    public const double MouthWidth = 150;

    protected override void Apply(double t)
    {
        var tl = ToStage(Home.Left, Home.Top);
        _funnel.Apply(t, new Rect(tl.X, tl.Y, W, H), ToStage(_mouth.X, _mouth.Y), MouthWidth);
    }
}

/// <summary>
/// The island lets a window back out: it pours down out of the notch through the same funnel and
/// opens up onto its spot. Or, when the user dragged it out, the card they're holding grows into
/// the window. The overlay then holds over the real window (so there's never a blank frame while
/// the app repaints) and fades away.
/// </summary>
internal sealed class EmergeAnimation : FlightOverlay
{
    private readonly WindowApi.RECT _from;
    private readonly bool _fromNotch;
    private readonly Funnel? _funnel;

    /// <param name="from">Where it starts: the island's mouth (a point-sized rect) or the dragged card, physical pixels.</param>
    /// <param name="to">The window's final visible bounds, physical pixels.</param>
    public EmergeAnimation(BitmapSource? snapshot, WindowApi.RECT from, WindowApi.RECT to, bool fromNotch, double dpiScale)
        : base("Emerge", snapshot, to, from, dpiScale, TimeSpan.FromMilliseconds(fromNotch ? 640 : 380))
    {
        _from = from;
        _fromNotch = fromNotch;
        if (fromNotch)
        {
            Visual.Visibility = Visibility.Hidden;
            _funnel = new Funnel(Stage, snapshot == null ? null : Downscale(snapshot));
        }
    }

    /// <summary>A lightly underdamped spring from 0 to 1: fast out, a whisper of overshoot, settled at t = 1.</summary>
    private static double Spring(double t, double damping, double omega)
    {
        if (t >= 1) return 1;
        double v = 1 - Math.Exp(-damping * t) * (Math.Cos(omega * t) + damping / omega * Math.Sin(omega * t));
        return Lerp(v, 1, Smoothstep(0.85, 1, t));
    }

    protected override void Apply(double t)
    {
        if (_funnel != null)
        {
            // The suction played backwards: the neck forms at the mouth and the window pours out
            // and opens up, the bottom rows landing last, decelerating into place.
            var tl = ToStage(Home.Left, Home.Top);
            _funnel.Apply(1 - t, new Rect(tl.X, tl.Y, W, H), ToStage(_from.Left, _from.Top), AbsorbAnimation.MouthWidth);
            return;
        }

        // From the dragged card: it expands from the card into the full window.
        double w = W, h = H, s = Scale;
        var start = Local(_from.Left + _from.Width / 2.0, _from.Top + _from.Height / 2.0);
        double fromW = _from.Width / s, fromH = _from.Height / s;
        double k = Spring(t, 9, 14);
        Place(Lerp(start.X, w / 2, k), Lerp(start.Y, h / 2, k), Lerp(fromW / Math.Max(1, w), 1, k), Lerp(fromH / Math.Max(1, h), 1, k), 0);
        Veil.Opacity = 0;
        Visual.Opacity = Lerp(0.92, 1, t);
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
        Stage.BeginAnimation(OpacityProperty, fade);
    }
}
