using System;
using System.Runtime.InteropServices;
using System.Threading;
using DynamicIsland.Services;

namespace DynamicIsland.Interop;

/// <summary>
/// Keeps the mouse below a given line on one monitor. ClipCursor can't do this during a window drag
/// (Windows' move loop resets the clip), so a low-level mouse hook stops the pointer at the line
/// instead. The hook lives on its own thread so a busy UI can never make the mouse stutter, and it
/// does nothing unless a fence is up.
/// </summary>
public static class CursorFence
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    private static readonly HookProc Proc = OnMouse; // kept alive for the hook's lifetime
    private static Thread? _thread;
    private static IntPtr _hook;

    // The fence: [left, right) horizontally, nothing above minY. Read on the hook thread.
    private static volatile bool _active;
    private static int _left, _right, _minY;

    /// <summary>Stops the pointer from going above <paramref name="minY"/> between left and right (physical pixels).</summary>
    public static void Raise(int left, int right, int minY)
    {
        EnsureHook();
        Interlocked.Exchange(ref _left, left);
        Interlocked.Exchange(ref _right, right);
        Interlocked.Exchange(ref _minY, minY);
        _active = true;
    }

    public static void Lower() => _active = false;

    // Side walls: soft stops at a monitor's left/right edge where another screen continues, so a
    // dragged window can snap to that edge. Pushing on past the wall (PushThrough of attempted
    // travel) lets the pointer through onto the other screen.
    private const int PushThrough = 260;
    private static volatile bool _walls;
    private static int _wallLeft = int.MinValue, _wallRight = int.MaxValue, _wallTop, _wallBottom;
    private static int _push;

    /// <summary>
    /// Holds the pointer at <paramref name="left"/> and/or <paramref name="right"/> (the monitor's
    /// outer pixel columns; null = no wall on that side) between top and bottom, physical pixels.
    /// Re-arming with the same walls keeps the current push.
    /// </summary>
    public static void RaiseWalls(int? left, int? right, int top, int bottom)
    {
        EnsureHook();
        int l = left ?? int.MinValue, r = right ?? int.MaxValue;
        if (_walls && l == _wallLeft && r == _wallRight && top == _wallTop && bottom == _wallBottom) return;
        Interlocked.Exchange(ref _wallLeft, l);
        Interlocked.Exchange(ref _wallRight, r);
        Interlocked.Exchange(ref _wallTop, top);
        Interlocked.Exchange(ref _wallBottom, bottom);
        Interlocked.Exchange(ref _push, 0);
        _walls = left != null || right != null;
    }

    public static void LowerWalls() => _walls = false;

    /// <summary>Installs the (idle) hook up front so the very first drag is already covered.</summary>
    public static void Start() => EnsureHook();

    private static void EnsureHook()
    {
        if (_thread != null) return;
        _thread = new Thread(() =>
        {
            _hook = SetWindowsHookEx(WH_MOUSE_LL, Proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log.Error($"Mouse hook failed ({Marshal.GetLastWin32Error()}); Windows' maximize preview may show over the island");
                return;
            }
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        })
        { IsBackground = true, Name = "CursorFence", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _walls && wParam == WM_MOUSEMOVE)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (info.Y >= _wallTop && info.Y < _wallBottom)
            {
                int over = info.X < _wallLeft ? _wallLeft - info.X : info.X > _wallRight ? info.X - _wallRight : 0;
                if (over > 0)
                {
                    int push = Interlocked.Add(ref _push, over);
                    if (push < PushThrough)
                    {
                        SetCursorPos(info.X < _wallLeft ? _wallLeft : _wallRight, info.Y);
                        return (IntPtr)1;
                    }
                    _walls = false; // pushed through: off to the other screen
                }
                else if (info.X > _wallLeft + 24 && info.X < _wallRight - 24)
                {
                    Interlocked.Exchange(ref _push, 0); // backed away from the wall
                }
            }
        }
        if (code >= 0 && _active && wParam == WM_MOUSEMOVE)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (info.X >= _left && info.X < _right && info.Y < _minY)
            {
                // Swallow the move and put the pointer where it's allowed to be instead.
                SetCursorPos(info.X, _minY);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
