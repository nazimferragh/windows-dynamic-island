using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Overlays;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// One island, pinned to the top-center of one monitor, styled after the macOS notch:
/// flush with the top edge, flared top corners, rounded bottom corners.
/// It also acts as a black hole: drag a window into it and it's absorbed; pull it back out later.
/// </summary>
public partial class IslandWindow : Window
{
    private enum State { Closed, Peek, Open, Attract }

    /// <summary>Body width/height, flare radius of the top corners, radius of the bottom corners.</summary>
    private readonly record struct Silhouette(double Width, double Height, double Flare, double Radius);

    private static readonly Silhouette ClosedIdleShape = new(190, 32, 6, 10);
    private static readonly Silhouette ClosedIdleHoverShape = new(204, 35, 6, 11);
    private static readonly Silhouette ClosedMediaShape = new(290, 32, 6, 10);
    private static readonly Silhouette ClosedMediaHoverShape = new(304, 35, 6, 11);
    private static readonly Silhouette ClosedVaultShape = new(250, 32, 6, 10);
    private static readonly Silhouette ClosedVaultHoverShape = new(264, 35, 6, 11);
    private static readonly Silhouette PeekShape = new(290, 58, 8, 18);
    private static readonly Silhouette OpenShape = new(640, 186, 14, 32);
    private static readonly Silhouette OpenShelfShape = new(640, 316, 14, 32);
    private static readonly Silhouette CaptureShape = new(440, 92, 12, 28);

    // Where a dragged window is pulled in / captured, in DIPs relative to the notch's top-center.
    // The capture zone is deliberately small and needs a short hold: the top of the screen is
    // busy (browser tab strips, drag-to-maximize), and a window must never be swallowed by accident.
    private const double AttractRadius = 320;
    private const double CaptureHalfWidth = 170;
    private const double CaptureDepth = 64;
    private static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(450);

    private static readonly Color HoleGlow = Color.FromRgb(150, 90, 255);

    private readonly MediaService _media;
    private readonly WindowVault _vault;
    private readonly WindowDragWatcher _dragWatcher;
    private readonly DownloadWatcher _downloads;
    private readonly Int32Rect _monitor; // physical pixels
    private readonly MenuBarStrip _strip;
    private readonly SolidColorBrush _accentBrush = new(ColorExtractor.DefaultAccent);
    private readonly DropShadowEffect _shadow = new() { Color = Colors.Black, BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0, RenderingBias = RenderingBias.Performance };

    private readonly Spring _width = new(ClosedIdleShape.Width);
    private readonly Spring _height = new(ClosedIdleShape.Height);
    private readonly Spring _flare = new(ClosedIdleShape.Flare);
    private readonly Spring _radius = new(ClosedIdleShape.Radius);
    private readonly Stopwatch _frameClock = new();
    private TimeSpan _lastFrame;
    private bool _animating;

    private readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _watchdogTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private IntPtr _hwnd;
    private MediaSnapshot? _snapshot;
    private State _state = State.Closed;
    private bool _hovered;
    private bool _userHidden;
    private bool _fullscreenHidden;
    private DateTime _calendarDate;
    private Color _shownAccent = ColorExtractor.DefaultAccent;

    // Black hole: a window being dragged toward us.
    private double _attraction; // 0..1
    private DateTime? _zoneEnteredAt;
    private bool _inZone;
    private bool _capture; // armed: releasing now absorbs the window
    private (AbsorbedWindow Item, WindowApi.RECT Rect)? _armed;
    private bool _spinning;

    // Black hole: pulling a window back out of the shelf.
    private IReadOnlyList<AbsorbedWindow> _visibleItems = Array.Empty<AbsorbedWindow>();
    private AbsorbedWindow? _pressedItem;
    private WindowApi.POINT _pressPoint;
    private DragGhost? _ghost;

