using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>
/// The macOS 26 dock, replacing the Windows taskbar: Liquid Glass bar, icons that grow under the
/// pointer, launch bounce, dots under open apps, drag to reorder or remove, right-click menus.
/// Motion values are the ones the owner approved in the Dock Lab preview
/// (https://claude.ai/artifact/L17cXCPHWhc4qd1YwJNSFt): size 56, magnification 1.6, spread 2.6
/// icons, follow 0.24, bounce 0.55. It only draws while something moves (never per frame at rest),
/// and steps aside over full-screen apps like the Mac dock.
/// </summary>
internal sealed class DockWindow : Window
{
    private const double Spread = 2.6, Follow = 0.24, BounceHeight = 0.55, Hop = 0.46, BottomMargin = 6;

    private sealed class Slot
    {
        public string Key = "";
        public bool IsSeparator;
        public DockEntry? Entry;
        public Image? Img;
        public Ellipse? Dot;
        public Border? Badge;
        public TextBlock? BadgeText;
        public Rectangle? Line;
        public double Fill = 1, Sc = 1, Appear, AppearTarget = 1, BounceStart = -1, X, Y, W;
        public bool Launching, Leaving;
        public string IconKey = "";
    }

    private readonly DockModel _model;
    private readonly NotificationService _notifications;
    private readonly Action _openApps;
    private readonly Int32Rect _monitor;
    private readonly Canvas _root = new() { ClipToBounds = false };
    private readonly Border _bar, _sheen;
    private readonly Border[] _shadows = new Border[3];
    private readonly Rectangle _catcher = new() { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Visibility = Visibility.Collapsed };
    private readonly Border _tip;
    private readonly TextBlock _tipText = new() { Foreground = Brushes.White, FontSize = 13 };
    private List<Slot> _slots = new();
    private readonly Dictionary<string, int> _badges = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _trashPoll = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Window _backdrop;
    private IntPtr _hwnd, _backHwnd;
    private AppBar? _appBar;
    private double _base = 56, _mag = 1.6, _pad, _gap, _sepW, _barH, _winW, _winH, _s = 1;
    private int _winX, _winY;
    private (int X, int Y, int W, int H) _backRect;
    private bool _rendering, _hoverOn, _trashFull, _hiddenForFullscreen, _built;
    private TimeSpan _lastFrame;
    private Slot? _press, _drag;
    private Point _pressAt;
    private bool _dragOutside;

