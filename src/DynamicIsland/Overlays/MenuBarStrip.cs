using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Controls;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>
/// The equivalent of the Mac menu bar behind the notch. It reserves a strip at the top of the monitor
/// so maximized apps sit below the island instead of under it, and turns solid black while a maximized
/// window is on screen, so the notch blends into it the way it does on a Mac.
/// With the menu bar on (Settings › Menu bar) it's a translucent bar: the app in front on the left;
/// Wi‑Fi, Bluetooth, battery, search, quick settings and the clock on the right, all live, each
/// opening the matching Windows panel.
/// </summary>
internal sealed class MenuBarStrip : Window
{
    /// <summary>Same as the closed notch, so the notch merges seamlessly into the bar.</summary>
    public const double StripHeight = 32;

    private readonly Int32Rect _monitor;
    private readonly Border _fill = new() { Background = Brushes.Black, Opacity = 0 };
    private readonly Border _tint = new();
    private readonly TextBlock _appName = new()
    {
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16, 0, 0, 1),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _clockText = "";
    private bool _light;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IntPtr _hwnd;
    private AppBar? _appBar;
    private bool _black;
    private bool _wantReserved;

    public MenuBarStrip(Int32Rect monitor)
    {
        _monitor = monitor;
        ReservedTop = monitor.Y;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        // Must be true: ShowInTaskbar=false gives the window a hidden owner, which pins it to one
        // virtual desktop. The tool-window style set below keeps it out of the taskbar instead.
        ShowInTaskbar = true;
        ShowActivated = false;
        Topmost = true;
        Width = 100;
        Height = StripHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_appName);
        Grid.SetColumn(_items, 1);
        bar.Children.Add(_items);
        Content = new Grid { Children = { _tint, _fill, bar } };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
            WindowApi.SetClickThrough(_hwnd, !AppSettings.Current.MenuBarEnabled);
            _appBar = new AppBar(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            if (_wantReserved) Reserve();
        };
        _poll.Tick += (_, _) => SetBlack(WindowApi.HasMaximizedWindowOn(MonitorRect));
        _poll.Start();
        _clockTimer.Tick += (_, _) =>
        {
            if (ClockText() != _clockText) RefreshBar();
        };
        _clockTimer.Start();
        SystemStatus.Current.Changed += RefreshBar;
        AppSettings.Changed += RefreshBar;
        Closed += (_, _) =>
        {
            _poll.Stop();
            _clockTimer.Stop();
            SystemStatus.Current.Changed -= RefreshBar;
            AppSettings.Changed -= RefreshBar;
            Release();
        };
        RefreshBar();
    }

    /// <summary>Raised when Windows reports a fullscreen app opening or closing.</summary>
    public event Action? FullscreenAppChanged;

    /// <summary>Raised when the strip moved (e.g. a Windows 10 taskbar docked at the top pushes it down).</summary>
    public event Action? PositionChanged;

    /// <summary>Top of the strip in physical pixels: the monitor top, or just below a top-docked taskbar.</summary>
    public int ReservedTop { get; private set; }

    /// <summary>Bottom edge of the strip, physical pixels (where the usable screen starts).</summary>
    public int ReservedBottom { get; private set; }

    private WindowApi.RECT MonitorRect => new()
    {
        Left = _monitor.X,
        Top = _monitor.Y,
        Right = _monitor.X + _monitor.Width,
        Bottom = _monitor.Y + _monitor.Height,
    };

    /// <summary>Reserve the strip (maximized windows move below it).</summary>
    public void Reserve()
    {
        _wantReserved = true;
        if (_appBar == null) return; // done once the window exists
        _appBar.Register();
        ApplyPosition();
    }

    /// <summary>Give the space back to other windows.</summary>
    public void Release()
    {
        _wantReserved = false;
        try
        {
            _appBar?.Unregister();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to release the top strip", ex);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyPosition();
    }

    private void ApplyPosition()
    {
        if (_hwnd == IntPtr.Zero) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int height = (int)Math.Round(StripHeight * s);
        var rect = _appBar is { IsRegistered: true }
            ? _appBar.ReserveTop(MonitorRect, height)
            : new WindowApi.RECT { Left = _monitor.X, Top = _monitor.Y, Right = _monitor.X + _monitor.Width, Bottom = _monitor.Y + height };
        WindowApi.PlaceTopmost(_hwnd, rect.Left, rect.Top, rect.Width, rect.Height);
        ReservedBottom = rect.Bottom;
        if (rect.Top != ReservedTop)
        {
            ReservedTop = rect.Top;
            PositionChanged?.Invoke();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar == null) return IntPtr.Zero;

        // Explorer restarted: our reserved strip is gone, claim it again.
        if (msg == (int)AppBar.TaskbarCreatedMessage)
        {
            _appBar.ForgetRegistration();
            if (_wantReserved) Reserve();
            Log.Info("Explorer restarted; top strip reserved again");
            return IntPtr.Zero;
        }

        if (msg != (int)_appBar.CallbackMessage) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case AppBar.ABN_POSCHANGED:
                ApplyPosition();
                break;
            case AppBar.ABN_FULLSCREENAPP:
                FullscreenAppChanged?.Invoke();
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// The strip's fill while a maximized window is on screen: black in dark mode; in light mode it
    /// follows Windows (like the light taskbar), so the black island stands out the way a Mac notch does.
    /// </summary>
    public void SetLight(bool light)
    {
        _light = light;
        _fill.Background = light ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)) : Brushes.Black;
        RefreshBar();
    }

    // ---------------------------------------------------------------- the menu bar

    private static string ClockText()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        return $"{now.ToString("ddd d MMM", culture)}  {now.ToString("t", culture)}";
    }

    /// <summary>Rebuilds the bar from the live status and the user's settings (cheap: a handful of icons).</summary>
    private void RefreshBar()
    {
        var s = AppSettings.Current;
        var st = SystemStatus.Current;
        bool on = s.MenuBarEnabled;
        if (_hwnd != IntPtr.Zero) WindowApi.SetClickThrough(_hwnd, !on);

        var fg = new SolidColorBrush(_light ? Color.FromRgb(0x1B, 0x1B, 0x1D) : Colors.White);
        fg.Freeze();
        // Translucent, so the wallpaper shows through like a Mac menu bar; still readable on any wallpaper.
        _tint.Background = on ? new SolidColorBrush(_light ? Color.FromArgb(0xB8, 0xF3, 0xF3, 0xF5) : Color.FromArgb(0x8C, 0x14, 0x14, 0x18)) : Brushes.Transparent;

        _appName.Visibility = on && s.MenuBarAppName ? Visibility.Visible : Visibility.Collapsed;
        _appName.Foreground = fg;
        _appName.Text = st.ActiveApp;

        _items.Children.Clear();
        _clockText = ClockText();
        if (!on) return;

        if (s.MenuBarBluetooth && st.HasBluetooth)
            AddItem(StatusIcons.Bluetooth(fg, st.BluetoothOn), st.BluetoothOn ? "Bluetooth: on" : "Bluetooth: off", () => Open("ms-settings:bluetooth"));

        if (s.MenuBarNetwork)
        {
            switch (st.Network)
            {
                case NetworkKind.Wifi:
                    AddItem(StatusIcons.Wifi(fg, st.WifiLevel, offline: false),
                        $"Wi‑Fi: {st.NetworkName} · {(st.WifiLevel >= 3 ? "excellent" : st.WifiLevel == 2 ? "good" : "weak")} signal", () => Open("ms-availablenetworks:"));
                    break;
                case NetworkKind.Wired:
                case NetworkKind.Cellular:
                    AddItem(StatusIcons.Ethernet(fg), st.Network == NetworkKind.Wired ? $"Wired network: {st.NetworkName}" : $"Mobile network: {st.NetworkName}",
                        () => Open("ms-availablenetworks:"));
                    break;
                default:
                    AddItem(StatusIcons.Wifi(fg, 0, offline: true), "Not connected to the internet", () => Open("ms-availablenetworks:"));
                    break;
            }
        }

        if (s.MenuBarBattery && st.HasBattery)
        {
            var battery = new StackPanel { Orientation = Orientation.Horizontal };
            if (s.MenuBarBatteryPercent)
                battery.Children.Add(new TextBlock
                {
                    Text = $"{st.BatteryPercent}%",
                    FontSize = 12.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 1),
                });
            battery.Children.Add(StatusIcons.Battery(fg, st.BatteryPercent, st.Charging, st.PluggedIn));
            string tip = st.Charging ? $"Charging · {st.BatteryPercent}%"
                : st.PluggedIn ? $"Plugged in · {st.BatteryPercent}%"
                : st.TimeLeft is { } left ? $"On battery · {st.BatteryPercent}% · about {(int)left.TotalHours} h {left.Minutes} min left"
                : $"On battery · {st.BatteryPercent}%";
            AddItem(battery, tip, () => Open("ms-settings:batterysaver"));
        }

        if (s.MenuBarSearch) AddItem(StatusIcons.Search(fg), "Search", () => Keys(VK_LWIN, VK_S));
        if (s.MenuBarQuickSettings) AddItem(StatusIcons.QuickSettings(fg), "Quick settings", () => Keys(VK_LWIN, VK_A));
        if (s.MenuBarClock)
            AddItem(new TextBlock { Text = _clockText, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) },
                DateTime.Now.ToString("D", CultureInfo.CurrentCulture), () => Keys(VK_LWIN, VK_MENU, VK_D));
    }

    private void AddItem(FrameworkElement content, string tip, Action click)
    {
        content.VerticalAlignment = VerticalAlignment.Center;
        var hover = new SolidColorBrush(_light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        // Hover through a style trigger: it always follows the real mouse state (enter/leave events
        // can be missed on a window that never takes focus, leaving a highlight stuck).
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
        var hovered = new Trigger { Property = IsMouseOverProperty, Value = true };
        hovered.Setters.Add(new Setter(Border.BackgroundProperty, hover));
        style.Triggers.Add(hovered);
        var item = new Border
        {
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(5),
            Style = style,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tip,
            Child = content,
        };
        item.MouseLeftButtonUp += (_, _) => click();
        _items.Children.Add(item);
    }

    private static void Open(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error($"Couldn't open {uri}", ex); }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    private const byte VK_LWIN = 0x5B, VK_MENU = 0x12, VK_S = 0x53, VK_A = 0x41, VK_D = 0x44;

    /// <summary>Presses a Windows shortcut (Win+S search, Win+A quick settings, Win+Alt+D calendar).</summary>
    private static void Keys(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, IntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, 0x2 /* KEYUP */, IntPtr.Zero);
    }

    private void SetBlack(bool black)
    {
        if (_black == black) return;
        _black = black;
        _fill.BeginAnimation(OpacityProperty, new DoubleAnimation(black ? 1 : 0, TimeSpan.FromMilliseconds(180)));
    }
}