    public IslandWindow(MediaService media, WindowVault vault, WindowDragWatcher dragWatcher, DownloadWatcher downloads, Int32Rect monitorBounds)
    {
        InitializeComponent();
        _media = media;
        _vault = vault;
        _dragWatcher = dragWatcher;
        _downloads = downloads;
        _monitor = monitorBounds;

        // The menu-bar strip goes up first so the island always stacks above it.
        _strip = new MenuBarStrip(monitorBounds);
        _strip.FullscreenAppChanged += () =>
        {
            Watchdog();
            // The fullscreen window may still be settling into its final size; check again shortly.
            Dispatcher.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(400);
                Watchdog();
            });
        };
        _strip.PositionChanged += PositionOnMonitor;
        _strip.Show();
        _strip.Reserve();

        ClosedEq.BarBrush = _accentBrush;
        OpenEq.BarBrush = _accentBrush;

        _openTimer.Tick += (_, _) => { _openTimer.Stop(); SetState(State.Open); };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); SetState(State.Closed); };
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            if (_state == State.Peek) SetState(State.Closed);
        };
        _tickTimer.Tick += (_, _) => OnTick();
        _watchdogTimer.Tick += (_, _) => Watchdog();

        _media.Changed += OnMediaChanged;
        _vault.Changed += OnVaultChanged;
        _dragWatcher.Moved += OnWindowDragMoved;
        _dragWatcher.Ended += OnWindowDragEnded;
        MediaBrowserWindow.NowPlayingChanged += OnBrowserThumbnailArrived;
        _downloads.Changed += OnDownloadsChanged;
        Closed += (_, _) =>
        {
            _media.Changed -= OnMediaChanged;
            _vault.Changed -= OnVaultChanged;
            _dragWatcher.Moved -= OnWindowDragMoved;
            _dragWatcher.Ended -= OnWindowDragEnded;
            MediaBrowserWindow.NowPlayingChanged -= OnBrowserThumbnailArrived;
            _downloads.Changed -= OnDownloadsChanged;
            _tickTimer.Stop();
            _watchdogTimer.Stop();
            _ghost?.Close();
            _strip.Close();
            if (_animating) CompositionTarget.Rendering -= OnRendering;
        };
        SourceInitialized += OnSourceInitialized;
        MouseMove += OnWindowMouseMove;
        MouseLeftButtonUp += OnWindowMouseLeftButtonUp;
        LostMouseCapture += (_, _) => EndShelfDrag(cancelled: true);

        Notch.Height = Height;
        ShowSnapshot(media.Current);
        RebuildShelf();
        RebuildDownloads();
        UpdateCalendar();
        ApplyState(animate: false);
    }

    /// <summary>This island's monitor, in physical pixels.</summary>
    public Int32Rect Monitor => _monitor;

    public bool UserHidden
    {
        get => _userHidden;
        set
        {
            _userHidden = value;
            UpdateVisibility();
        }
    }

    // ---------------------------------------------------------------- window placement

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeOverlayWindow(_hwnd);
        PositionOnMonitor();
        _tickTimer.Start();
        _watchdogTimer.Start();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PositionOnMonitor();
    }

    private double DpiScale => _hwnd == IntPtr.Zero ? 1 : VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Placed in physical pixels so it lands correctly on monitors with different scaling.</summary>
    private void PositionOnMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(Width * dpi.DpiScaleX);
        int h = (int)Math.Round(Height * dpi.DpiScaleY);
        int x = _monitor.X + (_monitor.Width - w) / 2;
        NativeMethods.PlaceTopmost(_hwnd, x, _strip.ReservedTop, w, h);
    }

    private bool IsOnMyMonitor(int x, int y) =>
        x >= _monitor.X && x < _monitor.X + _monitor.Width && y >= _monitor.Y && y < _monitor.Y + _monitor.Height;

    /// <summary>A physical screen point relative to the notch's top-center, in DIPs.</summary>
    private (double Dx, double Dy) FromNotch(int x, int y)
    {
        double s = DpiScale;
        return ((x - (_monitor.X + _monitor.Width / 2.0)) / s, (y - _strip.ReservedTop) / s);
    }

    // ---------------------------------------------------------------- state

    private void SetState(State state)
    {
        if (_state == state) return;
        _state = state;
        ApplyState(animate: true);
    }

    private Silhouette CurrentSilhouette()
    {
        bool hasMedia = _snapshot != null;
        bool hasVault = _visibleItems.Count > 0;
        bool dlActive = _downloads.HasActive;
        switch (_state)
        {
            case State.Open:
                double h = OpenShape.Height;
                if (_downloads.Items.Count > 0) h += _downloadsHeight;
                if (hasVault) h += 130;
                return new Silhouette(640, h, 20, 28);
            case State.Attract:
                if (_capture) return CaptureShape;
                double a = _attraction;
                return new Silhouette(250 + 150 * a, 36 + 26 * a, 7 + 4 * a, 12 + 10 * a);
            case State.Peek when hasMedia:
                return PeekShape;
        }
        // Closed priority: download > media > black hole > idle.
        if (dlActive) return _hovered ? ClosedVaultHoverShape : ClosedVaultShape;
        if (hasMedia) return _hovered ? ClosedMediaHoverShape : ClosedMediaShape;
        if (hasVault) return _hovered ? ClosedVaultHoverShape : ClosedVaultShape;
        return _hovered ? ClosedIdleHoverShape : ClosedIdleShape;
    }

    private void ApplyState(bool animate)
    {
        bool hasMedia = _snapshot != null;
        bool closed = _state is State.Closed or State.Peek;
        var shape = CurrentSilhouette();

        _width.Target = shape.Width;
        _height.Target = shape.Height;
        _flare.Target = shape.Flare;
        _radius.Target = shape.Radius;
        if (animate)
        {
            StartShapeAnimation();
        }
        else
        {
            _width.Snap();
            _height.Snap();
            _flare.Snap();
            _radius.Snap();
            RenderShape();
        }

        bool dlActive = _downloads.HasActive;
        Reveal(ClosedDownload, closed && dlActive, animate);
        Reveal(ClosedMedia, closed && hasMedia && !dlActive, animate);
        Reveal(PeekText, _state == State.Peek && hasMedia && !dlActive, animate);
        Reveal(ClosedVault, closed && !hasMedia && !dlActive && _visibleItems.Count > 0, animate);
        Reveal(BlackHoleContent, _state == State.Attract, animate);
        Reveal(OpenContent, _state == State.Open, animate);
        DownloadsSection.Visibility = _state == State.Open && _downloads.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfSection.Visibility = _state == State.Open && _visibleItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetDownloadArrowAnimating(closed && dlActive);

        bool playing = _snapshot?.IsPlaying == true;
        ClosedEq.IsPlaying = playing && closed && !dlActive;
        OpenEq.IsPlaying = playing && _state == State.Open;
        SetHoleSpinning(_state == State.Attract);
        HoleText.Text = _capture ? "Release to absorb" : _inZone ? "Hold to absorb…" : "Drag here to absorb";

        if (_state == State.Open)
        {
            // ActualWidth isn't final until layout runs, so update after this pass.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateProgress();
                UpdateBattery();
                UpdateVolumeUi(SystemVolume.Get());
            }), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Content trails the shape slightly and fades in from a soft blur, the way macOS does it.</summary>
    private static void Reveal(FrameworkElement element, bool visible, bool animate)
    {
        if (animate && element.IsHitTestVisible == visible) return;
        element.IsHitTestVisible = visible;

        double opacity = visible ? 1 : 0;
        double scale = visible ? 1 : 0.94;
        var scaleTransform = element.RenderTransform as ScaleTransform;

        if (!animate)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = opacity;
            element.Effect = null;
            if (scaleTransform != null)
            {
                scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scaleTransform.ScaleX = scaleTransform.ScaleY = scale;
            }
            return;
        }

        var duration = TimeSpan.FromMilliseconds(visible ? 320 : 140);
        var begin = TimeSpan.FromMilliseconds(visible ? 80 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var opacityAnimation = new DoubleAnimation(opacity, duration) { BeginTime = begin, EasingFunction = ease };
        opacityAnimation.Completed += (_, _) => element.Effect = null;
        element.BeginAnimation(OpacityProperty, opacityAnimation);

        var blur = new BlurEffect { Radius = visible ? 10 : 0, RenderingBias = RenderingBias.Performance };
        element.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(visible ? 0 : 10, duration) { BeginTime = begin, EasingFunction = ease });

        if (scaleTransform != null)
        {
            var scaleAnimation = new DoubleAnimation(scale, duration) { BeginTime = begin, EasingFunction = ease };
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
        }
    }

    // ---------------------------------------------------------------- spring animation

    private void StartShapeAnimation()
    {
        if (_animating) return;
        _animating = true;
        _frameClock.Restart();
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _frameClock.Elapsed;
        double dt = Math.Min((now - _lastFrame).TotalSeconds, 1 / 30.0);
        _lastFrame = now;

        const double step = 1 / 240.0;
        while (dt > 0)
        {
            double h = Math.Min(step, dt);
            _width.Step(h);
            _height.Step(h);
            _flare.Step(h);
            _radius.Step(h);
            dt -= h;
        }

        bool settled = _width.TrySettle() & _height.TrySettle() & _flare.TrySettle() & _radius.TrySettle();
        RenderShape();

        if (settled)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    private void RenderShape()
    {
        double w = Math.Max(_width.Value, 40);
        double h = Math.Max(_height.Value, 16);
        double topR = Math.Clamp(_flare.Value, 0, Math.Min(w / 2, h / 2));
        double botR = Math.Clamp(_radius.Value, 0, Math.Min(w / 2, h - topR));
        double left = (ActualWidthOrDefault() - w) / 2;

        var geometry = BuildNotchGeometry(left, w, h, topR, botR);
        NotchShape.Data = geometry;
        NotchContent.Clip = geometry;

        if (_state == State.Attract)
        {
            // A purple glow around the event horizon while a window is being pulled in.
            _shadow.Color = HoleGlow;
            _shadow.ShadowDepth = 0;
            _shadow.BlurRadius = 40;
            _shadow.Opacity = _capture ? 0.95 : 0.35 + 0.5 * _attraction;
            NotchShape.Effect ??= _shadow;
            return;
        }

        // Otherwise a regular drop shadow that grows in as the notch opens.
        _shadow.Color = Colors.Black;
        _shadow.ShadowDepth = 6;
        _shadow.BlurRadius = 30;
        double openness = Math.Clamp((h - ClosedIdleShape.Height) / (OpenShape.Height - ClosedIdleShape.Height), 0, 1);
        if (openness < 0.02)
        {
            NotchShape.Effect = null;
        }
        else
        {
            _shadow.Opacity = 0.6 * openness;
            NotchShape.Effect ??= _shadow;
        }
    }

    private double ActualWidthOrDefault() => Notch.ActualWidth > 0 ? Notch.ActualWidth : Width;

    /// <summary>
    /// A clean rounded panel hanging from the top edge: flush at the top with gently rounded top
    /// corners and generously rounded bottom corners.
    /// </summary>
    private static Geometry BuildNotchGeometry(double left, double width, double height, double topR, double botR)
    {
        double x0 = left, x1 = left + width;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x0 + topR, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x1 - topR, 0), true, false);
            ctx.ArcTo(new Point(x1, topR), new Size(topR, topR), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(x1, height - botR), true, false);
            ctx.ArcTo(new Point(x1 - botR, height), new Size(botR, botR), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(x0 + botR, height), true, false);
            ctx.ArcTo(new Point(x0, height - botR), new Size(botR, botR), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(x0, topR), true, false);
            ctx.ArcTo(new Point(x0 + topR, 0), new Size(topR, topR), 0, false, SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- black hole: absorbing

    private void OnWindowDragMoved(WindowDrag drag)
    {
        if (!drag.IsMove || !IsVisible || !IsOnMyMonitor(drag.CursorX, drag.CursorY) || !WindowVault.CanAbsorb(drag.Hwnd))
        {
            ExitAttract();
            return;
        }

        bool inZone = IsInCaptureZone(drag.CursorX, drag.CursorY);
        var (dx, dy) = FromNotch(drag.CursorX, drag.CursorY);
        double attraction = inZone ? 1 : Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / AttractRadius, 0, 1);
        if (!inZone && attraction <= 0)
        {
            ExitAttract();
            return;
        }

        // Only arm after the window has been held over the notch for a moment.
        _zoneEnteredAt = inZone ? _zoneEnteredAt ?? DateTime.UtcNow : null;
        bool armed = inZone && DateTime.UtcNow - _zoneEnteredAt >= ArmDelay;
        if (armed && !_capture)
        {
            _height.Kick(160); // a little "got it" bump
            // Snapshot it now, while it still looks the way the user is holding it: on release
            // Windows may snap/maximize it (drag-to-top) before we get to hide it.
            var item = _vault.Prepare(drag.Hwnd, drag.StartPlacement);
            _armed = item == null ? null : (item, WindowApi.GetVisibleBounds(drag.Hwnd));
        }

        _openTimer.Stop();
        _closeTimer.Stop();
        _peekTimer.Stop();
        _inZone = inZone;
        _capture = armed;
        _attraction = attraction;
        _state = State.Attract;
        ApplyState(animate: true);
    }

    private void OnWindowDragEnded(WindowDrag drag)
    {
        var armed = _state == State.Attract && _capture ? _armed : null;
        ExitAttract();
        if (armed == null || !drag.IsMove || !IsInCaptureZone(drag.CursorX, drag.CursorY)) return;
        Swallow(armed.Value.Item, armed.Value.Rect);
    }

    private bool IsInCaptureZone(int x, int y)
    {
        if (!IsOnMyMonitor(x, y)) return false;
        var (dx, dy) = FromNotch(x, y);
        return Math.Abs(dx) < CaptureHalfWidth && dy < CaptureDepth;
    }

    private void ExitAttract()
    {
        _zoneEnteredAt = null;
        _armed = null;
        if (_state != State.Attract) return;
        _inZone = false;
        _capture = false;
        _attraction = 0;
        _state = State.Closed;
        ApplyState(animate: true);
    }

    /// <summary>Swallows a window into this island with the black hole animation.</summary>
    public void AbsorbWindow(IntPtr hwnd, WindowApi.WINDOWPLACEMENT? restorePlacement)
    {
        var item = _vault.Prepare(hwnd, restorePlacement);
        if (item == null)
        {
            Wobble();
            return;
        }
        Swallow(item, WindowApi.GetVisibleBounds(hwnd));
    }

    /// <summary>Hides the window right away and plays the swirl from where it was (physical rect).</summary>
    private void Swallow(AbsorbedWindow item, WindowApi.RECT rect)
    {
        // Hide immediately: waiting would let Windows' own drag-to-top maximize flash on screen.
        if (!_vault.Commit(item))
        {
            Wobble();
            return;
        }

        double s = DpiScale;
        var target = new Point(_monitor.X + _monitor.Width / 2.0, _strip.ReservedTop + 18 * s);
        var animation = new AbsorbAnimation(item.Snapshot, rect, target, s);
        animation.ContentRendered += (_, _) => animation.Play();
        animation.Finished += Gulp;
        animation.Show();
    }

    /// <summary>The notch bounces a little when something falls in.</summary>
    private void Gulp()
    {
        _height.Kick(260);
        _width.Kick(380);
        StartShapeAnimation();
    }

    /// <summary>A quick squish to say "can't do that" (e.g. admin windows).</summary>
    private void Wobble()
    {
        _width.Kick(-900);
        StartShapeAnimation();
    }

    private void SetHoleSpinning(bool spin)
    {
        if (_spinning == spin) return;
        _spinning = spin;
        HoleSpin.BeginAnimation(RotateTransform.AngleProperty, spin
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    // ---------------------------------------------------------------- downloads

    private double _downloadsHeight = 80;
    private bool _arrowAnimating;

    private void OnDownloadsChanged()
    {
        RebuildDownloads();
        ApplyState(animate: IsLoaded);
    }

    private void RebuildDownloads()
    {
        var items = _downloads.Items;
        int rows = Math.Min(items.Count, 4);
        _downloadsHeight = 28 + rows * 40 + 8;

        // Closed chip text: percent for a single known-size download, otherwise a count.
        var active = items.Where(i => !i.Complete).ToList();
        if (active.Count == 1 && active[0].Fraction is { } f)
            ClosedDownloadText.Text = $"{(int)(f * 100)}%";
        else if (active.Count > 0)
            ClosedDownloadText.Text = active.Count.ToString(CultureInfo.CurrentCulture);
        else
            ClosedDownloadText.Text = "";

        DownloadsCount.Text = items.Count == 1 ? "1 file" : $"{items.Count} files";

        DownloadsPanel.Children.Clear();
        foreach (var item in items) DownloadsPanel.Children.Add(CreateDownloadRow(item));
    }

    private FrameworkElement CreateDownloadRow(DownloadItem item)
    {
        var grid = new Grid { Height = 40, Margin = new Thickness(0, 4, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = item.Name,
            Foreground = Brushes.White,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        top.Children.Add(name);

        string status = item.Complete
            ? "Done · " + DownloadWatcher.FormatBytes(item.Total ?? item.Received)
            : item.Fraction is { } fr
                ? $"{(int)(fr * 100)}%  ·  {DownloadWatcher.FormatBytes(item.Received)} / {DownloadWatcher.FormatBytes(item.Total!.Value)}"
                : DownloadWatcher.FormatBytes(item.Received);
        var info = new TextBlock
        {
            Text = status,
            Foreground = (Brush)FindResource(item.Complete ? "SecondaryText" : "TertiaryText"),
            FontSize = 10,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (item.Complete) info.Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88));
        Grid.SetColumn(info, 1);
        top.Children.Add(info);
        Grid.SetRow(top, 0);
        grid.Children.Add(top);

        // Progress bar: determinate when the size is known, else an indeterminate sweep.
        var track = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(track, 1);
        track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(2) });

        var fill = new Border
        {
            Background = new SolidColorBrush(item.Complete ? Color.FromRgb(48, 209, 88) : Colors.White),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        track.Children.Add(fill);
        grid.Children.Add(track);

        if (item.Fraction is { } frac)
        {
            fill.Width = 0;
            track.Loaded += (_, _) => fill.Width = track.ActualWidth * frac;
            grid.SizeChanged += (_, _) => fill.Width = track.ActualWidth * frac;
        }
        else if (!item.Complete)
        {
            // Unknown size: an indeterminate sweeping bar.
            fill.Width = 60;
            track.Loaded += (_, _) => AnimateIndeterminate(fill, track);
        }
        else
        {
            track.Loaded += (_, _) => fill.Width = track.ActualWidth;
        }

        return grid;
    }

    private static void AnimateIndeterminate(Border fill, Grid track)
    {
        var transform = new TranslateTransform();
        fill.RenderTransform = transform;
        double w = track.ActualWidth;
        var animation = new DoubleAnimation(-60, w, TimeSpan.FromSeconds(1.1))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void SetDownloadArrowAnimating(bool on)
    {
        if (_arrowAnimating == on) return;
        _arrowAnimating = on;
        DownloadArrowT.BeginAnimation(TranslateTransform.YProperty, on
            ? new DoubleAnimation(-2, 3, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            }
            : null);
    }

    // ---------------------------------------------------------------- black hole: shelf

    private void OnVaultChanged()
    {
        RebuildShelf();
        ApplyState(animate: IsLoaded);
    }

    private void RebuildShelf()
    {
        _visibleItems = _vault.VisibleItems;
        int count = _visibleItems.Count;
        ClosedVaultCount.Text = count.ToString(CultureInfo.CurrentCulture);
        ShelfCount.Text = count == 1 ? "1 window" : $"{count} windows";

        ShelfPanel.Children.Clear();
        foreach (var item in _visibleItems) ShelfPanel.Children.Add(CreateCard(item));
    }

    private FrameworkElement CreateCard(AbsorbedWindow item)
    {
        var thumbnail = new Border
        {
            Width = 124,
            Height = 64,
            CornerRadius = new CornerRadius(9),
            BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Background = item.Snapshot != null
                ? new ImageBrush(item.Snapshot) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top }
                : (Brush)FindResource("ArtPlaceholder"),
        };

        var titleRow = new DockPanel { Margin = new Thickness(1, 6, 0, 0), LastChildFill = true };
        if (item.Icon != null)
        {
            var icon = new Image { Source = item.Icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 5, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            titleRow.Children.Add(icon);
        }
        titleRow.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 11,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var scale = new ScaleTransform(1, 1);
        var card = new StackPanel
        {
            Width = 124,
            Margin = new Thickness(0, 0, 12, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = item,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            Children = { thumbnail, titleRow },
        };
        card.MouseEnter += (_, _) => AnimateScale(scale, 1.05);
        card.MouseLeave += (_, _) => AnimateScale(scale, 1);
        card.MouseLeftButtonDown += Card_MouseLeftButtonDown;
        return card;
    }

    private static void AnimateScale(ScaleTransform scale, double to)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AbsorbedWindow item }) return;
        _pressedItem = item;
        WindowApi.GetCursorPos(out _pressPoint);
        // Capture on the window itself, so the drag survives the notch closing underneath it.
        CaptureMouse();
        e.Handled = true;
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem == null) return;
        WindowApi.GetCursorPos(out var cursor);

        if (_ghost == null)
        {
            double moved = Math.Sqrt(Math.Pow(cursor.X - _pressPoint.X, 2) + Math.Pow(cursor.Y - _pressPoint.Y, 2));
            if (moved < 8 * DpiScale) return;
            _ghost = new DragGhost(_pressedItem.Snapshot, _pressedItem.Icon, _pressedItem.Title);
            _ghost.Show();
        }
        _ghost.MoveTo(cursor.X, cursor.Y);

        // Once the window is dragged out of the island, the island closes behind it.
        if (_state == State.Open && !IsInsideOpenNotch(cursor.X, cursor.Y))
        {
            _openTimer.Stop();
            _closeTimer.Stop();
            SetState(State.Closed);
        }
    }

    private void OnWindowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedItem == null) return;
        EndShelfDrag(cancelled: false);
    }

    private void EndShelfDrag(bool cancelled)
    {
        var item = _pressedItem;
        if (item == null) return;
        _pressedItem = null;
        bool dragged = _ghost != null;
        _ghost?.Close();
        _ghost = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (cancelled) return;

        WindowApi.GetCursorPos(out var cursor);
        if (!dragged)
        {
            _vault.Restore(item); // plain click: back to where it was
        }
        else if (IsOnMyMonitor(cursor.X, cursor.Y) && IsInsideOpenNotch(cursor.X, cursor.Y) && _state == State.Open)
        {
            return; // dropped back onto the island: keep it inside
        }
        else
        {
            _vault.RestoreAt(item, cursor.X, cursor.Y);
        }
        _hovered = false;
        SetState(State.Closed);
    }

    private bool IsInsideOpenNotch(int x, int y)
    {
        var (dx, dy) = FromNotch(x, y);
        return Math.Abs(dx) < OpenShelfShape.Width / 2 && dy < _height.Value;
    }

    private void ShelfScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ShelfScroll.ScrollToHorizontalOffset(ShelfScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- media

    private void OnMediaChanged(MediaSnapshot? snapshot, bool isNewTrack)
    {
        ShowSnapshot(snapshot);
        if (_state == State.Peek && snapshot == null) _state = State.Closed;
        ApplyState(animate: IsLoaded);

        if (isNewTrack && _state == State.Closed && !_hovered)
        {
            SetState(State.Peek);
            _peekTimer.Stop();
            _peekTimer.Start();
        }
    }

    private void ShowSnapshot(MediaSnapshot? snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;

        if (snapshot == null)
        {
            TitleText.Text = "Nothing playing";
            ArtistText.Text = "Play something in Spotify or your browser";
            SourceText.Text = "";
            OpenEq.Visibility = Visibility.Collapsed;
            ProgressRow.Visibility = Visibility.Collapsed;
            Transport.IsEnabled = false;
            SetArtwork(null);
            return;
        }

        TitleText.Text = snapshot.Title;
        ArtistText.Text = snapshot.Artist;
        // Show the song, not the app: a real music app's name (Spotify) is fine, but "Google Chrome"
        // for a browser is noise, so only the artwork + title + artist represent it.
        bool showSource = !string.IsNullOrEmpty(snapshot.Source) && !snapshot.IsBrowser;
        SourceText.Text = showSource ? snapshot.Source : "";
        SourceText.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        PeekText.Text = string.IsNullOrEmpty(snapshot.Artist) ? snapshot.Title : $"{snapshot.Title}  ·  {snapshot.Artist}";
        OpenEq.Visibility = Visibility.Visible;
        ProgressRow.Visibility = snapshot.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        Transport.IsEnabled = true;
        PlayPauseGlyph.Data = (Geometry)FindResource(snapshot.IsPlaying ? "PauseGlyph" : "PlayGlyph");

        // Prefer Windows' artwork; if it gave none, use the real YouTube thumbnail from the panel.
        var art = snapshot.Artwork ?? MediaBrowserWindow.ThumbnailFor(snapshot.Title);
        SetArtwork(art);
        var accent = art != null ? ColorExtractor.Extract(art) : snapshot.Accent;
        if (previous == null || _shownAccent != accent)
        {
            _shownAccent = accent;
            _accentBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(accent, TimeSpan.FromMilliseconds(500)));
        }
    }

    private void OnBrowserThumbnailArrived()
    {
        var s = _snapshot;
        if (s?.Artwork != null) return; // already had real art
        var art = s == null ? null : MediaBrowserWindow.ThumbnailFor(s.Title);
        if (art == null) return;
        SetArtwork(art);
        var accent = ColorExtractor.Extract(art);
        _shownAccent = accent;
        _accentBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(accent, TimeSpan.FromMilliseconds(500)));
    }

    private void SetArtwork(BitmapSource? art)
    {
        if (art != null && ReferenceEquals(OpenArt.Tag, art)) return;
        OpenArt.Tag = art;

        if (art != null)
        {
            var brush = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            OpenArt.Background = brush;
            ClosedArt.Background = brush;
            OpenArtGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            var placeholder = (Brush)FindResource("ArtPlaceholder");
            OpenArt.Background = placeholder;
            ClosedArt.Background = placeholder;
            OpenArtGlyph.Visibility = Visibility.Visible;
        }
    }

    // ---------------------------------------------------------------- periodic updates

    private void OnTick()
    {
        if (DateTime.Today != _calendarDate) UpdateCalendar();
        if (_state != State.Open) return;
        UpdateProgress();
        UpdateBattery();
        if (!_draggingVolume) UpdateVolumeUi(SystemVolume.Get());
    }

    private void UpdateProgress()
    {
        if (_seeking) return; // don't fight the user's drag
        var s = _snapshot;
        if (s == null || s.Duration <= TimeSpan.Zero) return;

        var position = s.EstimatePosition();
        double fraction = Math.Clamp(position.TotalSeconds / s.Duration.TotalSeconds, 0, 1);
        double w = ProgressTrack.ActualWidth;
        ProgressFill.Width = w * fraction;
        ProgressThumb.Margin = new Thickness(w * fraction - 6, 0, 0, 0);
        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = "-" + FormatTime(s.Duration - position);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void UpdateBattery()
    {
        var battery = NativeMethods.GetBattery();
        if (battery == null)
        {
            BatteryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var (percent, charging) = battery.Value;
        BatteryPanel.Visibility = Visibility.Visible;
        BatteryText.Text = $"{percent}%";
        BatteryFill.Width = Math.Max(2, 17 * percent / 100.0);
        BatteryFill.Background = charging ? new SolidColorBrush(Color.FromRgb(48, 209, 88))
            : percent <= 20 ? new SolidColorBrush(Color.FromRgb(255, 69, 58))
            : Brushes.White;
    }

    /// <summary>A Mac-style week strip with today highlighted in the current accent color.</summary>
    private void UpdateCalendar()
    {
        var culture = CultureInfo.CurrentCulture;
        var today = DateTime.Today;
        _calendarDate = today;

        MonthText.Text = culture.TextInfo.ToTitleCase(today.ToString("MMMM", culture));
        WeekdayText.Text = culture.TextInfo.ToTitleCase(today.ToString("dddd d", culture));

        int offset = ((int)today.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var start = today.AddDays(-offset);

        WeekGrid.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            var day = start.AddDays(i);
            var letter = culture.DateTimeFormat.GetShortestDayName(day.DayOfWeek);
            WeekGrid.Children.Add(new TextBlock
            {
                Text = letter.Substring(0, 1).ToUpper(culture),
                FontSize = 10,
                Foreground = (Brush)FindResource("TertiaryText"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }
        for (int i = 0; i < 7; i++)
        {
            var day = start.AddDays(i);
            bool isToday = day == today;
            WeekGrid.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = isToday ? _accentBrush : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(culture),
                    FontSize = 11.5,
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isToday ? Brushes.Black : (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                        ? (Brush)FindResource("SecondaryText") : Brushes.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }

    // ---------------------------------------------------------------- visibility

    private void Watchdog()
    {
        bool fullscreen = NativeMethods.IsExclusiveFullscreenOrPresenting()
            || NativeMethods.IsFullscreenWindowOn(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height);
        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            UpdateVisibility();
        }

        // Other always-on-top windows can end up above us; quietly reclaim the top spot.
        if (IsVisible && _hwnd != IntPtr.Zero) NativeMethods.BringToTopmost(_hwnd);
    }

    private void UpdateVisibility()
    {
        bool show = !_userHidden && !_fullscreenHidden;
        if (show)
        {
            if (!_strip.IsVisible) _strip.Show();
            if (!IsVisible) Show();
        }
        else
        {
            if (IsVisible) Hide();
            if (_strip.IsVisible) _strip.Hide();
        }

        // Fullscreen apps ignore the reserved strip anyway; only give the space back when the user hides the island.
        if (_userHidden) _strip.Release();
        else _strip.Reserve();
    }

    /// <summary>Gives the reserved top strip back to other windows (on exit or crash).</summary>
    public void ReleaseReservedSpace() => _strip.Release();

    // ---------------------------------------------------------------- input

    private void Notch_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_pressedItem != null || _state == State.Attract) return;
        _closeTimer.Stop();
        _peekTimer.Stop();
        _hovered = true;
        if (_state != State.Open)
        {
            ApplyState(animate: true); // the small "you can open me" nudge
            _openTimer.Start();
        }
    }

    private void Notch_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_pressedItem != null || _state == State.Attract) return;
        _openTimer.Stop();
        _hovered = false;
        if (_state == State.Closed) ApplyState(animate: true);
        else _closeTimer.Start();
    }

    private void Notch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state is State.Open or State.Attract) return;
        _openTimer.Stop();
        SetState(State.Open);
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        MediaBrowserWindow.Toggle(_monitor);
        // Collapse the notch so it doesn't sit over the panel's search box.
        _openTimer.Stop();
        _closeTimer.Stop();
        _hovered = false;
        SetState(State.Closed);
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    // ---------------------------------------------------------------- seek

    private bool _seeking;

    private void Progress_Down(object sender, MouseButtonEventArgs e)
    {
        _seeking = true;
        ProgressTrack.CaptureMouse();
        PreviewSeek(e.GetPosition(ProgressTrack).X);
    }

    private void Progress_Move(object sender, MouseEventArgs e)
    {
        if (_seeking) PreviewSeek(e.GetPosition(ProgressTrack).X);
    }

    private async void Progress_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        ProgressTrack.ReleaseMouseCapture();
        double fraction = Math.Clamp(e.GetPosition(ProgressTrack).X / Math.Max(1, ProgressTrack.ActualWidth), 0, 1);
        await _media.SeekToFractionAsync(fraction);
    }

    /// <summary>Moves the fill/thumb while dragging, before committing the seek on release.</summary>
    private void PreviewSeek(double x)
    {
        double w = Math.Max(1, ProgressTrack.ActualWidth);
        double fraction = Math.Clamp(x / w, 0, 1);
        ProgressFill.Width = w * fraction;
        ProgressThumb.Margin = new Thickness(w * fraction - 6, 0, 0, 0);
        var s = _snapshot;
        if (s != null && s.Duration > TimeSpan.Zero)
        {
            var pos = TimeSpan.FromSeconds(s.Duration.TotalSeconds * fraction);
            ElapsedText.Text = FormatTime(pos);
            RemainingText.Text = "-" + FormatTime(s.Duration - pos);
        }
    }

    // ---------------------------------------------------------------- volume

    private bool _draggingVolume;

    private void Volume_Down(object sender, MouseButtonEventArgs e)
    {
        _draggingVolume = true;
        VolumeTrack.CaptureMouse();
        SetVolumeFromX(e.GetPosition(VolumeTrack).X);
    }

    private void Volume_Move(object sender, MouseEventArgs e)
    {
        if (_draggingVolume) SetVolumeFromX(e.GetPosition(VolumeTrack).X);
    }

    private void Volume_Up(object sender, MouseButtonEventArgs e)
    {
        _draggingVolume = false;
        VolumeTrack.ReleaseMouseCapture();
    }

    private void SetVolumeFromX(double x)
    {
        double w = Math.Max(1, VolumeTrack.ActualWidth);
        float level = (float)Math.Clamp(x / w, 0, 1);
        SystemVolume.Set(level);
        UpdateVolumeUi(level);
    }

    private void UpdateVolumeUi(float level)
    {
        double w = Math.Max(1, VolumeTrack.ActualWidth);
        VolumeFill.Width = w * level;
        VolumeThumb.Margin = new Thickness(w * level - 5, 0, 0, 0);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A damped spring (response ≈ 0.4 s, damping ≈ 0.8): quick, with a small organic overshoot.</summary>
    private sealed class Spring
    {
        private const double Stiffness = 240;
        private const double Damping = 25;

        public Spring(double value)
        {
            Value = Target = value;
        }

        public double Value { get; private set; }
        public double Target { get; set; }
        private double Velocity { get; set; }

        public void Step(double dt)
        {
            double force = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += force * dt;
            Value += Velocity * dt;
        }

        /// <summary>Adds a burst of velocity (bounce) without changing the target.</summary>
        public void Kick(double velocity) => Velocity += velocity;

        public bool TrySettle()
        {
            if (Math.Abs(Value - Target) > 0.1 || Math.Abs(Velocity) > 0.5) return false;
            Snap();
            return true;
        }

        public void Snap()
        {
            Value = Target;
            Velocity = 0;
        }
    }
}
