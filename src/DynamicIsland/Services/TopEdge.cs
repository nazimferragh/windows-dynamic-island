using System;
using System.Windows;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Where the island hangs from: the monitor's top edge, or just below a taskbar docked at the top
/// (Windows 10). Nothing is reserved: like on an iPhone, apps use the whole screen (maximized ones
/// too) and the island floats on top of them, always visible.
/// </summary>
internal sealed class TopEdge
{
    private readonly Int32Rect _monitor;

    public TopEdge(Int32Rect monitor)
    {
        _monitor = monitor;
        Top = Measure();
    }

    /// <summary>Top of the island in physical pixels.</summary>
    public int Top { get; private set; }

    /// <summary>Raised when the top moved (e.g. the taskbar was docked to the top or moved away).</summary>
    public event Action? Changed;

    public void Refresh()
    {
        int top = Measure();
        if (top == Top) return;
        Top = top;
        Changed?.Invoke();
    }

    private int Measure()
    {
        var work = WindowApi.GetWorkArea(_monitor.X + _monitor.Width / 2, _monitor.Y + _monitor.Height / 2);
        return work.Top > _monitor.Y && work.Top < _monitor.Y + _monitor.Height / 4 ? work.Top : _monitor.Y;
    }
}
