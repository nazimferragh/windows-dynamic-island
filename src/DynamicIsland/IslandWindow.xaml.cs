using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class IslandWindow : Window
{
    private readonly record struct PillShape(double Width, double Height, double Radius);

    private static readonly PillShape CompactIdleShape = new(120, 34, 17);
    private static readonly PillShape CompactMediaShape = new(230, 34, 17);
    private static readonly PillShape ExpandedIdleShape = new(340, 92, 30);
    private static readonly PillShape ExpandedMediaShape = new(400, 168, 36);

    private readonly MediaService _media;
    private readonly SolidColorBrush _accentBrush = new(ColorExtractor.DefaultAccent);
    private readonly RectangleGeometry _clip = new();

    private readonly Spring _width = new(CompactIdleShape.Width);
    private readonly Spring _height = new(CompactIdleShape.Height);
    private readonly Spring _radius = new(CompactIdleShape.Radius);
    private readonly Stopwatch _frameClock = new();
    private TimeSpan _lastFrame;
    private bool _animating;

    private readonly DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _watchdogTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private IntPtr _hwnd;
    private MediaSnapshot? _snapshot;
    private bool _expanded;
    private bool _receivedFirstSnapshot;
    private bool _userHidden;
    private bool _fullscreenHidden;

    public IslandWindow(MediaService media)
    {
        InitializeComponent();
        _media = media;

        CompactEq.BarBrush = _accentBrush;
        ExpandedEq.BarBrush = _accentBrush;
        PillContent.Clip = _clip;

        _expandTimer.Tick += (_, _) => { _expandTimer.Stop(); SetExpanded(true); };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); SetExpanded(false); };
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            if (!Pill.IsMouseOver) SetExpanded(false);
        };
        _tickTimer.Tick += (_, _) => { UpdateClock(); UpdateProgress(); };
        _watchdogTimer.Tick += (_, _) => Watchdog();

        _media.Changed += OnMediaChanged;
        SourceInitialized += OnSourceInitialized;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        UpdateClock();
        ApplyState(animate: false);
        PositionOnScreen();
    }

    /// <summary>Hidden from the tray menu.</summary>
    public bool UserHidden
    {
        get => _userHidden;
        set
        {
            _userHidden = value;
            UpdateVisibility();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeOverlayWindow(_hwnd);
        _tickTimer.Start();
        _watchdogTimer.Start();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(PositionOnScreen);

    private void PositionOnScreen()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    // ---------------------------------------------------------------- state

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        ApplyState(animate: true);
    }

    private void ApplyState(bool animate)
    {
        bool hasMedia = _snapshot != null;
        var shape = _expanded
            ? (hasMedia ? ExpandedMediaShape : ExpandedIdleShape)
            : (hasMedia ? CompactMediaShape : CompactIdleShape);

        _width.Target = shape.Width;
        _height.Target = shape.Height;
        _radius.Target = shape.Radius;
        if (animate)
        {
            StartShapeAnimation();
        }
        else
        {
            _width.Snap();
            _height.Snap();
            _radius.Snap();
            RenderShape();
        }

        Reveal(CompactIdle, !_expanded && !hasMedia, animate);
        Reveal(CompactMedia, !_expanded && hasMedia, animate);
        Reveal(ExpandedIdle, _expanded && !hasMedia, animate);
        Reveal(ExpandedMedia, _expanded && hasMedia, animate);

        bool playing = _snapshot?.IsPlaying == true;
        CompactEq.IsPlaying = playing && !_expanded;
        ExpandedEq.IsPlaying = playing && _expanded;

        UpdateProgress();
    }

    private static void Reveal(FrameworkElement element, bool visible, bool animate)
    {
        if (element.IsHitTestVisible == visible && animate) return;
        element.IsHitTestVisible = visible;

        double opacity = visible ? 1 : 0;
        double scale = visible ? 1 : 0.94;
        var scaleTransform = element.RenderTransform as ScaleTransform;

        if (!animate)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = opacity;
            if (scaleTransform != null)
            {
                scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scaleTransform.ScaleX = scaleTransform.ScaleY = scale;
            }
            return;
        }

        // Incoming content waits a beat so the shape leads and the content follows, like iOS.
        var duration = TimeSpan.FromMilliseconds(visible ? 300 : 120);
        var begin = TimeSpan.FromMilliseconds(visible ? 90 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { BeginTime = begin, EasingFunction = ease });
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

        // Fixed small sub-steps keep the spring stable regardless of frame rate.
        const double step = 1 / 240.0;
        while (dt > 0)
        {
            double h = Math.Min(step, dt);
            _width.Step(h);
            _height.Step(h);
            _radius.Step(h);
            dt -= h;
        }

        bool settled = _width.TrySettle() & _height.TrySettle() & _radius.TrySettle();
        RenderShape();

        if (settled)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    private void RenderShape()
    {
        double w = Math.Max(_width.Value, 20);
        double h = Math.Max(_height.Value, 20);
        double r = Math.Clamp(_radius.Value, 0, Math.Min(w, h) / 2);

        Pill.Width = w;
        Pill.Height = h;
        PillBackground.CornerRadius = new CornerRadius(r);
        _clip.Rect = new Rect(0, 0, w, h);
        _clip.RadiusX = r;
        _clip.RadiusY = r;
    }

    // ---------------------------------------------------------------- media

    private void OnMediaChanged(MediaSnapshot? snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;

        if (snapshot != null)
        {
            TitleText.Text = snapshot.Title;
            ArtistText.Text = snapshot.Artist;
            SourceText.Text = snapshot.Source;
            SourceText.Visibility = string.IsNullOrEmpty(snapshot.Source) ? Visibility.Collapsed : Visibility.Visible;
            SetArtwork(CompactArt, CompactArtGlyph, snapshot.Artwork);
            SetArtwork(ExpandedArt, ExpandedArtGlyph, snapshot.Artwork);
            PlayPauseButton.Content = snapshot.IsPlaying ? "" : "";
            ProgressRow.Visibility = snapshot.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;

            if (previous == null || previous.Accent != snapshot.Accent)
                _accentBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(snapshot.Accent, TimeSpan.FromMilliseconds(500)));
        }

        ApplyState(animate: IsLoaded);

        // New song (or music just started): briefly pop open like iOS does.
        bool newTrack = snapshot != null && (previous == null ? _receivedFirstSnapshot : previous.TrackKey != snapshot.TrackKey);
        _receivedFirstSnapshot = true;
        if (newTrack) Peek();
    }

    private void SetArtwork(Border host, UIElement placeholderGlyph, BitmapSource? art)
    {
        if (art != null && ReferenceEquals(host.Tag, art)) return;
        host.Tag = art;
        if (art != null)
        {
            host.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            placeholderGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            host.Background = (Brush)FindResource("ArtPlaceholder");
            placeholderGlyph.Visibility = Visibility.Visible;
        }
    }

    private void Peek()
    {
        if (_expanded || Pill.IsMouseOver) return;
        SetExpanded(true);
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    private void UpdateProgress()
    {
        var s = _snapshot;
        if (!_expanded || s == null || s.Duration <= TimeSpan.Zero) return;

        var position = s.EstimatePosition();
        double fraction = Math.Clamp(position.TotalSeconds / s.Duration.TotalSeconds, 0, 1);
        ProgressFill.Width = ProgressTrack.ActualWidth * fraction;
        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = "-" + FormatTime(s.Duration - position);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var time = now.ToString("t", CultureInfo.CurrentCulture);
        CompactClock.Text = time;
        BigClock.Text = time;
        DateText.Text = now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
    }

    // ---------------------------------------------------------------- visibility

    private void Watchdog()
    {
        bool fullscreen = NativeMethods.IsFullscreenAppActive();
        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            UpdateVisibility();
        }

        // Other "always on top" windows can end up above us; quietly reclaim the top spot.
        if (IsVisible && _hwnd != IntPtr.Zero) NativeMethods.BringToTopmost(_hwnd);
    }

    private void UpdateVisibility()
    {
        bool show = !_userHidden && !_fullscreenHidden;
        if (show && !IsVisible) Show();
        else if (!show && IsVisible) Hide();
    }

    // ---------------------------------------------------------------- input

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _peekTimer.Stop();
        if (!_expanded) _expandTimer.Start();
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandTimer.Stop();
        if (_expanded) _collapseTimer.Start();
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        _expandTimer.Stop();
        SetExpanded(true);
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    // ---------------------------------------------------------------- helpers

    /// <summary>A damped spring, which gives the island its slightly bouncy, organic motion.</summary>
    private sealed class Spring
    {
        private const double Stiffness = 380;
        private const double Damping = 30; // ~0.77 damping ratio: a small, pleasant overshoot.

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
