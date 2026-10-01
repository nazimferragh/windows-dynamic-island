using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>
/// The equivalent of the Mac menu bar behind the notch. It reserves a strip at the top of the monitor
/// so maximized apps sit below the island instead of under it, and turns solid black while a maximized
/// window is on screen, so the notch blends into it the way it does on a Mac.
/// </summary>
internal sealed class MenuBarStrip : Window
{
    /// <summary>Same as the closed notch, so the notch merges seamlessly into the bar.</summary>
    public const double StripHeight = 32;

    private readonly Int32Rect _monitor;
    private readonly Border _fill = new() { Background = Brushes.Black, Opacity = 0 };
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
        Content = _fill;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
            _appBar = new AppBar(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            if (_wantReserved) Reserve();
        };
        _poll.Tick += (_, _) => SetBlack(WindowApi.HasMaximizedWindowOn(MonitorRect));
        _poll.Start();
        Closed += (_, _) =>
        {
            _poll.Stop();
            Release();
        };
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
    public void SetLight(bool light) =>
        _fill.Background = light ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)) : Brushes.Black;

    private void SetBlack(bool black)
    {
        if (_black == black) return;
        _black = black;
        _fill.BeginAnimation(OpacityProperty, new DoubleAnimation(black ? 1 : 0, TimeSpan.FromMilliseconds(180)));
    }
}
