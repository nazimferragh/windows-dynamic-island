using System;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <param name="IsMove">False when the user is resizing rather than moving the window.</param>
/// <param name="StartPlacement">Where the window was before the drag began (used to put it back).</param>
public readonly record struct WindowDrag(IntPtr Hwnd, int CursorX, int CursorY, bool IsMove, WindowApi.WINDOWPLACEMENT StartPlacement);

/// <summary>
/// Notices when any app's window is being dragged by its title bar and reports the cursor position
/// while it moves, so the islands can react like a black hole pulling it in.
/// </summary>
public sealed class WindowDragWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly WindowApi.WinEventProc _callback; // must stay referenced for the hook's lifetime
    private readonly IntPtr _hook;
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };

    private IntPtr _hwnd;
    private WindowApi.RECT _startRect;
    private WindowApi.WINDOWPLACEMENT _startPlacement;
    private bool _resized;

    public event Action<WindowDrag>? Moved;
    public event Action<WindowDrag>? Ended;

    public WindowDragWatcher()
    {
        _callback = OnWinEvent;
        _hook = WindowApi.SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _callback,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero) Log.Error("Could not watch window drags (SetWinEventHook failed)");
        _poll.Tick += (_, _) =>
        {
            if (_hwnd == IntPtr.Zero) return;
            // A size change *during* the drag means the user is resizing by an edge, not moving.
            if (SizeChanged()) _resized = true;
            Moved?.Invoke(Current());
        };
    }

    public void Dispose()
    {
        _poll.Stop();
        if (_hook != IntPtr.Zero) WindowApi.UnhookWinEvent(_hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0) return; // OBJID_WINDOW only

        if (eventType == EVENT_SYSTEM_MOVESIZESTART)
        {
            _hwnd = WindowApi.GetRootWindow(hwnd);
            WindowApi.GetWindowRect(_hwnd, out _startRect);
            _startPlacement = WindowApi.GetPlacement(_hwnd);
            _resized = false;
            _poll.Start();
        }
        else if (eventType == EVENT_SYSTEM_MOVESIZEEND && _hwnd != IntPtr.Zero)
        {
            _poll.Stop();
            // Don't look at the size now: on release Windows may already have snapped or maximized the
            // window (drag-to-top), which changes its size even though the user was moving it.
            var drag = Current();
            _hwnd = IntPtr.Zero;
            Ended?.Invoke(drag);
        }
    }

    private bool SizeChanged()
    {
        WindowApi.GetWindowRect(_hwnd, out var rect);
        return rect.Width != _startRect.Width || rect.Height != _startRect.Height;
    }

    private WindowDrag Current()
    {
        WindowApi.GetCursorPos(out var cursor);
        return new WindowDrag(_hwnd, cursor.X, cursor.Y, !_resized, _startPlacement);
    }
}
