using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>
/// Notices when a full-screen game or video is in front (exclusive or borderless) and puts the
/// island in game mode: animations stop, background checks slow right down, it stops re-asserting
/// "always on top", and the process drops below normal priority so the game always comes first.
/// Only the island on the game's own screen hides. Works on Windows 10 and 11.
/// </summary>
public static class GameMode
{
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rc rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)] private struct Rc { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rc Monitor, Work; public uint Flags; }

    private static readonly DispatcherTimer Poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private static readonly List<(DispatcherTimer Timer, TimeSpan Normal, TimeSpan Gaming)> Tuned = new();
    private static ProcessPriorityClass? _priorityBefore;
    private static bool _started;

    /// <summary>A full-screen game or video is in front.</summary>
    public static bool Active { get; private set; }

    /// <summary>The screen (physical pixels) it's on.</summary>
    public static Int32Rect? Screen { get; private set; }

    public static event Action? Changed;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        Poll.Tick += (_, _) => Check();
        Poll.Start();
        Check();
    }

    /// <summary>True when the full-screen app is on this screen (its island should step aside).</summary>
    public static bool IsOn(Int32Rect monitor) => Screen is { } s && s.X == monitor.X && s.Y == monitor.Y;

    /// <summary>Runs a background check at its normal pace, or much slower while a game is in front.</summary>
    public static void Tune(DispatcherTimer timer, TimeSpan normal, TimeSpan gaming)
    {
        Tuned.Add((timer, normal, gaming));
        timer.Interval = Active ? gaming : normal;
    }

    private static void Check()
    {
        Int32Rect? screen = null;
        try { screen = FullscreenScreen(); }
        catch (Exception ex) { Log.Error("Full-screen check failed", ex); }

        bool active = screen != null;
        bool same = active == Active && (!active || (Screen is { } s && s.X == screen!.Value.X && s.Y == screen.Value.Y));
        if (same) return;
        Active = active;
        Screen = screen;

        foreach (var (timer, normal, gaming) in Tuned) timer.Interval = active ? gaming : normal;
        SetPriority(active);
        Log.Info(active ? "Game mode on: a full-screen app is in front" : "Game mode off");
        Changed?.Invoke();
    }

    private static Int32Rect? FullscreenScreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return null;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* nearest */), ref info)) return null;
        var m = info.Monitor;
        var screen = new Int32Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);

        // Exclusive full screen (most games) or presentation mode: Windows says so directly.
        if (SHQueryUserNotificationState(out int state) == 0 && state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return screen;
        // Borderless full screen (many games, videos): the front window covers its whole screen.
        if (IsZoomed(hwnd) || !GetWindowRect(hwnd, out var r)) return null;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom ? screen : null;
    }

    /// <summary>Below normal while a game runs, so it always gets the CPU first; back to what it was after.</summary>
    private static void SetPriority(bool gaming)
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            if (gaming)
            {
                _priorityBefore ??= me.PriorityClass;
                me.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            else if (_priorityBefore is { } before)
            {
                me.PriorityClass = before;
                _priorityBefore = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't change the island's priority", ex);
        }
    }
}
