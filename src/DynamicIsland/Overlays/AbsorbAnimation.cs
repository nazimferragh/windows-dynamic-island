using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Interop;

namespace DynamicIsland.Overlays;

/// <summary>
/// A click-through overlay that sits exactly on top of a window's snapshot and then pulls it into
/// the notch like gravity: a soft settle, then it accelerates in, narrowing into a stream as it
/// nears the event horizon and dissolving as it crosses. Driven per frame (not storyboards) so the
/// path, squeeze and fade stay in lockstep. The real window is hidden underneath.
/// </summary>
internal sealed class AbsorbAnimation : Window
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(560);

    /// <summary>Snapshots are drawn downscaled: it looks identical in motion and keeps every frame cheap.</summary>
    private const int MaxSnapshotSide = 1100;

    private readonly WindowApi.RECT _windowRect;
    private readonly WindowApi.RECT _bounds;
    private readonly Point _target;
    private readonly FrameworkElement _visual;
    private readonly MatrixTransform _transform = new();
    private readonly Stopwatch _clock = new();
    private IntPtr _hwnd;
    private bool _playing;
    private int _frames;
    private double _lastMs, _worstGapMs;

    /// <param name="windowRect">Visible bounds of the window, physical pixels.</param>
    /// <param name="target">Point to swallow it into (the notch), physical pixels.</param>
    /// <param name="dpiScale">Scale of the monitor it's on, used for the initial size.</param>
    public AbsorbAnimation(BitmapSource? snapshot, WindowApi.RECT windowRect, Point target, double dpiScale)
    {
        _windowRect = windowRect;
        _target = target;
        const int pad = 40;
        _bounds = new WindowApi.RECT
        {
            Left = (int)Math.Min(windowRect.Left, target.X) - pad,
            Top = (int)Math.Min(windowRect.Top, target.Y) - pad,
            Right = (int)Math.Max(windowRect.Right, target.X) + pad,
            Bottom = (int)Math.Max(windowRect.Bottom, target.Y) + pad,
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

        if (snapshot != null)
        {
            var image = new Image { Source = Downscale(snapshot), Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
            _visual = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Child = image,
            };
        }
        else
        {
            _visual = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)), CornerRadius = new CornerRadius(12) };
        }
        _visual.RenderTransform = _transform;
        _visual.CacheMode = new BitmapCache { RenderAtScale = 1 };
        Content = new Canvas { Children = { _visual } };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
            Layout();
        };
        Closed += (_, _) => Stop();
    }

    public event Action? Finished;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Layout();
    }

    private void Layout()
    {
        if (_hwnd == IntPtr.Zero) return;
        WindowApi.PlaceTopmost(_hwnd, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height);
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Canvas.SetLeft(_visual, (_windowRect.Left - _bounds.Left) / s);
        Canvas.SetTop(_visual, (_windowRect.Top - _bounds.Top) / s);
        _visual.Width = _windowRect.Width / s;
        _visual.Height = _windowRect.Height / s;
    }

    public void Play()
    {
        if (_playing) return;
        _playing = true;
        _clock.Restart();
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
        double ms = _clock.Elapsed.TotalMilliseconds;
        if (_frames++ > 0) _worstGapMs = Math.Max(_worstGapMs, ms - _lastMs);
        _lastMs = ms;
        double t = Math.Clamp(ms / Duration.TotalMilliseconds, 0, 1);
        Apply(t);
        if (t < 1) return;

        Stop();
        DynamicIsland.Services.Log.Info($"Absorb animation: {_frames} frames in {ms:0} ms, worst gap {_worstGapMs:0} ms");
        Finished?.Invoke();
        Close();
    }

    /// <summary>Places the snapshot for progress t (0..1).</summary>
    private void Apply(double t)
    {
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double w = _windowRect.Width / s, h = _windowRect.Height / s;

        // Where the window's center travels to (in the snapshot's own coordinates).
        double toX = (_target.X - _windowRect.Left) / s - w / 2;
        double toY = (_target.Y - _windowRect.Top) / s - h / 2;

        // Gravity: barely moves at first, then falls in faster and faster (a smooth ease-in with no
        // corner at the end). The first ~12% is a gentle settle, a slight shrink that reads as
        // "it's been caught".
        double pull = Math.Pow(t, 2.6);
        double settle = Math.Sin(Math.Min(t / 0.24, 1) * Math.PI) * 0.025;

        // Overall size falls with the pull; closer to the hole, width narrows faster than height,
        // so the window stretches into a stream as it goes in.
        double size = 1 - 0.985 * Math.Pow(t, 1.7) - settle;
        double sx = size * (1 - 0.45 * Math.Sin(Math.PI * Math.Min(1, t * 1.15)) * t);
        double sy = size * (1 + 0.18 * Math.Sin(Math.PI * t));

        // A slight curve on the way (falls in from the side instead of a flat straight line).
        double curve = Math.Sin(Math.PI * pull) * Math.Min(Math.Abs(toX) * 0.08, 60) * -Math.Sign(toX);

        double cx = w / 2 + toX * pull + curve;
        double cy = h / 2 + toY * pull;

        // Scale around the window's own center, then move that center along the path.
        var m = Matrix.Identity;
        m.Translate(-w / 2, -h / 2);
        m.Scale(sx, sy);
        m.Translate(cx, cy);
        _transform.Matrix = m;

        // Stay solid while it travels, dissolve as it crosses the event horizon.
        double fade = Math.Clamp((t - 0.62) / 0.38, 0, 1);
        _visual.Opacity = 1 - fade * fade * (3 - 2 * fade);
    }

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
