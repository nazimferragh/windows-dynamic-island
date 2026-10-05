using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Controls;
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
    private enum State { Closed, Peek, Open, Attract, Notify, DownloadStart }

    /// <summary>Body width/height, flare radius of the top corners, radius of the bottom corners.</summary>
    private readonly record struct Silhouette(double Width, double Height, double Flare, double Radius);

    private static readonly Silhouette ClosedIdleShape = new(190, 32, 6, 10);
    private static readonly Silhouette ClosedIdleHoverShape = new(204, 35, 6, 11);
    private static readonly Silhouette ClosedMediaShape = new(290, 32, 6, 10);
    private static readonly Silhouette ClosedMediaHoverShape = new(304, 35, 6, 11);
    private static readonly Silhouette ClosedVaultShape = new(250, 32, 6, 10);
    private static readonly Silhouette ClosedLiveShape = new(280, 32, 6, 10);
    private static readonly Silhouette ClosedLiveHoverShape = new(294, 35, 6, 11);
    private static readonly Silhouette ClosedVaultHoverShape = new(264, 35, 6, 11);
    private static readonly Silhouette PeekShape = new(290, 58, 8, 18);
    private static readonly Silhouette NotifyShape = new(470, 84, 10, 26);
    private static readonly Silhouette DownloadStartShape = new(210, 60, 10, 24);
    private static readonly Silhouette OpenShape = new(640, 206, 12, 32);
    private static readonly Silhouette OpenShelfShape = new(640, 336, 14, 32);

    // Where a dragged window is pulled in / captured, in DIPs relative to the notch's top-center.
    // As in the owner-approved Black Hole Lab preview: the island grows as the window comes near,
    // and letting go anywhere in the zone under it eats the window at once (no hold).
    private const double AttractRadius = 420;
    private const double CaptureHalfWidth = 230;
    private const double CaptureDepth = 130;

    private static readonly Color HoleGlow = Color.FromRgb(150, 90, 255);

    private readonly MediaService _media;
    private readonly WindowVault _vault;
    private readonly WindowDragWatcher _dragWatcher;
    private readonly DownloadWatcher _downloads;
    private readonly NotificationService _notifications;
    private readonly PinnedApps _pins;
    private readonly Int32Rect _monitor; // physical pixels
    private readonly TopEdge _edge;
    private readonly TopBandFill _bandFill;
    private readonly SolidColorBrush _accentBrush = new(ColorExtractor.DefaultAccent);
    /// <summary>Sliders, today's date, download bar: the Windows accent (Settings › Match Windows colors), else white.</summary>
    private readonly SolidColorBrush _uiBrush = new(Colors.White);
    private readonly DropShadowEffect _shadow = new() { Color = Colors.Black, BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0, RenderingBias = RenderingBias.Performance };

    private readonly Spring _width = new(ClosedIdleShape.Width);
    private readonly Spring _height = new(ClosedIdleShape.Height);
    private readonly Spring _flare = new(ClosedIdleShape.Flare);
    private readonly Spring _radius = new(ClosedIdleShape.Radius);
    private readonly Stopwatch _frameClock = new();
    private TimeSpan _lastFrame;
    private bool _animating;
    /// <summary>Windows being eaten or let out right now (the island holds its mouth open).</summary>
    private int _eating;
    /// <summary>A purple glow that flashes when a window falls in or comes out, then fades (1..0).</summary>
    private double _glowPulse;

    private readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _notifyTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _burstTimer = new() { Interval = TimeSpan.FromMilliseconds(1900) };
    private readonly HashSet<string> _seenDownloads = new();
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
    private bool _inZone;
    private bool _capture; // armed: releasing now absorbs the window
    // The window, its bounds and the cursor at the moment it was armed (to place it on release).
    private (AbsorbedWindow Item, WindowApi.RECT Rect, int CursorX, int CursorY)? _armed;

    // Black hole: pulling a window back out of the shelf.
    private IReadOnlyList<AbsorbedWindow> _visibleItems = Array.Empty<AbsorbedWindow>();
    private AbsorbedWindow? _pressedItem;
    private WindowApi.POINT _pressPoint;
    private DragGhost? _ghost;

    public IslandWindow(MediaService media, WindowVault vault, WindowDragWatcher dragWatcher, DownloadWatcher downloads,
        NotificationService notifications, PinnedApps pins, Int32Rect monitorBounds)
    {
        InitializeComponent();
        _pins = pins;
        _media = media;
        _vault = vault;
        _dragWatcher = dragWatcher;
        _downloads = downloads;
        _notifications = notifications;
        _monitor = monitorBounds;

        _edge = new TopEdge(monitorBounds);
        _edge.Changed += PositionOnMonitor;
        _edge.FullscreenAppChanged += () => Watchdog();
        // Shown first so the island always stacks above it.
        _bandFill = new TopBandFill(monitorBounds, () => (_edge.Top, _edge.Bottom));
        _bandFill.Raised += () => { if (_hwnd != IntPtr.Zero) WindowApi.RaiseTopmost(_hwnd); };
        _edge.Changed += _bandFill.Place;

        ClosedEq.BarBrush = _accentBrush;
        OpenEq.BarBrush = _accentBrush;
        ProgressFill.Background = _uiBrush;
        ProgressThumb.Fill = _uiBrush;
        VolumeFill.Background = _uiBrush;
        VolumeThumb.Fill = _uiBrush;

        _openTimer.Tick += (_, _) => { _openTimer.Stop(); SetState(State.Open); };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (_appMenuOpen || _appDrag != null) return; // a pinned app's menu is up, or apps are being rearranged
            SetState(State.Closed);
        };
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            if (_state == State.Peek) SetState(State.Closed);
        };
        _burstTimer.Tick += (_, _) =>
        {
            _burstTimer.Stop();
            if (_state == State.DownloadStart) SetState(State.Closed);
        };
        _notifyTimer.Tick += (_, _) =>
        {
            _notifyTimer.Stop();
            if (_state == State.Notify) SetState(State.Closed);
        };
        _tickTimer.Tick += (_, _) => OnTick();
        _watchdogTimer.Tick += (_, _) => Watchdog();
        GameMode.Tune(_watchdogTimer, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        GameMode.Changed += OnGameModeChanged;
        Closed += (_, _) => GameMode.Changed -= OnGameModeChanged;

        _media.Changed += OnMediaChanged;
        _vault.Changed += OnVaultChanged;
        _dragWatcher.Moved += OnWindowDragMoved;
        _dragWatcher.Ended += OnWindowDragEnded;
        MediaBrowserWindow.NowPlayingChanged += OnBrowserThumbnailArrived;
        _downloads.Changed += OnDownloadsChanged;
        _notifications.Received += OnNotificationReceived;
        _pins.Changed += RebuildApps;
        WindowsTheme.Current.Changed += ApplyAppearance;
        AppSettings.Changed += ApplyAppearance;
        Closed += (_, _) =>
        {
            WindowsTheme.Current.Changed -= ApplyAppearance;
            AppSettings.Changed -= ApplyAppearance;
            _pins.Changed -= RebuildApps;
            _media.Changed -= OnMediaChanged;
            _vault.Changed -= OnVaultChanged;
            _dragWatcher.Moved -= OnWindowDragMoved;
            _dragWatcher.Ended -= OnWindowDragEnded;
            MediaBrowserWindow.NowPlayingChanged -= OnBrowserThumbnailArrived;
            _downloads.Changed -= OnDownloadsChanged;
            _notifications.Received -= OnNotificationReceived;
            _tickTimer.Stop();
            _watchdogTimer.Stop();
            _ghost?.Close();
            ReleaseCursor();
            _bandFill.Close();
            _edge.Dispose();
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
        RebuildApps();
        RebuildStatus();
        SystemStatus.Current.Changed += RebuildStatus;
        Closed += (_, _) => SystemStatus.Current.Changed -= RebuildStatus;
        BuildLiveBars();
        MicActivity.Current.Changed += OnMicActivityChanged;
        Closed += (_, _) =>
        {
            MicActivity.Current.Changed -= OnMicActivityChanged;
            _liveTimer.Stop();
        };
        _liveTimer.Tick += (_, _) => UpdateLive();
        OnMicActivityChanged();
        _dwellTimer.Tick += (_, _) => FinishDwell();
        AddDwell(GearButton, OpenSettingsFromIsland, () => false);
        AddDwell(AppsButton, ShowAppsView, () => _appsView);
        ApplyAppearance();
        ApplyState(animate: false);
    }

    /// <summary>Applies Windows colors and the user's settings. Runs at start and whenever either changes.</summary>
    private void ApplyAppearance()
    {
        var s = AppSettings.Current;
        bool match = s.MatchWindowsColors;
        var theme = WindowsTheme.Current;
        var target = match ? theme.AccentOnDark : Colors.White;
        _uiBrush.BeginAnimation(SolidColorBrush.ColorProperty, IsLoaded ? new ColorAnimation(target, TimeSpan.FromMilliseconds(300)) : null);
        if (!IsLoaded) _uiBrush.Color = target;
        Brush bars = match ? _uiBrush : _accentBrush;
        ClosedEq.BarBrush = bars;
        OpenEq.BarBrush = bars;

        _openTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.HoverDelayMs, 50, 2000));
        AppsButton.Visibility = s.PinnedAppsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!s.PinnedAppsEnabled) _appsView = false;
        RebuildStatus();
        if (!s.BlackHoleEnabled && !s.SnapLayoutsEnabled) ExitAttract();

        UpdateCalendar();
        RebuildDownloads();
        if (IsLoaded) ApplyState(animate: true);
    }

    /// <summary>Today's date in the calendar: the Windows accent, or the song's color.</summary>
    private Brush TodayBrush => AppSettings.Current.MatchWindowsColors ? _uiBrush : _accentBrush;

    private bool DownloadsShown => AppSettings.Current.DownloadsEnabled;
    private bool DownloadActive => DownloadsShown && _downloads.HasActive;
    private bool ClosedMediaShown => AppSettings.Current.ShowClosedMedia;

    private void GearButton_Click(object sender, RoutedEventArgs e) => OpenSettingsFromIsland();

    private void OpenSettingsFromIsland()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _hovered = false;
        SetState(State.Closed);
        (Application.Current as App)?.OpenSettings();
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
        ReserveTop();
        if (!_userHidden) _bandFill.Start();
        _tickTimer.Start();
        _watchdogTimer.Start();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Windows resizes the window for the new scale *after* this returns; centering now would use
        // the old size and leave the island off-center on monitors with a different scale (e.g. 125%).
        Dispatcher.BeginInvoke(() => { ReserveTop(); PositionOnMonitor(); }, DispatcherPriority.Loaded);
    }

    /// <summary>The closed island's height, kept free of other windows like the Mac menu bar.</summary>
    private const double ReservedBand = 32;

    private void ReserveTop()
    {
        if (_userHidden) return;
        _edge.Reserve((int)Math.Round(ReservedBand * DpiScale));
    }

    /// <summary>Gives the reserved top band back to other windows (on exit or crash).</summary>
    public void ReleaseReservedSpace() => _edge.Release();

    private double DpiScale => _hwnd == IntPtr.Zero ? 1 : VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Placed in physical pixels so it lands correctly on monitors with different scaling.</summary>
    private void PositionOnMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        var r = ExpectedBounds();
        NativeMethods.PlaceTopmost(_hwnd, r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>Top middle of this monitor, physical pixels.</summary>
    private WindowApi.RECT ExpectedBounds()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(Width * dpi.DpiScaleX);
        int h = (int)Math.Round(Height * dpi.DpiScaleY);
        int x = _monitor.X + (_monitor.Width - w) / 2;
        return new WindowApi.RECT { Left = x, Top = _edge.Top, Right = x + w, Bottom = _edge.Top + h };
    }

    /// <summary>Puts the island back in the top middle if anything (a DPI change, Explorer, another app) moved it.</summary>
    private void KeepCentered()
    {
        if (_hwnd == IntPtr.Zero || !WindowApi.GetWindowRect(_hwnd, out var actual)) return;
        var expected = ExpectedBounds();
        if (Math.Abs(actual.Left - expected.Left) > 1 || Math.Abs(actual.Top - expected.Top) > 1 ||
            Math.Abs(actual.Width - expected.Width) > 1 || Math.Abs(actual.Height - expected.Height) > 1)
        {
            Log.Info($"Island was off its spot ({actual.Left},{actual.Top} {actual.Width}x{actual.Height}); re-centering");
            PositionOnMonitor();
        }
    }

    private bool IsOnMyMonitor(int x, int y) =>
        x >= _monitor.X && x < _monitor.X + _monitor.Width && y >= _monitor.Y && y < _monitor.Y + _monitor.Height;

    /// <summary>A physical screen point relative to the notch's top-center, in DIPs.</summary>
    private (double Dx, double Dy) FromNotch(int x, int y)
    {
        double s = DpiScale;
        return ((x - (_monitor.X + _monitor.Width / 2.0)) / s, (y - _edge.Top) / s);
    }

    // ---------------------------------------------------------------- state

    private void SetState(State state)
    {
        if (_state == state) return;
        _state = state;
        if (state != State.Open) ClosePanels(); // next time it opens on the player
        ApplyState(animate: true);
    }

    private Silhouette CurrentSilhouette()
    {
        bool hasMedia = _snapshot != null;
        bool hasVault = _visibleItems.Count > 0;
        bool dlActive = DownloadActive;
        switch (_state)
        {
            case State.Open:
                double h = OpenShape.Height;
                if (ListPanelShown) return new Silhouette(640, h + ListPanelExtra, 12, 28);
                if (LiveShown) h += 58;
                if (DownloadsShown && _downloads.Items.Count > 0) h += _downloadsHeight;
                if (hasVault) h += 130;
                return new Silhouette(640, h, 12, 28);
            case State.Attract:
                // The island opens up as the window comes near, to the same size it eats at.
                var near = ClosedSilhouette(hasMedia, hasVault, dlActive);
                double a = _attraction;
                return new Silhouette(near.Width * (1 + 0.45 * a), near.Height * (1 + 0.55 * a), near.Flare + 2 * a, near.Radius * (1 + 0.5 * a));
            case State.Peek when hasMedia:
                return PeekShape;
            case State.Notify:
                return NotifyShape;
            case State.DownloadStart:
                return DownloadStartShape;
        }
        var closed = ClosedSilhouette(hasMedia, hasVault, dlActive);
        // Eating (or letting out) a window: the island opens up around it (as in the Black Hole Lab
        // preview: 1.45× wider, 1.55× taller).
        if (_eating > 0) return new Silhouette(closed.Width * 1.45, closed.Height * 1.55, closed.Flare + 2, closed.Radius * 1.5);
        return closed;
    }

    private Silhouette ClosedSilhouette(bool hasMedia, bool hasVault, bool dlActive)
    {
        // Closed priority: call/recording > download > media > black hole > idle.
        if (LiveShown) return _hovered ? ClosedLiveHoverShape : ClosedLiveShape;
        if (dlActive) return _hovered ? ClosedVaultHoverShape : ClosedVaultShape;
        if (hasMedia && ClosedMediaShown) return _hovered ? ClosedMediaHoverShape : ClosedMediaShape;
        if (hasVault) return _hovered ? ClosedVaultHoverShape : ClosedVaultShape;
        return _hovered ? ClosedIdleHoverShape : ClosedIdleShape;
    }

    /// <summary>The island's outline right now, physical pixels (the target windows morph into).</summary>
    private Rect IslandScreenRect()
    {
        double s = DpiScale;
        double w = Math.Max(_width.Value, 40) * s, h = Math.Max(_height.Value, 16) * s;
        return new Rect(_monitor.X + _monitor.Width / 2.0 - w / 2, _edge.Top, w, h);
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

        Reveal(NotificationContent, _state == State.Notify, animate);
        Reveal(DownloadBurstContent, _state == State.DownloadStart, animate);
        SetBurstAnimating(_state == State.DownloadStart);
        bool live = LiveShown;
        bool dlActive = DownloadActive && !live;
        bool closedMedia = hasMedia && ClosedMediaShown && !live;
        Reveal(ClosedLive, closed && live, animate);
        Reveal(ClosedDownload, closed && dlActive, animate);
        Reveal(ClosedMedia, closed && closedMedia && !dlActive, animate);
        Reveal(PeekText, _state == State.Peek && hasMedia && !dlActive && !live, animate);
        Reveal(ClosedVault, closed && !closedMedia && !dlActive && !live && _visibleItems.Count > 0, animate);
        LiveSection.Visibility = _state == State.Open && live && !ListPanelShown ? Visibility.Visible : Visibility.Collapsed;
        // In game mode nothing animates on its own: every redraw of an island costs the game frames.
        bool calm = GameMode.Active;
        SetLiveAnimating(live && !calm && (closed || _state == State.Open));
        bool hintOn = _state == State.Attract && _capture;
        if (hintOn != _dropHintShown)
        {
            _dropHintShown = hintOn;
            DropHint.BeginAnimation(OpacityProperty, new DoubleAnimation(hintOn ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        }
        Reveal(OpenContent, _state == State.Open, animate);
        string view = _appsView ? "apps" : _wifiView ? "wifi" : _btView ? "bluetooth" : "player";
        if (_state == State.Open && animate && view != _shownView && _shownView != null)
            SlideIn(view == "apps" ? AppsGrid : view == "player" ? PlayerGrid : WifiGrid);
        _shownView = _state == State.Open ? view : null;
        PlayerGrid.Visibility = _appsView || ListPanelShown ? Visibility.Collapsed : Visibility.Visible;
        AppsGrid.Visibility = _appsView ? Visibility.Visible : Visibility.Collapsed;
        WifiGrid.Visibility = ListPanelShown ? Visibility.Visible : Visibility.Collapsed;
        PlayerRow.Height = new GridLength(ListPanelShown ? PlayerRowHeight + ListPanelExtra : PlayerRowHeight);
        AppsGlyphPath.Fill = _appsView ? Brushes.White : (Brush)FindResource("SecondaryText");
        DownloadsSection.Visibility = _state == State.Open && !ListPanelShown && DownloadsShown && _downloads.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfSection.Visibility = _state == State.Open && !ListPanelShown && _visibleItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetDownloadArrowAnimating(closed && dlActive && !calm);

        bool playing = _snapshot?.IsPlaying == true;
        ClosedEq.IsPlaying = playing && closed && !dlActive && !calm;
        OpenEq.IsPlaying = playing && _state == State.Open && !calm;

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

    /// <summary>The view showing in the open island (player, apps, wifi, bluetooth); null while closed.</summary>
    private string? _shownView;

    /// <summary>Switching views: the new one rises a few pixels and fades in while the island resizes around it.</summary>
    private static void SlideIn(FrameworkElement view)
    {
        var move = view.RenderTransform as TranslateTransform;
        if (move == null) view.RenderTransform = move = new TranslateTransform();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
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

        // The open view is big: blurring it every frame costs smoothness, so it only fades and scales.
        bool big = element.ActualWidth * element.ActualHeight > 150_000;
        var duration = TimeSpan.FromMilliseconds(visible ? (big ? 260 : 320) : 140);
        var begin = TimeSpan.FromMilliseconds(visible ? (big ? 30 : 80) : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var opacityAnimation = new DoubleAnimation(opacity, duration) { BeginTime = begin, EasingFunction = ease };
        opacityAnimation.Completed += (_, _) => element.Effect = null;
        element.BeginAnimation(OpacityProperty, opacityAnimation);

        if (big)
        {
            element.Effect = null;
        }
        else
        {
            var blur = new BlurEffect { Radius = visible ? 10 : 0, RenderingBias = RenderingBias.Performance };
            element.Effect = blur;
            blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(visible ? 0 : 10, duration) { BeginTime = begin, EasingFunction = ease });
        }

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

        _glowPulse = _glowPulse < 0.01 ? 0 : _glowPulse * Math.Exp(-4.5 * dt);
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

        bool settled = _width.TrySettle() & _height.TrySettle() & _flare.TrySettle() & _radius.TrySettle() & _glowPulse == 0;
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
        double flare = Math.Clamp(_flare.Value, 0, h / 3);
        double botR = Math.Clamp(_radius.Value, 0, Math.Min(w / 2, h - flare));
        double left = (ActualWidthOrDefault() - w) / 2; // the body; the flares reach just outside it

        var geometry = BuildNotchGeometry(left, w, h, flare, botR);
        DropHint.Margin = new Thickness(0, h + 10, 0, 0);
        NotchShape.Data = geometry;
        NotchContent.Clip = geometry;

        if (_glowPulse > 0)
        {
            _shadow.Color = HoleGlow;
            _shadow.ShadowDepth = 0;
            _shadow.BlurRadius = 40;
            _shadow.Opacity = 0.9 * _glowPulse;
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
    /// The MacBook notch outline: flush with the top of the screen, the top corners flare outward into
    /// the screen edge with a small concave quarter-circle (so the island grows out of the edge
    /// instead of hanging from it), and the bottom corners are rounded.
    /// </summary>
    private static Geometry BuildNotchGeometry(double left, double width, double height, double flare, double botR)
    {
        double x0 = left, x1 = left + width;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x0 - flare, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x1 + flare, 0), true, false);
            // Right flare: from the screen edge, curving down into the island's side.
            ctx.ArcTo(new Point(x1, flare), new Size(flare, flare), 0, false, SweepDirection.Counterclockwise, true, true);
            ctx.LineTo(new Point(x1, height - botR), true, false);
            ctx.ArcTo(new Point(x1 - botR, height), new Size(botR, botR), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(x0 + botR, height), true, false);
            ctx.ArcTo(new Point(x0, height - botR), new Size(botR, botR), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(x0, flare), true, false);
            // Left flare: back up and out into the screen edge.
            ctx.ArcTo(new Point(x0 - flare, 0), new Size(flare, flare), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- black hole: absorbing

    private void OnWindowDragMoved(WindowDrag drag)
    {
        var settings = AppSettings.Current;
        bool holeOn = settings.BlackHoleEnabled, snapOn = settings.SnapLayoutsEnabled;
        if ((!holeOn && !snapOn) || !drag.IsMove || !IsVisible || !IsOnMyMonitor(drag.CursorX, drag.CursorY) || !WindowVault.CanAbsorb(drag.Hwnd))
        {
            if (IsOnMyMonitor(drag.CursorX, drag.CursorY)) CursorFence.LowerWalls();
            ClearEdge();
            ExitAttract();
            return;
        }

        // A side shared with another screen gets a soft wall, so its half/quarters work like any
        // other edge; pushing on through it moves the window to the other screen.
        if (snapOn) RaiseSideWalls(drag.CursorY);
        else CursorFence.LowerWalls();

        // The top middle is always the black hole's; edges and corners arrange the window.
        bool inZone = holeOn && IsInCaptureZone(drag.CursorX, drag.CursorY);
        SetEdgeTarget(snapOn && !inZone ? EdgeCellAt(drag.CursorX, drag.CursorY) : null);
        if (!holeOn)
        {
            ExitAttract();
            return;
        }

        var (dx, dy) = FromNotch(drag.CursorX, drag.CursorY);
        double attraction = inZone ? 1 : Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / AttractRadius, 0, 1);
        if (!inZone && attraction <= 0)
        {
            ExitAttract();
            return;
        }

        // Without our edge snapping, Windows' own drag-to-top maximize would start up here (the whole
        // screen goes grey and blurry), so the cursor is kept just below the top while the island pulls.
        if (!EdgeSnapping.Active) HoldCursorBelowStrip();

        // In the zone = armed right away: letting go eats it.
        bool armed = inZone;
        if (armed && !_capture)
        {
            _height.Kick(120); // a little "got it" bump
            // Snapshot it now, while it still looks the way the user is holding it. Its picture is
            // taken on a worker thread, so the drag never hitches.
            var item = _vault.Prepare(drag.Hwnd, drag.StartPlacement, captureNow: false);
            _armedSnapshot = item == null ? null : WindowVault.CaptureSnapshotAsync(drag.Hwnd);
            _armed = item == null ? null : (item, WindowApi.GetVisibleBounds(drag.Hwnd), drag.CursorX, drag.CursorY);
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

    private void RaiseSideWalls(int cursorY)
    {
        int left = _monitor.X, right = _monitor.X + _monitor.Width - 1;
        CursorFence.RaiseWalls(
            WindowApi.HasMonitorAt(left - 1, cursorY) ? left : null,
            WindowApi.HasMonitorAt(right + 1, cursorY) ? right : null,
            _monitor.Y, _monitor.Y + _monitor.Height);
    }

    private void OnWindowDragEnded(WindowDrag drag)
    {
        CursorFence.LowerWalls();
        var armed = _state == State.Attract && _capture ? _armed : null;
        var edge = _edgeCell;
        ClearEdge();
        ExitAttract();
        if (edge != null && drag.IsMove && IsOnMyMonitor(drag.CursorX, drag.CursorY))
        {
            SnapWindow(drag.Hwnd, edge);
            return;
        }
        if (armed == null || !drag.IsMove || !IsInCaptureZone(drag.CursorX, drag.CursorY)) return;
        // The window kept following the cursor after it was armed; start the animation exactly where
        // it was let go, not where it was a moment ago.
        var (item, rect, armX, armY) = armed.Value;
        int dx = drag.CursorX - armX, dy = drag.CursorY - armY;
        rect.Left += dx;
        rect.Right += dx;
        rect.Top += dy;
        rect.Bottom += dy;
        // The picture was started when it entered the zone; it's almost always ready by now.
        if (_armedSnapshot is { } shot && item.Snapshot == null)
        {
            try { if (shot.Wait(250)) item.Snapshot = shot.Result; }
            catch (Exception ex) { Log.Error("Window snapshot failed", ex); }
        }
        Swallow(item, rect);
    }

    private bool IsInCaptureZone(int x, int y)
    {
        if (!IsOnMyMonitor(x, y)) return false;
        var (dx, dy) = FromNotch(x, y);
        return Math.Abs(dx) < CaptureHalfWidth && dy < CaptureDepth;
    }

    private void ExitAttract()
    {
        _armed = null;
        if (_state != State.Attract) return;
        ReleaseCursor();
        _inZone = false;
        _capture = false;
        _attraction = 0;
        _state = State.Closed;
        ApplyState(animate: true);
    }

    // ---------------------------------------------------------------- snap layouts: edges and corners

    // Owner-approved in the Black Hole Lab preview (option C): no panel. Drag a window to the left or
    // right edge for a half, into a corner for a quarter, to the top beside the island for full
    // screen. The top middle stays the black hole's. Windows' own docking is paused meanwhile
    // (EdgeSnapping), so only our outline shows.

    private static readonly SnapCell LeftHalf = new(0, 0, .5, 1, "Left half");
    private static readonly SnapCell RightHalf = new(.5, 0, .5, 1, "Right half");
    private static readonly SnapCell FullScreen = new(0, 0, 1, 1, "Full screen");
    private static readonly SnapCell[] Quarters =
    {
        new(0, 0, .5, .5, "Top-left quarter"), new(.5, 0, .5, .5, "Top-right quarter"),
        new(0, .5, .5, .5, "Bottom-left quarter"), new(.5, .5, .5, .5, "Bottom-right quarter"),
    };

    private SnapCell? _edgeCell;
    private SnapPreview? _preview;

    /// <summary>Which zone the cursor's spot on this screen asks for, if any (same thresholds as the preview).</summary>
    private SnapCell? EdgeCellAt(int x, int y)
    {
        double fx = (x - _monitor.X) / (double)_monitor.Width, fy = (y - _monitor.Y) / (double)_monitor.Height;
        bool left = fx < 0.025, right = fx > 0.975, top = fy < 0.04, bottom = fy > 0.86;
        bool nearLeft = fx < 0.06, nearRight = fx > 0.94, nearTop = fy < 0.09;
        if ((nearLeft || nearRight) && (nearTop || bottom) && (left || right || top || bottom))
            return Quarters[(nearTop ? 0 : 2) + (nearRight ? 1 : 0)];
        if (left) return LeftHalf;
        if (right) return RightHalf;
        if (top) return FullScreen;
        return null;
    }

    /// <summary>The other half, which the most recent window fills after a drop (halves only).</summary>
    private static SnapCell? PartnerOf(SnapCell cell) =>
        ReferenceEquals(cell, LeftHalf) ? RightHalf : ReferenceEquals(cell, RightHalf) ? LeftHalf : null;

    private WindowApi.RECT MyWorkArea() => WindowSnapper.WorkAreaAt(_monitor.X + _monitor.Width / 2, _monitor.Y + _monitor.Height / 2);

    /// <summary>Moves the outline on screen to the zone (and the other half, dashed), or hides it.</summary>
    private void SetEdgeTarget(SnapCell? cell)
    {
        if (ReferenceEquals(cell, _edgeCell)) return;
        _edgeCell = cell;
        if (cell == null)
        {
            _preview?.HideZone();
            return;
        }
        var work = MyWorkArea();
        _preview ??= new SnapPreview(work);
        var partner = AppSettings.Current.SnapAutoFill ? PartnerOf(cell) : null;
        _preview.ShowZone(WindowSnapper.ZoneRect(work, cell), partner == null ? Array.Empty<WindowApi.RECT>() : new[] { WindowSnapper.ZoneRect(work, partner) });
        WindowApi.RaiseTopmost(_hwnd); // the island stays above the outline
    }

    private void ClearEdge()
    {
        SetEdgeTarget(null);
        ClosePreviewSoon();
    }

    private void ClosePreviewSoon()
    {
        var preview = _preview;
        _preview = null;
        if (preview == null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            preview.Close();
        };
        timer.Start();
    }

    /// <summary>Glides the dropped window into its zone; for a half, the most recent other window takes the other half.</summary>
    private void SnapWindow(IntPtr hwnd, SnapCell cell)
    {
        var work = MyWorkArea();
        if (!WindowSnapper.Glide(hwnd, work, cell))
        {
            Wobble();
            return;
        }
        var partner = AppSettings.Current.SnapAutoFill ? PartnerOf(cell) : null;
        if (partner == null) return;
        var windows = RecentWindowsOnMyMonitor(hwnd, 1);
        if (windows.Count > 0) WindowSnapper.Glide(windows[0], work, partner);
        WindowApi.RaiseTopmost(_hwnd);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>The most recently used normal windows on this monitor (z-order), excluding one.</summary>
    private List<IntPtr> RecentWindowsOnMyMonitor(IntPtr except, int count)
    {
        var found = new List<IntPtr>();
        foreach (var w in WindowApi.TopLevelWindowsInZOrder())
        {
            if (found.Count >= count) break;
            if (w == except || !WindowVault.CanAbsorb(w) || WindowApi.IsMinimized(w) || WindowApi.IsCloaked(w)) continue;
            if (string.IsNullOrWhiteSpace(WindowApi.GetTitle(w))) continue;
            var r = WindowApi.GetVisibleBounds(w);
            if (!IsOnMyMonitor(r.Left + r.Width / 2, r.Top + r.Height / 2)) continue;
            found.Add(w);
        }
        return found;
    }

    private bool _cursorHeld;

    private void HoldCursorBelowStrip()
    {
        int top = _edge.Top + (int)Math.Round(32 * DpiScale);
        // Windows 11 starts its maximize preview a little before the actual edge, hence the margin.
        CursorFence.Raise(_monitor.X, _monitor.X + _monitor.Width, top + (int)Math.Round(28 * DpiScale));
        _cursorHeld = true;
    }

    /// <summary>Frees the cursor again. Also safe to call when it isn't held.</summary>
    public void ReleaseCursor()
    {
        if (!_cursorHeld) return;
        _cursorHeld = false;
        CursorFence.Lower();
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
        double s = DpiScale;
        // Opens the mouth first, so the window morphs towards the opened island.
        _eating++;
        ApplyState(animate: true);
        var animation = new AbsorbAnimation(item.Snapshot, rect, IslandScreenRect, s);
        animation.ContentRendered += (_, _) => animation.Play();
        animation.Finished += () =>
        {
            // Swallowed: the mouth snaps shut with a gulp.
            _eating = Math.Max(0, _eating - 1);
            ApplyState(animate: true);
            Gulp();
        };
        // The snapshot goes up first, then the real window is hidden beneath it in the same beat, so
        // there's no blank frame in between (and no time for Windows' drag-to-top maximize to show).
        animation.Show();
        // The island stays in front, so the window falls *into* it rather than over it.
        WindowApi.RaiseTopmost(_hwnd);
        if (!_vault.Commit(item))
        {
            animation.Close();
            _eating = Math.Max(0, _eating - 1);
            ApplyState(animate: true);
            Wobble();
        }
    }

    /// <summary>The notch bounces a little when something falls in.</summary>
    private void Gulp()
    {
        // As in the preview: a quick wide gulp, no glow (it's all black).
        _height.Kick(180);
        _width.Kick(600);
        StartShapeAnimation();
    }

    /// <summary>A quick squish to say "can't do that" (e.g. admin windows).</summary>
    private void Wobble()
    {
        _width.Kick(-900);
        StartShapeAnimation();
    }

    private bool _dropHintShown;
    /// <summary>The armed window's picture, being taken in the background.</summary>
    private System.Threading.Tasks.Task<System.Windows.Media.Imaging.BitmapSource?>? _armedSnapshot;

    // ---------------------------------------------------------------- downloads

    private double _downloadsHeight = 80;
    private bool _arrowAnimating;

    private void OnDownloadsChanged()
    {
        RebuildDownloads();
        bool started = false;
        foreach (var item in _downloads.Items)
            if (_seenDownloads.Add(item.Id) && !item.Complete) started = true;
        if (started && DownloadsShown && AppSettings.Current.AnimateDownloadStart) ShowDownloadStarted();
        ApplyState(animate: IsLoaded);
    }

    // ---------------------------------------------------------------- notifications

    private NotificationInfo? _shownNotification;

    private void OnNotificationReceived(NotificationInfo info)
    {
        // Don't interrupt while the user is actively using the island or absorbing a window.
        if (!AppSettings.Current.NotificationsInIsland || !IsVisible || _state is State.Open or State.Attract) return;

        _shownNotification = info;
        NotifApp.Text = string.IsNullOrWhiteSpace(info.AppName) ? "Notification" : info.AppName;
        NotifTitle.Text = info.Title;
        NotifBody.Text = info.Body;
        NotifBody.Visibility = string.IsNullOrWhiteSpace(info.Body) ? Visibility.Collapsed : Visibility.Visible;
        if (info.Icon != null)
        {
            NotifIcon.Source = info.Icon;
            NotifIcon.Visibility = Visibility.Visible;
            NotifBell.Visibility = Visibility.Collapsed;
        }
        else
        {
            NotifIcon.Source = null;
            NotifIcon.Visibility = Visibility.Collapsed;
            NotifBell.Visibility = Visibility.Visible;
        }

        _openTimer.Stop();
        _closeTimer.Stop();
        _peekTimer.Stop();
        SetState(State.Notify);
        // A fresh notification restarts the display timer.
        _notifyTimer.Stop();
        _notifyTimer.Interval = TimeSpan.FromSeconds(_hovered ? 60 : Math.Clamp(AppSettings.Current.NotificationSeconds, 2, 30));
        _notifyTimer.Start();
    }

    /// <summary>A new download: the island pops out and shows the drop animation for a moment.</summary>
    private void ShowDownloadStarted()
    {
        // Leave it alone while it's in use, pulling a window in, or showing a notification.
        if (!IsVisible || _state is State.Open or State.Attract or State.Notify) return;
        _openTimer.Stop();
        _closeTimer.Stop();
        _peekTimer.Stop();
        SetState(State.DownloadStart);
        _width.Kick(420);
        _height.Kick(220);
        StartShapeAnimation();
        _burstTimer.Stop();
        _burstTimer.Start();
    }

    private bool _bursting;

    /// <summary>Arrow drops into the tray (twice) while a ring ripples out, in step.</summary>
    private void SetBurstAnimating(bool on)
    {
        if (_bursting == on) return;
        _bursting = on;
        if (!on)
        {
            BurstArrowT.BeginAnimation(TranslateTransform.YProperty, null);
            BurstArrow.BeginAnimation(OpacityProperty, null);
            BurstRing.BeginAnimation(OpacityProperty, null);
            BurstRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BurstRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }

        var cycle = TimeSpan.FromMilliseconds(800);
        var drop = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = cycle };
        drop.KeyFrames.Add(new DiscreteDoubleKeyFrame(-16, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        drop.KeyFrames.Add(new EasingDoubleKeyFrame(2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)),
            new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
        drop.KeyFrames.Add(new DiscreteDoubleKeyFrame(2, KeyTime.FromTimeSpan(cycle)));

        var fadeIn = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = cycle };
        fadeIn.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fadeIn.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        fadeIn.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620))));
        fadeIn.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(cycle)));

        // The ripple goes out as the arrow lands.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = cycle };
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromTimeSpan(cycle), ease));
        var ripple = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = cycle };
        ripple.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        ripple.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        ripple.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(cycle), ease));

        BurstArrowT.BeginAnimation(TranslateTransform.YProperty, drop);
        BurstArrow.BeginAnimation(OpacityProperty, fadeIn);
        BurstRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        BurstRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        BurstRing.BeginAnimation(OpacityProperty, ripple);
    }

    private void ClearDownloads_Click(object sender, RoutedEventArgs e) => _downloads.ClearFinished();

    private void RebuildDownloads()
    {
        var items = _downloads.Items;
        ClearDownloadsButton.Visibility = _downloads.HasFinished ? Visibility.Visible : Visibility.Collapsed;
        int rows = Math.Min(items.Count, 4);
        _downloadsHeight = 28 + rows * 40 + 8;

        // Closed chip: a progress line + percent when the sizes are known; otherwise the line slides
        // back and forth and the text shows how much came in so far (or the count, for several).
        var active = items.Where(i => !i.Complete).ToList();
        double? fraction = active.Count > 0 && active.All(i => i.Total is > 0)
            ? Math.Clamp((double)active.Sum(i => i.Received) / active.Sum(i => i.Total!.Value), 0, 1)
            : null;
        if (fraction is { } f)
            ClosedDownloadText.Text = $"{(int)(f * 100)}%";
        else if (active.Count == 1)
            ClosedDownloadText.Text = DownloadWatcher.FormatBytes(active[0].Received);
        else if (active.Count > 0)
            ClosedDownloadText.Text = active.Count.ToString(CultureInfo.CurrentCulture);
        else
            ClosedDownloadText.Text = "";
        UpdateClosedDownloadBar(active.Count > 0 ? fraction : 0);

        DownloadsCount.Text = items.Count == 1 ? "1 file" : $"{items.Count} files";

        DownloadsPanel.Children.Clear();
        foreach (var item in items) DownloadsPanel.Children.Add(CreateDownloadRow(item));
    }

    private bool _downloadBarSliding;

    /// <param name="fraction">0..1, or null when the total isn't known (the line slides instead).</param>
    private void UpdateClosedDownloadBar(double? fraction)
    {
        double track = ClosedDownload.Width - ClosedDownloadTrack.Margin.Left - ClosedDownloadTrack.Margin.Right;
        bool calm = GameMode.Active;
        if (fraction is { } f)
        {
            if (_downloadBarSliding)
            {
                _downloadBarSliding = false;
                ClosedDownloadFillT.BeginAnimation(TranslateTransform.XProperty, null);
                ClosedDownloadFillT.X = 0;
            }
            double width = Math.Round(track * f, 1);
            if (calm || !IsLoaded) { ClosedDownloadFill.BeginAnimation(WidthProperty, null); ClosedDownloadFill.Width = width; }
            else ClosedDownloadFill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(450))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            return;
        }

        if (_downloadBarSliding && !calm) return;
        double seg = Math.Round(track * 0.3);
        ClosedDownloadFill.BeginAnimation(WidthProperty, null);
        ClosedDownloadFill.Width = seg;
        if (calm)
        {
            // Games: no per-frame work, just a still segment.
            _downloadBarSliding = false;
            ClosedDownloadFillT.BeginAnimation(TranslateTransform.XProperty, null);
            ClosedDownloadFillT.X = (track - seg) / 2;
            return;
        }
        _downloadBarSliding = true;
        ClosedDownloadFillT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, track - seg, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private FrameworkElement CreateDownloadRow(DownloadItem item)
    {
        var grid = new Grid { Height = 40, Margin = new Thickness(0, 4, 0, 0) };
        // Click the row: open the file (or its folder while it's still downloading).
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 0, 4, 0),
            Margin = new Thickness(-8, 0, -4, 0),
            Cursor = Cursors.Hand,
            Child = grid,
            ToolTip = item.Complete ? "Open" : "Show in folder",
        };
        var hover = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (item.Complete) OpenDownload(item);
            else RevealDownload(item);
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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

        // Folder button: show the file in Explorer, selected.
        var folder = new Button
        {
            Style = (Style)FindResource("TransportButton"),
            Width = 24,
            Height = 24,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Show in folder",
            Content = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource("FolderGlyph"),
                Stroke = (Brush)FindResource("SecondaryText"),
                StrokeThickness = 1.3,
                Width = 13,
                Height = 11,
                Stretch = Stretch.Uniform,
            },
        };
        folder.Click += (_, _) => RevealDownload(item);
        Grid.SetColumn(folder, 2);
        top.Children.Add(folder);
        Grid.SetRow(top, 0);
        grid.Children.Add(top);

        // Progress bar: determinate when the size is known, else an indeterminate sweep.
        var track = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(track, 1);
        track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(2) });

        var fill = new Border
        {
            Background = item.Complete ? new SolidColorBrush(Color.FromRgb(48, 209, 88)) : _uiBrush,
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

        return row;
    }

    /// <summary>
    /// Opens a finished download the way double-clicking it in Explorer would. Going through the
    /// shell keeps Windows' own safety prompts for programs downloaded from the internet.
    /// </summary>
    private void OpenDownload(DownloadItem item)
    {
        if (!File.Exists(item.FilePath))
        {
            RevealDownload(item);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true })?.Dispose();
            CloseAfterAction();
        }
        catch (Exception ex)
        {
            // No app for this file type (or the user cancelled the prompt): show it instead.
            Log.Error($"Couldn't open {item.FilePath}", ex);
            RevealDownload(item);
        }
    }

    /// <summary>Opens the download's folder in Explorer with the file selected.</summary>
    private void RevealDownload(DownloadItem item)
    {
        try
        {
            string path = item.FilePath;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            else
            {
                var dir = Directory.Exists(Path.GetDirectoryName(path) ?? "") ? Path.GetDirectoryName(path)!
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false })?.Dispose();
            }
            CloseAfterAction();
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't show {item.FilePath} in its folder", ex);
        }
    }

    private void CloseAfterAction()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _hovered = false;
        SetState(State.Closed);
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
        var ghost = _ghost;
        _ghost = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (cancelled)
        {
            ghost?.Close();
            return;
        }

        WindowApi.GetCursorPos(out var cursor);
        if (ghost == null)
        {
            EmergeWindow(item, null, null); // plain click: back to where it was, out of the notch
        }
        else if (IsOnMyMonitor(cursor.X, cursor.Y) && IsInsideOpenNotch(cursor.X, cursor.Y) && _state == State.Open)
        {
            ghost.Close();
            return; // dropped back onto the island: keep it inside
        }
        else
        {
            // The card the user is holding grows into the window, title bar under the cursor.
            EmergeWindow(item, (cursor.X, cursor.Y), ghost.CardRect(cursor.X, cursor.Y));
            ghost.Close();
        }
        _hovered = false;
        SetState(State.Closed);
    }

    /// <summary>
    /// Brings a window back out of the black hole with the emerge animation: from the notch (or the
    /// dragged card) to its spot, then the real window is shown under the landing snapshot.
    /// </summary>
    private void EmergeWindow(AbsorbedWindow item, (int X, int Y)? at, WindowApi.RECT? fromCard)
    {
        // A click brings it back on this island's screen (the one the user is looking at), wherever
        // it was eaten; dragged out, it lands under the cursor.
        var myWork = WindowSnapper.WorkAreaAt(_monitor.X + _monitor.Width / 2, _monitor.Y + _monitor.Height / 2);
        var dest = _vault.BeginRestore(item, at, at == null ? myWork : null);
        if (dest == null) return;

        bool shown = false, mouthOpen = false;
        void Show()
        {
            if (shown) return;
            shown = true;
            _vault.FinishRestore(item, at != null);
            if (!mouthOpen) return;
            _eating = Math.Max(0, _eating - 1);
            ApplyState(animate: true);
            _width.Kick(-270); // it settles back with a small squeeze, as in the preview
            StartShapeAnimation();
        }

        // Safety net: whatever happens to the animation, the window comes back.
        var net = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        net.Tick += (_, _) =>
        {
            net.Stop();
            Show();
        };
        net.Start();

        try
        {
            double s = DpiScale;
            bool fromNotch = fromCard == null;
            var from = fromCard ?? NotchMouth(s);
            if (fromNotch)
            {
                mouthOpen = true;
                _eating++;
                ApplyState(animate: true);
            }
            var animation = new EmergeAnimation(item.Snapshot, from, dest.Value, fromNotch, IslandScreenRect, s);
            animation.ContentRendered += (_, _) => animation.Play();
            animation.Finished += Show;
            animation.Closed += (_, _) => Show();
            animation.Show();
            // It comes out of the island, which stays in front.
            if (fromNotch) WindowApi.RaiseTopmost(_hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("Emerge animation failed", ex);
            Show();
        }
    }

    /// <summary>The point windows fall into and come out of, as a tiny rect (physical pixels).</summary>
    private WindowApi.RECT NotchMouth(double s)
    {
        int x = _monitor.X + _monitor.Width / 2, y = _edge.Top + (int)(18 * s);
        return new WindowApi.RECT { Left = x, Top = y, Right = x + 1, Bottom = y + 1 };
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

        if (isNewTrack && _state == State.Closed && !_hovered && AppSettings.Current.ShowSongPreview)
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
        PlaceKnob(ProgressTrack, ProgressFill, ProgressThumb, fraction);
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
                Background = isToday ? TodayBrush : Brushes.Transparent,
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
        // The island stays over full-screen games and videos (like on an iPhone), unless the user
        // chose to hide it there; then only the island on that app's own screen steps aside.
        bool fullscreen = AppSettings.Current.HideInFullscreenApps && GameMode.IsOn(_monitor);
        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            UpdateVisibility();
        }

        // Other always-on-top windows can end up above us; quietly reclaim the top spot. While a
        // game runs, only when the game really got above us: re-ordering topmost windows every
        // second can hitch its frames, a cheap z-order check can't.
        if (IsVisible && _hwnd != IntPtr.Zero && (!GameMode.Active || NativeMethods.IsCoveredByForeground(_hwnd)))
            NativeMethods.BringToTopmost(_hwnd);
        if (IsVisible) _edge.Refresh();
        if (IsVisible) KeepCentered();
    }

    private void OnGameModeChanged()
    {
        Watchdog();
        RebuildDownloads();
        ApplyState(animate: false);
    }

    private void UpdateVisibility()
    {
        bool show = !_userHidden && !_fullscreenHidden;
        if (show)
        {
            if (!IsVisible) Show();
        }
        else
        {
            if (IsVisible) Hide();
        }

        // Full-screen apps ignore the reserved band anyway; only give it back when the user hides the island.
        if (_userHidden)
        {
            _bandFill.Stop();
            _edge.Release();
        }
        else
        {
            ReserveTop();
            _bandFill.Start();
        }
    }

    // ---------------------------------------------------------------- input

    private void Notch_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_pressedItem != null || _state == State.Attract) return;
        _closeTimer.Stop();
        _peekTimer.Stop();
        _hovered = true;
        if (_state == State.Notify)
        {
            // Reading it: keep it up (no auto-open into the player), so it can be clicked.
            _notifyTimer.Stop();
            NotificationContent.Opacity = 1;
            NotifHover.Opacity = 1;
            return;
        }
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
        NotifHover.Opacity = 0;
        if (_state == State.Notify)
        {
            // Give it a couple more seconds after the pointer leaves.
            _notifyTimer.Interval = TimeSpan.FromSeconds(2.5);
            _notifyTimer.Start();
            return;
        }
        if (_state == State.Closed) ApplyState(animate: true);
        else _closeTimer.Start();
    }

    private void Notch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state is State.Open or State.Attract) return;
        _openTimer.Stop();
        if (_state == State.Notify && _shownNotification is { } notification)
        {
            // Open what the notification is about, like clicking it in Windows.
            _notifyTimer.Stop();
            _shownNotification = null;
            NotifHover.Opacity = 0;
            _hovered = false;
            SetState(State.Closed);
            NotificationActivator.Activate(notification);
            return;
        }
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

    // ---------------------------------------------------------------- pinned apps

    private bool _appsView;
    private bool _appMenuOpen;
    private readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource?> _appIcons = new(StringComparer.OrdinalIgnoreCase);

    private void AppsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appsView && JustHoverOpened) return; // hovering already opened it
        bool show = !_appsView;
        ClosePanels();
        _appsView = show;
        ApplyState(animate: true);
    }

    private void ShowAppsView()
    {
        if (_appsView) return;
        ClosePanels();
        _appsView = true;
        ApplyState(animate: true);
    }

    // ---------------------------------------------------------------- hover to open

    /// <summary>
    /// Hovering an icon in the open island opens it, no click needed. The tiny delay only filters
    /// out the pointer passing over on its way somewhere else; it reads as instant.
    /// </summary>
    private static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(90);
    /// <summary>Settings opens a window and closes the island, so it waits a little longer.</summary>
    private static readonly TimeSpan HoverOpenSettingsDelay = TimeSpan.FromMilliseconds(450);
    private readonly DispatcherTimer _dwellTimer = new();
    private UIElement? _dwellHost;
    private Action? _dwellAction;
    /// <summary>When a view was last opened by hovering (a click right after must not close it again).</summary>
    private DateTime _hoverOpenedAt;

    private bool JustHoverOpened => DateTime.UtcNow - _hoverOpenedAt < TimeSpan.FromMilliseconds(900);

    private void AddDwell(Button button, Action open, Func<bool> isOpen) =>
        AttachDwell(button, open, isOpen, button == GearButton ? HoverOpenSettingsDelay : HoverOpenDelay);

    private void AttachDwell(UIElement host, Action open, Func<bool> isOpen, TimeSpan delay)
    {
        host.MouseEnter += (_, _) =>
        {
            CancelDwell();
            if (_state != State.Open || _appDrag != null || isOpen()) return;
            _dwellHost = host;
            _dwellAction = open;
            _dwellTimer.Interval = delay;
            _dwellTimer.Start();
        };
        host.MouseLeave += (_, _) => { if (ReferenceEquals(_dwellHost, host)) CancelDwell(); };
        host.PreviewMouseLeftButtonDown += (_, _) => CancelDwell();
    }

    private void CancelDwell()
    {
        _dwellTimer.Stop();
        _dwellHost = null;
        _dwellAction = null;
    }

    private void FinishDwell()
    {
        var open = _dwellAction;
        CancelDwell();
        if (open == null || _state != State.Open || _appDrag != null) return;
        _hoverOpenedAt = DateTime.UtcNow;
        open();
    }

    private const double PlayerRowHeight = 168;
    /// <summary>The Wi‑Fi/Bluetooth lists get more room than the player.</summary>
    private const double ListPanelExtra = 84;

    // ---------------------------------------------------------------- calls and recordings (live activity)

    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly List<System.Windows.Shapes.Rectangle> _liveBars = new(), _liveOpenBars = new();
    private double _liveLevel;
    private static readonly Brush CallGreen = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush RecordRed = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));

    private bool LiveShown => AppSettings.Current.ShowMicActivity && MicActivity.Current.Active != null;

    private void BuildLiveBars()
    {
        foreach (var (host, list, height) in new[] { (LiveBars, _liveBars, 12.0), (LiveOpenBars, _liveOpenBars, 18.0) })
            for (int i = 0; i < 5; i++)
            {
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = 2.6,
                    Height = height,
                    RadiusX = 1.3,
                    RadiusY = 1.3,
                    Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1, 0.2),
                };
                host.Children.Add(bar);
                list.Add(bar);
            }
    }

    private void OnMicActivityChanged()
    {
        var use = MicActivity.Current.Active;
        if (use != null)
        {
            var color = use.IsCall ? CallGreen : RecordRed;
            LiveDot.Fill = color;
            LiveOpenDot.Fill = color;
            foreach (var bar in _liveBars.Concat(_liveOpenBars)) bar.Fill = color;
            LiveName.Text = use.AppName;
            LiveOpenTitle.Text = use.IsCall ? $"{use.AppName} call" : $"{use.AppName} is using the microphone";
        }
        UpdateLive();
        if (IsLoaded) ApplyState(animate: true);
    }

    private void SetLiveAnimating(bool on)
    {
        if (on == _liveTimer.IsEnabled) return;
        if (on) _liveTimer.Start();
        else _liveTimer.Stop();
    }

    /// <summary>Timer text, mute state and the live mic level bars (60 ms, only while shown).</summary>
    private void UpdateLive()
    {
        var use = MicActivity.Current.Active;
        if (use == null) return;
        var elapsed = DateTime.UtcNow - use.Since;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        string time = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
        LiveTimer.Text = time;
        bool muted = Interop.Microphone.IsMuted();
        LiveOpenSub.Text = (use.IsCall ? "On a call · " : "Recording · ") + time + (muted ? " · microphone muted" : "");
        LiveMuteText.Text = muted ? "Unmute mic" : "Mute mic";
        LiveMuteButton.Background = muted ? RecordRed : Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        // Smooth the level so the bars move like a voice, not like noise.
        double level = muted ? 0 : Math.Min(1, Interop.Microphone.Level() * 2.2);
        _liveLevel = level > _liveLevel ? level : _liveLevel * 0.82 + level * 0.18;
        double t = Environment.TickCount64 / 1000.0;
        for (int i = 0; i < _liveBars.Count; i++)
        {
            double wobble = 0.55 + 0.45 * Math.Sin(t * 9 + i * 1.7);
            double scale = Math.Clamp(0.18 + _liveLevel * wobble, 0.18, 1);
            ((ScaleTransform)_liveBars[i].RenderTransform).ScaleY = scale;
            ((ScaleTransform)_liveOpenBars[i].RenderTransform).ScaleY = scale;
        }
    }

    private void LiveMute_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Interop.Microphone.SetMuted(!Interop.Microphone.IsMuted());
        UpdateLive();
    }

    private void LiveOpenApp_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var use = MicActivity.Current.Active;
        if (use == null) return;
        bool shown = use.PackageFamily != null ? WindowFinder.ActivateWindowOfPackage(use.PackageFamily)
            : use.ExePath != null && WindowFinder.ActivateWindowOf(use.ExePath);
        if (!shown && use.PackageFamily != null) OpenUri("shell:AppsFolder\\" + use.PackageFamily + "!App");
        CloseAfterAction();
    }

    // ---------------------------------------------------------------- status icons (top row)

    private void RebuildStatus()
    {
        var s = AppSettings.Current;
        var st = SystemStatus.Current;
        var fg = (Brush)FindResource("SecondaryText");
        StatusPanel.Children.Clear();

        if (s.StatusBluetooth && st.HasBluetooth)
            AddStatus(StatusIcons.Bluetooth(Brushes.White, st.BluetoothOn), st.BluetoothOn ? "Bluetooth: on" : "Bluetooth: off",
                ToggleBluetoothView, () => { if (!_btView) ToggleBluetoothView(); }, () => _btView);

        if (s.StatusWifi)
        {
            FrameworkElement icon = st.Network switch
            {
                NetworkKind.Wifi => StatusIcons.Wifi(Brushes.White, st.WifiLevel, offline: false),
                NetworkKind.Wired or NetworkKind.Cellular => StatusIcons.Ethernet(Brushes.White),
                _ => StatusIcons.Wifi(Brushes.White, 0, offline: true),
            };
            string tip = st.Network switch
            {
                NetworkKind.Wifi => $"Wi‑Fi: {st.NetworkName}",
                NetworkKind.Wired => $"Wired network: {st.NetworkName}",
                NetworkKind.Cellular => $"Mobile network: {st.NetworkName}",
                _ => "Not connected",
            };
            AddStatus(icon, tip, ToggleWifiView, () => { if (!_wifiView) ToggleWifiView(); }, () => _wifiView);
        }

        if (s.StatusBattery && st.HasBattery)
        {
            var battery = new StackPanel { Orientation = Orientation.Horizontal };
            if (s.StatusBatteryPercent)
                battery.Children.Add(new TextBlock
                {
                    Text = $"{st.BatteryPercent}%",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0),
                });
            battery.Children.Add(StatusIcons.Battery(Brushes.White, st.BatteryPercent, st.Charging, st.PluggedIn));
            string tip = st.Charging ? $"Charging · {st.BatteryPercent}%"
                : st.PluggedIn ? $"Plugged in · {st.BatteryPercent}%"
                : st.TimeLeft is { } left ? $"On battery · about {(int)left.TotalHours} h {left.Minutes} min left"
                : $"On battery · {st.BatteryPercent}%";
            AddStatus(battery, tip, () => OpenUri("ms-settings:batterysaver"));
        }
        _ = fg;
    }

    private void AddStatus(FrameworkElement content, string tip, Action click, Action? dwellOpen = null, Func<bool>? dwellIsOpen = null)
    {
        content.VerticalAlignment = VerticalAlignment.Center;
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
        var hovered = new Trigger { Property = IsMouseOverProperty, Value = true };
        hovered.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF))));
        style.Triggers.Add(hovered);
        var item = new Border
        {
            Height = 26,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(7),
            Style = style,
            Cursor = Cursors.Hand,
            ToolTip = tip,
        };
        item.Child = content;
        if (dwellOpen != null) AttachDwell(item, dwellOpen, dwellIsOpen ?? (() => false), HoverOpenDelay);
        item.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (dwellOpen != null && JustHoverOpened) return; // hovering already opened it; don't toggle it shut
            click();
        };
        StatusPanel.Children.Add(item);
    }

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error($"Couldn't open {uri}", ex); }
    }

    // ---------------------------------------------------------------- Wi‑Fi and Bluetooth panels

    // One panel area in the open island shows either Wi‑Fi or Bluetooth: a title with an on/off
    // switch, a list whose rows show their actions on hover, and a status line.
    private bool _wifiView;
    private bool _btView;
    private int _panelVersion;
    private List<BtDevice> _btPaired = new();
    private bool ListPanelShown => _wifiView || _btView;

    /// <summary>Opens the island on a panel (from <c>--panel</c>): wifi, bluetooth, apps, player, close, or restore (newest black-hole window).</summary>
    public void ShowPanel(string panel)
    {
        if (panel == "close")
        {
            SetState(State.Closed);
            return;
        }
        if (panel.StartsWith("absorb:", StringComparison.Ordinal) && long.TryParse(panel.AsSpan(7), out long handle))
        {
            // Test helper: swallow that window as if it had been dropped on the black hole.
            AbsorbWindow(new IntPtr(handle), null);
            return;
        }
        if (panel == "restore")
        {
            // The newest window in the black hole comes back out (same as clicking it on the shelf).
            if (_visibleItems.Count > 0) EmergeWindow(_visibleItems[0], null, null);
            return;
        }
        ClosePanels();
        SetState(State.Open);
        switch (panel)
        {
            case "wifi": ToggleWifiView(); break;
            case "bluetooth": ToggleBluetoothView(); break;
            case "apps": _appsView = true; ApplyState(animate: true); break;
            case "search": SetState(State.Closed); MediaBrowserWindow.Toggle(_monitor); break;
            default: ApplyState(animate: true); break;
        }
    }

    private void ToggleWifiView()
    {
        bool show = !_wifiView;
        ClosePanels();
        _wifiView = show;
        ApplyState(animate: true);
        if (_wifiView) _ = RefreshWifiAsync();
    }

    private void ToggleBluetoothView()
    {
        bool show = !_btView;
        ClosePanels();
        _btView = show;
        ApplyState(animate: true);
        if (_btView) _ = RefreshBluetoothAsync(full: true);
    }

    /// <summary>Leaves the Wi‑Fi/Bluetooth/apps views (and stops looking for Bluetooth devices).</summary>
    private void ClosePanels()
    {
        if (_btView)
        {
            BluetoothService.NearbyChanged -= OnNearbyChanged;
            BluetoothService.StopDiscovery();
        }
        _wifiView = false;
        _btView = false;
        _appsView = false;
    }

    private void RenderPanelSwitch(bool on)
    {
        WifiSwitch.Background = on ? _uiBrush : Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        WifiSwitchKnob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        WifiSwitchKnob.Fill = on && AppSettings.Current.MatchWindowsColors ? Brushes.Black : Brushes.White;
    }

    private async void WifiSwitch_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_wifiView)
        {
            await WifiService.SetRadioAsync(!WifiService.RadioOn);
            await System.Threading.Tasks.Task.Delay(700);
            await RefreshWifiAsync();
        }
        else if (_btView)
        {
            await BluetoothService.SetRadioAsync(!BluetoothService.RadioOn);
            await System.Threading.Tasks.Task.Delay(700);
            await RefreshBluetoothAsync(full: true);
        }
    }

    private void WifiSettings_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        string uri = _btView ? "ms-settings:bluetooth" : WifiService.NamesHidden ? "ms-settings:privacy-location" : "ms-settings:network-wifi";
        CloseAfterAction();
        OpenUri(uri);
    }

    private void WifiScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        WifiScroll.ScrollToVerticalOffset(WifiScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    // ---- Wi‑Fi

    private async System.Threading.Tasks.Task RefreshWifiAsync()
    {
        int version = ++_panelVersion;
        PanelTitle.Text = "Wi‑Fi";
        if (WifiList.Tag as string != "wifi") WifiList.Children.Clear(); // don't show the other panel's list while scanning
        WifiList.Tag = "wifi";
        WifiStatus.Text = "Looking for networks…";
        if (!await WifiService.InitAsync())
        {
            WifiList.Children.Clear();
            RenderPanelSwitch(false);
            WifiStatus.Text = "This PC has no Wi‑Fi, or Windows didn't allow access to it.";
            return;
        }
        RenderPanelSwitch(WifiService.RadioOn);
        if (!WifiService.RadioOn)
        {
            WifiList.Children.Clear();
            WifiStatus.Text = "Wi‑Fi is off.";
            return;
        }
        var networks = await WifiService.ScanAsync();
        if (version != _panelVersion || !_wifiView) return;
        WifiList.Children.Clear();
        foreach (var n in networks) WifiList.Children.Add(CreateWifiRow(n));
        WifiStatus.Text = WifiService.NamesHidden
            ? "Windows hides network names until location access is on (More settings)."
            : networks.Count == 0 ? "No networks found." : $"{networks.Count} networks nearby";
    }

    private FrameworkElement CreateWifiRow(WifiNetwork n)
    {
        int level = n.Bars >= 4 ? 3 : n.Bars >= 2 ? 2 : 1;
        var actions = new List<(string, Func<System.Threading.Tasks.Task>)>();
        if (n.Connected) actions.Add(("Disconnect", () => WifiDisconnectAsync(n)));
        else actions.Add(("Join", () => WifiJoinAsync(n)));
        if (n.Saved) actions.Add(("Forget", () => WifiForgetAsync(n)));
        return CreateListRow(StatusIcons.Wifi(Brushes.White, level, offline: false), n.Ssid, n.Connected,
            n.Connected ? "Connected" : n.Saved ? "Saved" : "", n.Connected ? _uiBrush : (Brush)FindResource("TertiaryText"),
            n.Secured, actions);
    }

    private async System.Threading.Tasks.Task WifiJoinAsync(WifiNetwork n)
    {
        WifiStatus.Text = $"Joining {n.Ssid}…";
        var result = await WifiService.JoinAsync(n);
        switch (result)
        {
            case WifiJoinResult.Joined:
                WifiStatus.Text = $"Connected to {n.Ssid}";
                await RefreshWifiAsync();
                break;
            case WifiJoinResult.NeedsPassword:
            case WifiJoinResult.WrongPassword:
                // New network, or its password changed: ask for it.
                CloseAfterAction();
                WifiPasswordWindow.ShowFor(n, _monitor);
                break;
            default:
                WifiStatus.Text = $"Couldn't join {n.Ssid}. Move closer to the router and try again.";
                break;
        }
    }

    private async System.Threading.Tasks.Task WifiDisconnectAsync(WifiNetwork n)
    {
        WifiService.Disconnect();
        WifiStatus.Text = $"Disconnected from {n.Ssid}";
        await System.Threading.Tasks.Task.Delay(900);
        await RefreshWifiAsync();
    }

    private async System.Threading.Tasks.Task WifiForgetAsync(WifiNetwork n)
    {
        bool ok = WifiService.Forget(n.Ssid);
        WifiStatus.Text = ok ? $"Forgot {n.Ssid}. Joining again will ask for the password." : $"Couldn't forget {n.Ssid}.";
        await System.Threading.Tasks.Task.Delay(600);
        await RefreshWifiAsync();
    }

    // ---- Bluetooth

    private async System.Threading.Tasks.Task RefreshBluetoothAsync(bool full)
    {
        int version = ++_panelVersion;
        PanelTitle.Text = "Bluetooth";
        if (WifiList.Tag as string != "bluetooth") WifiList.Children.Clear();
        WifiList.Tag = "bluetooth";
        if (full)
        {
            WifiStatus.Text = "Looking for devices…";
            if (!await BluetoothService.InitAsync())
            {
                WifiList.Children.Clear();
                RenderPanelSwitch(false);
                WifiStatus.Text = "This PC has no Bluetooth, or Windows didn't allow access to it.";
                return;
            }
            RenderPanelSwitch(BluetoothService.RadioOn);
            if (!BluetoothService.RadioOn)
            {
                WifiList.Children.Clear();
                WifiStatus.Text = "Bluetooth is off.";
                BluetoothService.StopDiscovery();
                return;
            }
            _btPaired = await BluetoothService.GetPairedAsync();
            if (version != _panelVersion || !_btView) return;
            BluetoothService.NearbyChanged -= OnNearbyChanged;
            BluetoothService.NearbyChanged += OnNearbyChanged;
            BluetoothService.StartDiscovery();
        }
        RenderBluetoothList();
    }

    private DispatcherOperation? _nearbyRefresh;

    /// <summary>Nearby devices come and go while the view is open (on a background thread).</summary>
    private void OnNearbyChanged()
    {
        if (_nearbyRefresh is { Status: DispatcherOperationStatus.Pending }) return;
        _nearbyRefresh = Dispatcher.InvokeAsync(() => { if (_btView) RenderBluetoothList(); }, DispatcherPriority.Background);
    }

    private void RenderBluetoothList()
    {
        double offset = WifiScroll.VerticalOffset;
        WifiList.Children.Clear();
        WifiList.Children.Add(SectionLabel("My devices"));
        if (_btPaired.Count == 0) WifiList.Children.Add(SectionHint("No paired devices yet."));
        foreach (var d in _btPaired)
        {
            var actions = new List<(string, Func<System.Threading.Tasks.Task>)>();
            if (!d.IsLowEnergy) actions.Add(d.Connected ? ("Disconnect", () => BtConnectAsync(d, false)) : ("Connect", () => BtConnectAsync(d, true)));
            actions.Add(("Forget", () => BtForgetAsync(d)));
            WifiList.Children.Add(CreateListRow(StatusIcons.Device(d.Kind, Brushes.White), d.Name, d.Connected,
                d.Connected ? "Connected" : "Not connected", d.Connected ? _uiBrush : (Brush)FindResource("TertiaryText"), false, actions));
        }

        WifiList.Children.Add(SectionLabel("Other devices"));
        var nearby = BluetoothService.Nearby();
        if (nearby.Count == 0) WifiList.Children.Add(SectionHint("Searching… Put your device in pairing mode."));
        foreach (var d in nearby)
            WifiList.Children.Add(CreateListRow(StatusIcons.Device(d.Kind, Brushes.White), d.Name, false, "", (Brush)FindResource("TertiaryText"), false,
                new List<(string, Func<System.Threading.Tasks.Task>)> { ("Pair", () => BtPairAsync(d)) }));
        WifiScroll.ScrollToVerticalOffset(offset);
        WifiStatus.Text = $"{_btPaired.Count(d => d.Connected)} connected · {nearby.Count} nearby";
    }

    private async System.Threading.Tasks.Task BtConnectAsync(BtDevice d, bool connect)
    {
        WifiStatus.Text = (connect ? "Connecting " : "Disconnecting ") + d.Name + "…";
        bool ok = await BluetoothService.SetConnectedAsync(d, connect);
        if (!ok) WifiStatus.Text = $"Couldn't {(connect ? "connect" : "disconnect")} {d.Name}. Make sure it's on and nearby.";
        await System.Threading.Tasks.Task.Delay(connect ? 2500 : 1200); // the device takes a moment
        await RefreshBluetoothAsync(full: true);
    }

    private async System.Threading.Tasks.Task BtForgetAsync(BtDevice d)
    {
        WifiStatus.Text = $"Forgetting {d.Name}…";
        bool ok = await BluetoothService.ForgetAsync(d);
        WifiStatus.Text = ok ? $"Forgot {d.Name}" : $"Couldn't forget {d.Name}";
        await RefreshBluetoothAsync(full: true);
    }

    private async System.Threading.Tasks.Task BtPairAsync(BtDevice d)
    {
        WifiStatus.Text = $"Pairing with {d.Name}…";
        bool ok = await BluetoothService.PairAsync(d, (prompt, pin) =>
            System.Threading.Tasks.TaskExtensions.Unwrap(Dispatcher.InvokeAsync(() => BluetoothPairWindow.AskAsync(d.Name, prompt, pin, _monitor)).Task));
        WifiStatus.Text = ok ? $"Paired with {d.Name}" : $"Couldn't pair with {d.Name}. Put it in pairing mode and try again.";
        if (ok) await RefreshBluetoothAsync(full: true);
    }

    // ---- shared list pieces

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)FindResource("SecondaryText"),
        Margin = new Thickness(6, 6, 0, 2),
    };

    private TextBlock SectionHint(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = (Brush)FindResource("TertiaryText"),
        Margin = new Thickness(6, 2, 0, 4),
    };

    /// <summary>
    /// A list row: icon, name, and on the right a status ("Connected", "Saved") that turns into
    /// action buttons on hover (Join · Forget, Connect · Forget, Pair…).
    /// </summary>
    private FrameworkElement CreateListRow(FrameworkElement icon, string title, bool bold, string idleText, Brush idleBrush, bool locked,
        List<(string Label, Func<System.Threading.Tasks.Task> Run)> actions)
    {
        var grid = new Grid { Height = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var idle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (locked)
            idle.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 3,6 V 4.5 A 3,3 0 0 1 9,4.5 V 6 M 1.5,6 H 10.5 A 1,1 0 0 1 11.5,7 V 12 A 1,1 0 0 1 10.5,13 H 1.5 A 1,1 0 0 1 0.5,12 V 7 A 1,1 0 0 1 1.5,6 Z"),
                Stroke = (Brush)FindResource("SecondaryText"),
                StrokeThickness = 1.2,
                Width = 12,
                Height = 14,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        idle.Children.Add(new TextBlock { Text = idleText, FontSize = 11.5, Foreground = idleBrush, VerticalAlignment = VerticalAlignment.Center });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        foreach (var (label, run) in actions)
        {
            bool primary = label is "Join" or "Connect" or "Pair";
            var button = new Border
            {
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(6, 0, 0, 0),
                CornerRadius = new CornerRadius(8),
                Background = primary ? _uiBrush : Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = primary && AppSettings.Current.MatchWindowsColors ? Brushes.Black : Brushes.White,
                },
            };
            button.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                buttons.IsEnabled = false;
                buttons.Opacity = 0.5;
                await run();
            };
            buttons.Children.Add(button);
        }
        var right = new Grid { Children = { idle, buttons } };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
        var hovered = new Trigger { Property = IsMouseOverProperty, Value = true };
        hovered.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))));
        style.Triggers.Add(hovered);
        var row = new Border { Style = style, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0), Child = grid };
        row.MouseEnter += (_, _) => { idle.Visibility = Visibility.Collapsed; buttons.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { idle.Visibility = Visibility.Visible; buttons.Visibility = Visibility.Collapsed; };
        row.MouseLeftButtonUp += (_, e) => e.Handled = true; // clicks inside the panel never close or open things
        return row;
    }

    private void RebuildApps()
    {
        AppsPanel.Children.Clear();
        foreach (var app in _pins.Apps) AppsPanel.Children.Add(CreateAppTile(app));
        AppsPanel.Children.Add(CreateAddTile());
        AppsHint.Visibility = _pins.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement CreateAppTile(PinnedApp app)
    {
        if (!_appIcons.TryGetValue(app.ParsingName, out var icon))
            _appIcons[app.ParsingName] = icon = ShellIcons.Get(app.ParsingName, 96);

        var image = new Image { Source = icon, Width = 40, Height = 40 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var tile = CreateTile(image, app.Name);
        tile.ToolTip = app.Name;
        tile.Tag = app;
        tile.PreviewMouseLeftButtonDown += (_, e) => PressApp(tile, app, e);
        tile.MouseMove += (_, e) => MoveApp(tile, e);
        tile.MouseLeftButtonUp += (_, e) => ReleaseApp(tile, app, e);
        tile.LostMouseCapture += (_, _) => { if (_appDrag?.Tile == tile) DropApp(); };

        var menu = new ContextMenu();
        var left = new MenuItem { Header = "Move left", IsEnabled = _pins.Apps.Count > 0 && _pins.Apps[0] != app };
        left.Click += (_, _) => _pins.Move(app, -1);
        var right = new MenuItem { Header = "Move right", IsEnabled = _pins.Apps.Count > 0 && _pins.Apps[^1] != app };
        right.Click += (_, _) => _pins.Move(app, +1);
        var unpin = new MenuItem { Header = "Unpin" };
        unpin.Click += (_, _) => _pins.Unpin(app);
        menu.Items.Add(left);
        menu.Items.Add(right);
        menu.Items.Add(new Separator());
        menu.Items.Add(unpin);
        menu.Opened += (_, _) => _appMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _appMenuOpen = false;
            if (!Notch.IsMouseOver) _closeTimer.Start();
        };
        tile.ContextMenu = menu;
        return tile;
    }

    private FrameworkElement CreateAddTile()
    {
        var plus = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("PlusGlyph"),
            Fill = (Brush)FindResource("SecondaryText"),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
        };
        var tile = CreateTile(plus, "Add");
        tile.ToolTip = "Pin an app";
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseAfterAction();
            AppPickerWindow.ShowFor(_pins, _monitor);
        };
        return tile;
    }

    /// <summary>A dock-style tile: rounded icon well with the name underneath, grows a little on hover.</summary>
    private Border CreateTile(UIElement content, string label)
    {
        var well = new Border
        {
            Width = 58,
            Height = 58,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = content,
        };
        var name = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("SecondaryText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 80,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var scale = new ScaleTransform(1, 1);
        var tile = new Border
        {
            Width = 84,
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            // Scale: hover and lift. Rotate: the jiggle while rearranging. Translate: sliding into place.
            RenderTransform = new TransformGroup { Children = { scale, new RotateTransform(), new TranslateTransform() } },
            Child = new StackPanel { Children = { well, name } },
        };
        var hover = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        tile.MouseEnter += (_, _) =>
        {
            AnimateScale(scale, 1.07);
            well.Background = hover;
            name.Foreground = Brushes.White;
        };
        tile.MouseLeave += (_, _) =>
        {
            AnimateScale(scale, 1);
            well.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            name.Foreground = (Brush)FindResource("SecondaryText");
        };
        return tile;
    }

    // ---- rearranging pinned apps, iOS style: hold (or just drag) an app, the others jiggle and
    // slide aside to make room, let go and it settles into its new place.

    private sealed class AppDragState
    {
        public required PinnedApp App { get; init; }
        public required Border Tile { get; init; }
        public required Point Start { get; init; }
        public required int From { get; init; }
        public int To { get; set; }
        public bool Lifted { get; set; }
        public DispatcherTimer? Hold { get; set; }
    }

    private AppDragState? _appDrag;
    private static readonly TimeSpan AppHoldTime = TimeSpan.FromMilliseconds(380);

    private static (ScaleTransform Scale, RotateTransform Rotate, TranslateTransform Move) TileParts(UIElement tile)
    {
        var group = (TransformGroup)tile.RenderTransform;
        return ((ScaleTransform)group.Children[0], (RotateTransform)group.Children[1], (TranslateTransform)group.Children[2]);
    }

    /// <summary>The app tiles in order (without the "+" tile).</summary>
    private List<Border> AppTiles() => AppsPanel.Children.OfType<Border>().Where(b => b.Tag is PinnedApp).ToList();

    private void PressApp(Border tile, PinnedApp app, MouseButtonEventArgs e)
    {
        if (_appDrag != null) return;
        int index = _pins.Apps.ToList().IndexOf(app);
        if (index < 0) return;
        var drag = new AppDragState { App = app, Tile = tile, Start = e.GetPosition(AppsPanel), From = index, To = index };
        drag.Hold = new DispatcherTimer { Interval = AppHoldTime };
        drag.Hold.Tick += (_, _) => LiftApp(drag);
        drag.Hold.Start();
        _appDrag = drag;
        tile.CaptureMouse();
        e.Handled = true;
    }

    private void MoveApp(Border tile, MouseEventArgs e)
    {
        var drag = _appDrag;
        if (drag == null || drag.Tile != tile) return;
        var p = e.GetPosition(AppsPanel);
        double dx = p.X - drag.Start.X, dy = p.Y - drag.Start.Y;
        if (!drag.Lifted)
        {
            if (Math.Abs(dx) < 6 && Math.Abs(dy) < 6) return;
            LiftApp(drag); // dragging right away works too, no need to wait for the hold
        }

        // The lifted app follows the pointer (a little give vertically, it stays in its row).
        var (_, _, move) = TileParts(tile);
        move.BeginAnimation(TranslateTransform.XProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);
        move.X = dx;
        move.Y = Math.Clamp(dy * 0.35, -14, 14);

        double slot = tile.ActualWidth + tile.Margin.Left + tile.Margin.Right;
        int to = Math.Clamp(drag.From + (int)Math.Round(dx / slot), 0, _pins.Apps.Count - 1);
        if (to == drag.To) return;
        drag.To = to;
        // The others slide aside to open a gap where it would land.
        var tiles = AppTiles();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < tiles.Count; i++)
        {
            if (tiles[i] == tile) continue;
            double shift = drag.From < to && i > drag.From && i <= to ? -slot
                : to < drag.From && i >= to && i < drag.From ? slot : 0;
            TileParts(tiles[i]).Move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
    }

    private void ReleaseApp(Border tile, PinnedApp app, MouseButtonEventArgs e)
    {
        var drag = _appDrag;
        if (drag == null || drag.Tile != tile) return;
        e.Handled = true;
        if (drag.Lifted)
        {
            DropApp();
            return;
        }
        // A plain click: open the app.
        drag.Hold?.Stop();
        _appDrag = null;
        if (tile.IsMouseCaptured) tile.ReleaseMouseCapture();
        PinnedApps.Launch(app);
        CloseAfterAction();
    }

    /// <summary>Picks the app up: it grows and floats, the others start to jiggle.</summary>
    private void LiftApp(AppDragState drag)
    {
        drag.Hold?.Stop();
        if (drag.Lifted || _appDrag != drag) return;
        drag.Lifted = true;
        drag.Tile.ToolTip = null;
        Panel.SetZIndex(drag.Tile, 10);
        var (scale, _, _) = TileParts(drag.Tile);
        var lift = new DoubleAnimation(1.16, TimeSpan.FromMilliseconds(180)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, lift);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, lift);
        drag.Tile.Opacity = 0.95;
        drag.Tile.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Direction = 270, Opacity = 0.55, RenderingBias = RenderingBias.Performance };

        var random = new Random();
        foreach (var other in AppTiles())
        {
            if (other == drag.Tile) continue;
            var wiggle = new DoubleAnimation(-1.6, 1.6, TimeSpan.FromMilliseconds(125))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(random.Next(0, 125)),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            TileParts(other).Rotate.BeginAnimation(RotateTransform.AngleProperty, wiggle);
        }
    }

    /// <summary>Lets go: the app glides into its slot, then the new order is saved (the row is rebuilt exactly where things already are).</summary>
    private void DropApp()
    {
        var drag = _appDrag;
        if (drag == null) return;
        _appDrag = null;
        drag.Hold?.Stop();
        if (drag.Tile.IsMouseCaptured) drag.Tile.ReleaseMouseCapture();
        if (!drag.Lifted) return;

        var tile = drag.Tile;
        double slot = tile.ActualWidth + tile.Margin.Left + tile.Margin.Right;
        var (scale, _, move) = TileParts(tile);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);
        var settle = new DoubleAnimation((drag.To - drag.From) * slot, duration) { EasingFunction = ease };
        settle.Completed += (_, _) =>
        {
            foreach (var other in AppTiles()) TileParts(other).Rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            if (drag.To != drag.From) _pins.MoveTo(drag.App, drag.To); // rebuilds the row
            else
            {
                tile.Effect = null;
                tile.Opacity = 1;
                tile.ToolTip = drag.App.Name;
                Panel.SetZIndex(tile, 0);
            }
            if (!Notch.IsMouseOver) _closeTimer.Start();
        };
        move.BeginAnimation(TranslateTransform.XProperty, settle);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        var down = new DoubleAnimation(1, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, down);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, down);
    }

    private void AppsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        AppsScroll.ScrollToHorizontalOffset(AppsScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    // Drag a shortcut, program or file onto the island to pin it.

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void Notch_DragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        _closeTimer.Stop();
        _appsView = true;
        if (_state != State.Open) SetState(State.Open);
        else ApplyState(animate: true);
    }

    private void Notch_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Notch_DragLeave(object sender, DragEventArgs e)
    {
        if (!Notch.IsMouseOver) _closeTimer.Start();
    }

    private void Notch_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths) _pins.PinPath(path);
        e.Handled = true;
    }

    private void OpenArt_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_snapshot != null)
            OpenArtHover.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    private void OpenArt_MouseLeave(object sender, MouseEventArgs e) =>
        OpenArtHover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));

    /// <summary>
    /// Opens the playing song in the island's player panel. If it's playing elsewhere (e.g. a
    /// browser tab), that is paused and the panel picks up from the same moment, so the song
    /// simply moves into the island instead of playing twice.
    /// </summary>
    private async void OpenArt_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var s = _snapshot;
        if (s == null) return;
        OpenArtHover.BeginAnimation(OpacityProperty, null);
        OpenArtHover.Opacity = 0;

        // Collapse the notch so it doesn't sit over the panel.
        _openTimer.Stop();
        _closeTimer.Stop();
        _hovered = false;
        SetState(State.Closed);

        if (MediaBrowserWindow.IsPlaying(s.Title))
        {
            MediaBrowserWindow.ShowVideo(_monitor, null, s.Title, TimeSpan.Zero);
            return;
        }
        var position = s.EstimatePosition();
        var videoId = await _media.FindVideoIdAsync(s.Title, s.Artist);
        if (s.IsPlaying) await _media.PauseAsync();
        MediaBrowserWindow.ShowVideo(_monitor, videoId, videoId != null ? s.Title : $"{s.Title} {s.Artist}", position);
    }
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
        PlaceKnob(ProgressTrack, ProgressFill, ProgressThumb, fraction);
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

    private void UpdateVolumeUi(float level) => PlaceKnob(VolumeTrack, VolumeFill, VolumeThumb, level);

    /// <summary>
    /// Puts a slider's knob at a fraction of its track. The knob's travel is kept inside the track
    /// (centered on the left end at 0, on the right end at 1): pushed past the edge, the layout
    /// squeezes it into a sliver, which is what showed at full volume.
    /// </summary>
    private static void PlaceKnob(FrameworkElement track, FrameworkElement fill, FrameworkElement knob, double fraction)
    {
        double w = Math.Max(1, track.ActualWidth);
        double d = knob.Width;
        double x = Math.Clamp(fraction, 0, 1) * Math.Max(0, w - d);
        knob.Margin = new Thickness(x, 0, 0, 0);
        fill.Width = x + d / 2;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A damped spring (response ≈ 0.33 s, damping ≈ 0.8): quick, with a small organic overshoot.</summary>
    private sealed class Spring
    {
        private const double Stiffness = 340;
        private const double Damping = 30;

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
