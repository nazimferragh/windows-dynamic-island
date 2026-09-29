using System;
using System.Diagnostics;
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
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// One island, pinned to the top-center of one monitor, styled after the macOS notch:
/// flush with the top edge, flared top corners, rounded bottom corners.
/// </summary>
public partial class IslandWindow : Window
{
    private enum State { Closed, Peek, Open }

    /// <summary>Body width/height, flare radius of the top corners, radius of the bottom corners.</summary>
    private readonly record struct Silhouette(double Width, double Height, double Flare, double Radius);

    private static readonly Silhouette ClosedIdleShape = new(190, 32, 6, 10);
    private static readonly Silhouette ClosedIdleHoverShape = new(204, 35, 6, 11);
    private static readonly Silhouette ClosedMediaShape = new(290, 32, 6, 10);
    private static readonly Silhouette ClosedMediaHoverShape = new(304, 35, 6, 11);
    private static readonly Silhouette PeekShape = new(290, 58, 8, 18);
    private static readonly Silhouette OpenShape = new(640, 186, 14, 32);

    private readonly MediaService _media;
    private readonly Int32Rect _monitor; // physical pixels
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
    private readonly DispatcherTimer _watchdogTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private IntPtr _hwnd;
    private MediaSnapshot? _snapshot;
    private State _state = State.Closed;
    private bool _hovered;
    private bool _userHidden;
    private bool _fullscreenHidden;
    private DateTime _calendarDate;

    public IslandWindow(MediaService media, Int32Rect monitorBounds)
    {
        InitializeComponent();
        _media = media;
        _monitor = monitorBounds;

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
        Closed += (_, _) =>
        {
            _media.Changed -= OnMediaChanged;
            _tickTimer.Stop();
            _watchdogTimer.Stop();
            if (_animating) CompositionTarget.Rendering -= OnRendering;
        };
        SourceInitialized += OnSourceInitialized;

        Notch.Height = Height;
        ShowSnapshot(media.Current);
        UpdateCalendar();
        ApplyState(animate: false);
    }

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

    /// <summary>Placed in physical pixels so it lands correctly on monitors with different scaling.</summary>
    private void PositionOnMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(Width * dpi.DpiScaleX);
        int h = (int)Math.Round(Height * dpi.DpiScaleY);
        int x = _monitor.X + (_monitor.Width - w) / 2;
        NativeMethods.PlaceTopmost(_hwnd, x, _monitor.Y, w, h);
    }

    // ---------------------------------------------------------------- state

    private void SetState(State state)
    {
        if (_state == state) return;
        _state = state;
        ApplyState(animate: true);
    }

    private void ApplyState(bool animate)
    {
        bool hasMedia = _snapshot != null;
        var shape = _state switch
        {
            State.Open => OpenShape,
            State.Peek when hasMedia => PeekShape,
            _ when hasMedia => _hovered ? ClosedMediaHoverShape : ClosedMediaShape,
            _ => _hovered ? ClosedIdleHoverShape : ClosedIdleShape,
        };

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

        Reveal(ClosedMedia, _state != State.Open && hasMedia, animate);
        Reveal(PeekText, _state == State.Peek && hasMedia, animate);
        Reveal(OpenContent, _state == State.Open, animate);

        bool playing = _snapshot?.IsPlaying == true;
        ClosedEq.IsPlaying = playing && _state != State.Open;
        OpenEq.IsPlaying = playing && _state == State.Open;

        if (_state == State.Open)
        {
            UpdateProgress();
            UpdateBattery();
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
        double flare = Math.Clamp(_flare.Value, 0, h / 3);
        double r = Math.Clamp(_radius.Value, 0, Math.Min(w / 2, h - flare));
        double left = (ActualWidthOrDefault() - w) / 2 - flare;

        var geometry = BuildNotchGeometry(left, w, h, flare, r);
        NotchShape.Data = geometry;
        NotchContent.Clip = geometry;

        // The drop shadow grows in as the notch opens and disappears when it's closed.
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
    /// The macOS notch outline: the top edge is flush with the screen, the top corners flare outward
    /// into the screen edge (concave), and the bottom corners are rounded.
    /// </summary>
    private static Geometry BuildNotchGeometry(double left, double width, double height, double flare, double radius)
    {
        double outerLeft = left, outerRight = left + width + flare * 2;
        double bodyLeft = left + flare, bodyRight = outerRight - flare;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(outerLeft, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(outerRight, 0), true, false);
            ctx.QuadraticBezierTo(new Point(bodyRight, 0), new Point(bodyRight, flare), true, true);
            ctx.LineTo(new Point(bodyRight, height - radius), true, false);
            ctx.ArcTo(new Point(bodyRight - radius, height), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(bodyLeft + radius, height), true, false);
            ctx.ArcTo(new Point(bodyLeft, height - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(bodyLeft, flare), true, false);
            ctx.QuadraticBezierTo(new Point(bodyLeft, 0), new Point(outerLeft, 0), true, true);
        }
        geometry.Freeze();
        return geometry;
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
        SourceText.Text = snapshot.Source;
        PeekText.Text = string.IsNullOrEmpty(snapshot.Artist) ? snapshot.Title : $"{snapshot.Title}  Â·  {snapshot.Artist}";
        OpenEq.Visibility = Visibility.Visible;
        ProgressRow.Visibility = snapshot.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        Transport.IsEnabled = true;
        PlayPauseGlyph.Data = (Geometry)FindResource(snapshot.IsPlaying ? "PauseGlyph" : "PlayGlyph");
        SetArtwork(snapshot.Artwork);

        if (previous == null || previous.Accent != snapshot.Accent)
            _accentBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(snapshot.Accent, TimeSpan.FromMilliseconds(500)));
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
    }

    private void UpdateProgress()
    {
        var s = _snapshot;
        if (s == null || s.Duration <= TimeSpan.Zero) return;

        var position = s.EstimatePosition();
        double fraction = Math.Clamp(position.TotalSeconds / s.Duration.TotalSeconds, 0, 1);
        ProgressFill.Width = ProgressTrack.ActualWidth * fraction;
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
        if (show && !IsVisible) Show();
        else if (!show && IsVisible) Hide();
    }

    // ---------------------------------------------------------------- input

    private void Notch_MouseEnter(object sender, MouseEventArgs e)
    {
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
        _openTimer.Stop();
        _hovered = false;
        if (_state == State.Closed) ApplyState(animate: true);
        else _closeTimer.Start();
    }

    private void Notch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state == State.Open) return;
        _openTimer.Stop();
        SetState(State.Open);
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    // ---------------------------------------------------------------- helpers

    /// <summary>A damped spring (response â‰ˆ 0.4 s, damping â‰ˆ 0.8): quick, with a small organic overshoot.</summary>
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