    public DockWindow(DockModel model, NotificationService notifications, Int32Rect monitor, Action openApps)
    {
        _model = model;
        _notifications = notifications;
        _monitor = monitor;
        _openApps = openApps;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true; // see NativeMethods.MakeOverlayWindow: false would pin it to one virtual desktop
        ShowActivated = false;
        Topmost = true;
        Title = "Dock";
        Content = _root;

        for (int i = 0; i < _shadows.Length; i++)
        {
            _shadows[i] = new Border { Background = new SolidColorBrush(Color.FromArgb((byte)(new[] { 26, 16, 10 }[i]), 0, 0, 0)), IsHitTestVisible = false };
            _root.Children.Add(_shadows[i]);
        }
        // Liquid Glass: a light tint over the blurred backdrop, a bright rim along the top, a soft sheen.
        var sheen = new RadialGradientBrush { Center = new Point(0.12, -0.2), GradientOrigin = new Point(0.12, -0.2), RadiusX = 1.2, RadiusY = 0.9 };
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x47, 255, 255, 255), 0));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.45));
        _sheen = new Border { Background = sheen, IsHitTestVisible = false };
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 255, 255, 255), 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 255, 255, 255), 0.5));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, 255, 255, 255), 1));
        _bar = new Border
        {
            Background = new LinearGradientBrush(Color.FromArgb(0x33, 255, 255, 255), Color.FromArgb(0x12, 255, 255, 255), 90),
            BorderBrush = rim,
            BorderThickness = new Thickness(0.8),
            Child = _sheen,
        };
        _root.Children.Add(_bar);
        _root.Children.Add(_catcher);
        _tip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xA8, 0x26, 0x26, 0x2A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 3, 11, 5),
            Child = _tipText,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        Panel.SetZIndex(_tip, 50);
        _root.Children.Add(_tip);
        TextOptions.SetTextFormattingMode(_root, TextFormattingMode.Display);

        _backdrop = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = true, ShowActivated = false, Topmost = true, Width = 10, Height = 10, Title = "Dock glass",
        };
        _backdrop.SourceInitialized += (_, _) =>
        {
            _backHwnd = new WindowInteropHelper(_backdrop).Handle;
            NativeMethods.MakeOverlayWindow(_backHwnd);
            WindowApi.MakeClickThrough(_backHwnd);
            GlassBlur.Enable(_backHwnd);
        };

        SourceInitialized += OnSourceInitialized;
        MouseEnter += (_, _) => StartRendering();
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnLeftDown;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonUp += OnRightUp;
        LostMouseCapture += (_, _) => { if (_drag != null) EndDrag(drop: false); };

        _model.Changed += OnModelChanged;
        _notifications.Received += OnNotification;
        GameMode.Changed += OnGameModeChanged;
        _watch.Tick += (_, _) => KeepOnTop();
        GameMode.Tune(_watch, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        _trashPoll.Tick += (_, _) => CheckTrash();
        GameMode.Tune(_trashPoll, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));
        Closed += (_, _) =>
        {
            _model.Changed -= OnModelChanged;
            _notifications.Received -= OnNotification;
            GameMode.Changed -= OnGameModeChanged;
            _watch.Stop();
            _trashPoll.Stop();
            StopRendering();
            _appBar?.Unregister();
            _backdrop.Close();
        };
        ApplySettings();
    }

    /// <summary>Icon size and magnification from Settings.</summary>
    public void ApplySettings()
    {
        var s = AppSettings.Current;
        _base = Math.Clamp(s.DockIconSize, 32, 96);
        _mag = Math.Clamp(s.DockMagnification, 1, 2.2);
        _pad = Math.Round(_base * 0.13);
        _gap = Math.Round(_base * 0.1);
        _sepW = Math.Round(_base * 0.36);
        _barH = _base + _pad * 2;
        if (_hwnd != IntPtr.Zero) { Reposition(); StartRendering(); }
    }

    // ---------------------------------------------------------------- window placement

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeOverlayWindow(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _backdrop.Show();
        _appBar = new AppBar(_hwnd);
        _appBar.Register();
        OnModelChanged();
        CheckTrash();
        Reposition();
        _watch.Start();
        _trashPoll.Start();
        OnGameModeChanged();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
    }

    /// <summary>Sizes the window for the current icons (room for magnified icons and labels) and reserves the dock's strip.</summary>
    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        _s = WindowApi.ScaleAt(_monitor.X + _monitor.Width / 2, _monitor.Y + _monitor.Height / 2);
        double monW = _monitor.Width / _s;
        double baseW = _pad * 2 + _slots.Count(x => !x.Leaving) * (_base + _gap) + 2 * _base;
        _winW = Math.Min(monW, baseW + _base * (_mag - 1) * Spread * 1.3 + 240);
        _winH = Math.Ceiling(_base * Math.Max(_mag, 1 + BounceHeight * 1.1) + _pad + BottomMargin + 48);
        Width = _winW;
        Height = _winH;
        _root.Width = _winW;
        _root.Height = _winH;
        int w = (int)Math.Round(_winW * _s), h = (int)Math.Round(_winH * _s);
        _winX = _monitor.X + (_monitor.Width - w) / 2;
        _winY = _monitor.Y + _monitor.Height - h;
        WindowApi.PlaceTopmost(_hwnd, _winX, _winY, w, h);
        Reserve();
        _backRect = default;
        Layout(0);
    }

    private void Reserve()
    {
        if (_appBar is not { IsRegistered: true }) return;
        var m = new WindowApi.RECT { Left = _monitor.X, Top = _monitor.Y, Right = _monitor.X + _monitor.Width, Bottom = _monitor.Y + _monitor.Height };
        _appBar.ReserveBottom(m, (int)Math.Round((_barH + BottomMargin + 2) * _s));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar == null) return IntPtr.Zero;
        if (msg == (int)AppBar.TaskbarCreatedMessage)
        {
            // Explorer restarted: our reserved strip is gone, claim it again.
            _appBar.ForgetRegistration();
            _appBar.Register();
            Reserve();
            return IntPtr.Zero;
        }
        if (msg == (int)_appBar.CallbackMessage && wParam.ToInt32() == AppBar.ABN_POSCHANGED)
        {
            Reserve();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void KeepOnTop()
    {
        if (!IsVisible || _hwnd == IntPtr.Zero || GameMode.Active) return;
        NativeMethods.BringToTopmost(_hwnd);
        _backRect = default;
        Layout(0);
    }

    /// <summary>Like the Mac dock, it steps aside while a full-screen app or game is in front on its screen.</summary>
    private void OnGameModeChanged()
    {
        bool hide = GameMode.IsOn(_monitor);
        if (hide == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hide;
        if (hide)
        {
            StopRendering();
            _backdrop.Hide();
            Hide();
        }
        else
        {
            _backdrop.Show();
            Show();
            KeepOnTop();
        }
    }

    // ---------------------------------------------------------------- content

    private void OnModelChanged()
    {
        var wanted = new List<(string Key, DockEntry? Entry)>();
        foreach (var e in _model.Entries) wanted.Add((e.Id, e));
        wanted.Add(("sep", null));
        wanted.Add(("special:" + DockModel.Downloads, new DockEntry { Special = DockModel.Downloads, Name = "Downloads" }));
        wanted.Add(("special:" + DockModel.Trash, new DockEntry { Special = DockModel.Trash, Name = "Trash" }));

        var old = _slots.Where(x => !x.Leaving).ToDictionary(x => x.Key);
        var list = new List<Slot>();
        foreach (var (key, entry) in wanted)
        {
            if (old.Remove(key, out var slot)) slot.Entry = entry;
            else slot = NewSlot(key, entry, appearing: _built);
            if (entry != null) UpdateIcon(slot);
            list.Add(slot);
        }
        // Gone apps shrink away where they were.
        foreach (var gone in old.Values)
        {
            gone.Leaving = true;
            gone.AppearTarget = 0;
            int i = _slots.IndexOf(gone);
            list.Insert(Math.Clamp(i, 0, list.Count), gone);
        }
        foreach (var x in _slots.Where(x => x.Leaving && !list.Contains(x))) list.Add(x);
        int before = _slots.Count(x => !x.Leaving);
        _slots = list;
        _built = true;

        // A badge goes away once its app is in front.
        foreach (var s in _slots)
            if (s.Entry != null && _badges.ContainsKey(s.Key) && s.Entry.Windows.Any(w => w.Hwnd == _model.Foreground))
                _badges.Remove(s.Key);

        if (_slots.Count(x => !x.Leaving) != before) Reposition();
        StartRendering();
    }

    private Slot NewSlot(string key, DockEntry? entry, bool appearing)
    {
        var s = new Slot { Key = key, Entry = entry, Appear = appearing ? 0 : 1 };
        if (entry == null)
        {
            s.IsSeparator = true;
            s.Line = new Rectangle { Width = 1, Fill = new SolidColorBrush(Color.FromArgb(0x47, 255, 255, 255)), IsHitTestVisible = false };
            _root.Children.Add(s.Line);
            return s;
        }
        s.Img = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(s.Img, BitmapScalingMode.HighQuality);
        s.Dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        s.BadgeText = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        s.Badge = new Border { Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), Child = s.BadgeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _root.Children.Add(s.Img);
        _root.Children.Add(s.Dot);
        _root.Children.Add(s.Badge);
        Panel.SetZIndex(s.Badge, 5);
        return s;
    }

    private void UpdateIcon(Slot s)
    {
        if (s.Entry == null || s.Img == null) return;
        string key = s.Entry.Special == DockModel.Trash ? (_trashFull ? "trash-full" : "trash") : s.Key;
        if (s.IconKey == key && s.Img.Source != null) return;
        var icon = DockIcons.For(s.Entry, _trashFull);
        s.IconKey = key;
        s.Img.Source = icon?.Source;
        s.Fill = icon?.Fill ?? 1;
    }

    private void RemoveVisuals(Slot s)
    {
        foreach (UIElement? el in new UIElement?[] { s.Img, s.Dot, s.Badge, s.Line }) if (el != null) _root.Children.Remove(el);
    }

    private void CheckTrash()
    {
        bool full = RecycleBin.HasItems();
        if (full == _trashFull) return;
        _trashFull = full;
        foreach (var s in _slots.Where(x => x.Entry?.Special == DockModel.Trash)) UpdateIcon(s);
    }

    private void OnNotification(NotificationInfo n)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var slot = _slots.FirstOrDefault(s => s.Entry is { } e && !s.Leaving &&
                ((n.AppId.Length > 0 && (string.Equals(e.Aumid, n.AppId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.Pin?.Target, n.AppId, StringComparison.OrdinalIgnoreCase)))
                 || (n.AppName.Length > 0 && string.Equals(e.Name, n.AppName, StringComparison.OrdinalIgnoreCase))));
            if (slot?.Entry == null || slot.Entry.Windows.Any(w => w.Hwnd == WindowApi.GetForegroundWindow())) return;
            _badges[slot.Key] = _badges.GetValueOrDefault(slot.Key) + 1;
            StartRendering();
        });
    }

    // ---------------------------------------------------------------- animation

    private void StartRendering()
    {
        if (_rendering || _hiddenForFullscreen || _hwnd == IntPtr.Zero) return;
        _rendering = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (t - _lastFrame).TotalSeconds);
        _lastFrame = t;
        if (!Layout(dt)) StopRendering();
    }

    private Point CursorLocal()
    {
        WindowApi.GetCursorPos(out var p);
        return new Point((p.X - _winX) / _s, (p.Y - _winY) / _s);
    }

    private double BaseWidth(Slot s) => s.IsSeparator ? _sepW : _base * s.Appear;
    private double CurrentWidth(Slot s) => s.IsSeparator ? _sepW : _base * s.Sc * s.Appear;

    /// <summary>Lays everything out for this frame. Returns true while anything still moves.</summary>
    private bool Layout(double dt)
    {
        if (_hwnd == IntPtr.Zero) return false;
        var m = CursorLocal();
        double k = 1 - Math.Pow(1 - Follow, dt * 60), ka = 1 - Math.Pow(1 - 0.2, dt * 60);
        double now = _clock.Elapsed.TotalSeconds;
        double cx = _winW / 2, barTop = _winH - BottomMargin - _barH;

        // Where each icon sits at rest: the magnification falls off from there (like the Mac dock).
        double w0 = _pad * 2 - _gap;
        foreach (var s in _slots) w0 += BaseWidth(s) + _gap;
        var centers = new Dictionary<Slot, double>();
        double bx = cx - w0 / 2 + _pad;
        foreach (var s in _slots)
        {
            double sw = BaseWidth(s);
            centers[s] = bx + sw / 2;
            bx += sw + _gap;
        }
        double magTop = barTop - (_hoverOn ? _base * (_mag - 1) : 0);
        _hoverOn = _drag != null || (IsVisible && m.X >= cx - w0 / 2 - 8 && m.X <= cx + w0 / 2 + 8 && m.Y >= magTop && m.Y <= _winH + 1);
        bool busy = _hoverOn;
        double range = Spread * _base;

        foreach (var s in _slots)
        {
            double target = 1;
            if (_hoverOn && !s.IsSeparator && _mag > 1)
            {
                double d = Math.Abs(m.X - centers[s]);
                if (d < range) target = 1 + (_mag - 1) * (Math.Cos(Math.PI * d / range) + 1) / 2;
            }
            if (dt <= 0) { }
            else if (Math.Abs(target - s.Sc) > 0.002) { s.Sc += (target - s.Sc) * k; busy = true; }
            else s.Sc = target;
            if (dt <= 0) { }
            else if (Math.Abs(s.AppearTarget - s.Appear) > 0.01) { s.Appear += (s.AppearTarget - s.Appear) * ka; busy = true; }
            else s.Appear = s.AppearTarget;
        }
        foreach (var gone in _slots.Where(s => s.Leaving && s.Appear <= 0).ToList())
        {
            RemoveVisuals(gone);
            _slots.Remove(gone);
        }

        double W = _pad * 2 - _gap;
        foreach (var s in _slots) W += CurrentWidth(s) + _gap;
        double left = cx - W / 2, x = left + _pad, radius = _barH * 0.44;
        Place(_bar, left, barTop, W, _barH);
        _bar.CornerRadius = new CornerRadius(radius);
        _sheen.CornerRadius = new CornerRadius(radius);
        for (int i = 0; i < _shadows.Length; i++)
        {
            double grow = new[] { 1.0, 4, 9 }[i];
            Place(_shadows[i], left - grow, barTop - grow + 3 + i * 1.5, W + grow * 2, _barH + grow * 2);
            _shadows[i].CornerRadius = new CornerRadius(radius + grow);
        }

        Slot? hovered = null;
        foreach (var s in _slots)
        {
            if (s.IsSeparator)
            {
                Place(s.Line!, x + _sepW / 2, barTop + _pad + _base * 0.1, 1, _base * 0.8);
                s.X = x; s.W = _sepW; s.Y = barTop;
                x += _sepW + _gap;
                continue;
            }
            double size = _base * s.Sc * s.Appear, lift = 0;
            if (s.BounceStart >= 0)
            {
                double bt = now - s.BounceStart;
                int hop = (int)(bt / Hop);
                bool done = hop >= 1 && (!s.Launching || s.Entry?.Running == true || hop >= 6);
                if (done) { s.BounceStart = -1; s.Launching = false; }
                else
                {
                    double ph = bt % Hop / Hop;
                    lift = BounceHeight * _base * 1.1 * 4 * ph * (1 - ph);
                    busy = true;
                }
            }
            double iy = barTop + _pad + _base - size - lift, ix = x;
            s.X = x; s.W = size; s.Y = iy;
            if (s == _drag) { ix = m.X - size / 2; iy = m.Y - size / 2; }
            double inner = size * s.Fill;
            Place(s.Img!, ix + (size - inner) / 2, iy + (size - inner) / 2, inner, inner);
            s.Img!.Opacity = s.Leaving ? s.Appear : s == _drag && _dragOutside ? 0.55 : 1;
            Panel.SetZIndex(s.Img, s == _drag ? 20 : 1);

            bool running = s.Entry?.Running == true && s.Appear > 0.5;
            s.Dot!.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (running) Place(s.Dot, x + size / 2 - 2, barTop + _pad + _base + _pad * 0.5 - 2, 4, 4);

            int badge = _badges.GetValueOrDefault(s.Key);
            s.Badge!.Visibility = badge > 0 && s.Appear > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            if (badge > 0)
            {
                double bh = Math.Max(14, size * 0.36);
                s.BadgeText!.Text = badge > 99 ? "99+" : badge.ToString();
                s.BadgeText.FontSize = bh * 0.6;
                s.Badge.CornerRadius = new CornerRadius(bh / 2);
                s.Badge.Padding = new Thickness(bh * 0.25, 0, bh * 0.25, 0);
                s.Badge.MinWidth = bh;
                s.Badge.Height = bh;
                Canvas.SetLeft(s.Badge, ix + size - bh * 0.85);
                Canvas.SetTop(s.Badge, iy - bh * 0.15);
            }
            if (_drag == null && !s.Leaving && m.X >= x - _gap / 2 && m.X <= x + size + _gap / 2 && m.Y >= iy - 4 && m.Y <= _winH + 1) hovered = s;
            x += size + _gap;
        }

        // While the pointer is over the dock, the space above the bar (where icons grow) keeps the
        // pointer too; at rest the transparent parts let clicks through to the windows below.
        if (_hoverOn)
        {
            double top = barTop - _base * (_mag - 1) - 4;
            Place(_catcher, left, top, W, _winH - top);
            _catcher.Visibility = Visibility.Visible;
        }
        else _catcher.Visibility = Visibility.Collapsed;

        // Name label above the icon under the pointer.
        if (_drag != null && _dragOutside) ShowTip("Remove", m.X, m.Y - _base / 2 - 34);
        else if (hovered?.Entry != null && DockMenu.Current == null) ShowTip(hovered.Entry.Name, hovered.X + hovered.W / 2, hovered.Y - 36);
        else _tip.Opacity = 0;

        if (_drag != null) ReorderWhileDragging(m);
        UpdateBackdrop(left, barTop, W, _barH, radius);
        return busy || _slots.Any(s => s.BounceStart >= 0);
    }

    private void ShowTip(string text, double centerX, double top)
    {
        if (_tipText.Text != text) { _tipText.Text = text; _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); }
        double w = _tip.DesiredSize.Width;
        Canvas.SetLeft(_tip, Math.Clamp(centerX - w / 2, 2, _winW - w - 2));
        Canvas.SetTop(_tip, Math.Max(0, top));
        _tip.Opacity = 1;
    }

    private static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
    }

    /// <summary>Keeps the blur window exactly under the bar.</summary>
    private void UpdateBackdrop(double x, double y, double w, double h, double r)
    {
        if (_backHwnd == IntPtr.Zero) return;
        var rect = ((int)Math.Round(_winX + x * _s), (int)Math.Round(_winY + y * _s), (int)Math.Round(w * _s), (int)Math.Round(h * _s));
        if (rect == _backRect) return;
        bool resized = rect.Item3 != _backRect.W || rect.Item4 != _backRect.H;
        _backRect = rect;
        WindowApi.PlaceBelow(_backHwnd, _hwnd, rect.Item1, rect.Item2, rect.Item3, rect.Item4);
        if (resized) GlassBlur.SetRoundedRegion(_backHwnd, rect.Item3, rect.Item4, (int)Math.Round(r * _s));
    }

    // ---------------------------------------------------------------- input

    private Slot? SlotAt(Point m) => _slots.FirstOrDefault(s => !s.IsSeparator && !s.Leaving && s.Entry != null
        && m.X >= s.X - _gap / 2 && m.X <= s.X + s.W + _gap / 2 && m.Y >= s.Y - 4 && m.Y <= _winH + 1);

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        StartRendering();
        if (_press == null || _drag != null) return;
        var m = CursorLocal();
        if ((m - _pressAt).Length > 6 && _press.Entry?.Pin != null && _press.Entry.Special != DockModel.Finder)
        {
            _drag = _press;
            _dragOutside = false;
        }
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        DockMenu.CloseCurrent();
        var m = CursorLocal();
        _press = SlotAt(m);
        _pressAt = m;
        if (_press != null) CaptureMouse();
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _press;
        _press = null;
        if (_drag != null) { EndDrag(drop: true); ReleaseMouseCapture(); return; }
        ReleaseMouseCapture();
        if (pressed != null && pressed == SlotAt(CursorLocal())) Click(pressed);
    }

    private void Click(Slot s)
    {
        var e = s.Entry;
        if (e == null) return;
        _badges.Remove(s.Key);
        if (e.Special == DockModel.AppsGrid) { _openApps(); return; }
        if (_model.Activate(e))
        {
            s.Launching = true;
            s.BounceStart = _clock.Elapsed.TotalSeconds;
        }
        StartRendering();
    }

    private void ReorderWhileDragging(Point m)
    {
        var d = _drag!;
        _dragOutside = m.Y < _winH - BottomMargin - _barH - 70;
        // Kept apps only (Finder stays first); open-but-not-kept apps, Downloads and Trash don't move.
        var kept = _slots.Where(s => s.Entry?.Pin != null && !s.Leaving).ToList();
        int cur = kept.IndexOf(d), target = cur;
        for (int i = 1; i < kept.Count; i++)
        {
            var o = kept[i];
            if (o == d) continue;
            double c = o.X + o.W / 2;
            if (i < cur && m.X < c) { target = i; break; }
            if (i > cur && m.X > c) target = i;
        }
        if (target == cur || target < 1) return;
        int from = _slots.IndexOf(d), to = _slots.IndexOf(kept[target]);
        _slots.RemoveAt(from);
        _slots.Insert(to, d);
    }

    private void EndDrag(bool drop)
    {
        var d = _drag;
        _drag = null;
        if (d?.Entry == null) return;
        if (drop && _dragOutside)
        {
            _model.Remove(d.Entry);
        }
        else
        {
            int index = _slots.Where(s => s.Entry?.Pin != null && !s.Leaving).ToList().IndexOf(d);
            _model.MoveTo(d.Entry, index);
        }
        _dragOutside = false;
        StartRendering();
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        var s = SlotAt(CursorLocal());
        if (s?.Entry == null) return;
        var entry = s.Entry;
        var items = new List<DockMenu.Item>();
        if (entry.Special == DockModel.Trash)
        {
            items.Add(new("Open", () => _model.Activate(entry)));
            items.Add(DockMenu.Item.Separator);
            items.Add(new("Empty Trash", RecycleBin.Empty, Enabled: _trashFull));
        }
        else if (entry.Special == DockModel.Downloads)
        {
            items.Add(new("Open \"Downloads\"", () => _model.Activate(entry)));
        }
        else
        {
            foreach (var w in entry.Windows.Take(8))
            {
                var hwnd = w.Hwnd;
                string title = WindowApi.GetTitle(hwnd);
                if (title.Length > 48) title = title[..47] + "…";
                items.Add(new(title, () => { if (WindowApi.IsMinimized(hwnd)) WindowApi.ShowWindow(hwnd, 9); WindowApi.ForceForeground(hwnd); }));
            }
            if (entry.Windows.Count > 0) items.Add(DockMenu.Item.Separator);
            if (entry.Special == DockModel.Finder) items.Add(new("New Finder Window", () => System.Diagnostics.Process.Start("explorer.exe")?.Dispose()));
            if (entry.Pin == null) items.Add(new("Keep in Dock", () => _model.Keep(entry)));
            else if (entry.Special != DockModel.Finder) items.Add(new("Remove from Dock", () => _model.Remove(entry)));
            if (entry.Special.Length == 0) items.Add(new("Show in Explorer", () => DockModel.ShowInExplorer(entry)));
            items.Add(DockMenu.Item.Separator);
            if (entry.Running && entry.Special != DockModel.Finder)
            {
                items.Add(new("Hide", () => DockModel.Hide(entry)));
                items.Add(new("Quit", () => DockModel.Quit(entry)));
            }
            else if (!entry.Running) items.Add(new("Open", () => Click(s)));
            else items.Add(new("Hide", () => DockModel.Hide(entry)));
        }
        while (items.Count > 0 && items[^1].IsSeparator) items.RemoveAt(items.Count - 1);
        int ax = (int)Math.Round(_winX + (s.X + s.W / 2) * _s), ay = (int)Math.Round(_winY + (s.Y - 10) * _s);
        DockMenu.ShowAt(items, ax, ay, _monitor);
        _tip.Opacity = 0;
        e.Handled = true;
    }
}

/// <summary>The Recycle Bin, for the Trash icon.</summary>
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Info { public int Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref Info info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static bool HasItems()
    {
        try
        {
            var info = new Info { Size = Marshal.SizeOf<Info>() };
            return SHQueryRecycleBin(null, ref info) == 0 && info.Items > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Empties it; Windows asks for confirmation first, as in Explorer.</summary>
    public static void Empty()
    {
        try { SHEmptyRecycleBin(IntPtr.Zero, null, 0); }
        catch (Exception ex) { Log.Error("Couldn't empty the Recycle Bin", ex); }
    }
}
