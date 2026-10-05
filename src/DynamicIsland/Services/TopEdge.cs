using System;
using System.Windows;
using System.Windows.Interop;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// The band at the top of a monitor the island lives in, reserved like the Mac menu bar: Windows lays
/// maximized and snapped windows out below it, so nothing ever sits under the island. Nothing is
/// drawn there (the owner rejected a black strip): the wallpaper shows on both sides of the notch.
/// Full-screen games and videos ignore the reservation; the island floats over them.
/// Hangs below a taskbar docked at the top (Windows 10).
/// </summary>
internal sealed class TopEdge : IDisposable
{
    private readonly Int32Rect _monitor;
    private HwndSource? _host;
    private AppBar? _appBar;
    private int _height;
    private bool _reserved;

    public TopEdge(Int32Rect monitor)
    {
        _monitor = monitor;
        Top = Measure();
    }

    /// <summary>Top of the island in physical pixels.</summary>
    public int Top { get; private set; }

    /// <summary>Bottom of the reserved band in physical pixels (where apps start); equals Top when nothing is reserved.</summary>
    public int Bottom { get; private set; }

    /// <summary>Raised when the top moved (e.g. the taskbar was docked to the top or moved away).</summary>
    public event Action? Changed;

    /// <summary>Raised when Windows reports a full-screen app opening or closing on this monitor.</summary>
    public event Action? FullscreenAppChanged;

    private WindowApi.RECT MonitorRect => new()
    {
        Left = _monitor.X,
        Top = _monitor.Y,
        Right = _monitor.X + _monitor.Width,
        Bottom = _monitor.Y + _monitor.Height,
    };

    /// <summary>Reserves <paramref name="heightPx"/> physical pixels (the closed island) at the top.</summary>
    public void Reserve(int heightPx)
    {
        _height = heightPx;
        _reserved = true;
        try
        {
            EnsureHost();
            _appBar!.Register();
            Apply();
        }
        catch (Exception ex)
        {
            Log.Error("Could not reserve the top of the screen", ex);
        }
    }

    /// <summary>Gives the space back to other windows (island hidden, quit, crash).</summary>
    public void Release()
    {
        _reserved = false;
        try
        {
            _appBar?.Unregister();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to release the top of the screen", ex);
        }
        Refresh();
    }

    public void Refresh()
    {
        if (_reserved && _appBar is { IsRegistered: true }) return; // Apply() keeps Top up to date
        int top = Measure();
        Bottom = top;
        SetTop(top);
    }

    private void EnsureHost()
    {
        if (_host != null) return;
        // An invisible tool window: app bars need a top-level window to talk to Explorer; it's never shown.
        var p = new HwndSourceParameters("DynamicIsland.TopEdge")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x00000080 | 0x08000000, // TOOLWINDOW | NOACTIVATE
            PositionX = _monitor.X,
            PositionY = _monitor.Y,
            Width = 1,
            Height = 1,
        };
        _host = new HwndSource(p);
        _host.AddHook(WndProc);
        _appBar = new AppBar(_host.Handle);
    }

    private void Apply()
    {
        if (_appBar is not { IsRegistered: true } || _height <= 0) return;
        var rect = _appBar.ReserveTop(MonitorRect, _height);
        bool moved = rect.Bottom != Bottom;
        Bottom = rect.Bottom;
        if (rect.Top != Top) SetTop(rect.Top);
        else if (moved) Changed?.Invoke();
    }

    private void SetTop(int top)
    {
        if (top == Top) return;
        Top = top;
        Changed?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar == null) return IntPtr.Zero;

        // Explorer restarted: every app bar registration died with it; claim the space again.
        if (msg == (int)AppBar.TaskbarCreatedMessage)
        {
            _appBar.ForgetRegistration();
            if (_reserved) Reserve(_height);
            Log.Info("Explorer restarted; top of the screen reserved again");
            return IntPtr.Zero;
        }

        if (msg != (int)_appBar.CallbackMessage) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case AppBar.ABN_POSCHANGED:
                Apply();
                break;
            case AppBar.ABN_FULLSCREENAPP:
                FullscreenAppChanged?.Invoke();
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Without a reservation: the monitor top, or just below a top-docked taskbar.</summary>
    private int Measure()
    {
        var work = WindowApi.GetWorkArea(_monitor.X + _monitor.Width / 2, _monitor.Y + _monitor.Height / 2);
        return work.Top > _monitor.Y && work.Top < _monitor.Y + _monitor.Height / 4 ? work.Top : _monitor.Y;
    }

    public void Dispose()
    {
        Release();
        _host?.Dispose();
        _host = null;
    }
}
