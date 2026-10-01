using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

public sealed class AbsorbedWindow
{
    public required IntPtr Handle { get; init; }
    public required uint ProcessId { get; init; }
    public required string Title { get; init; }
    public BitmapSource? Snapshot { get; init; }
    public ImageSource? Icon { get; init; }
    public WindowApi.WINDOWPLACEMENT Placement { get; init; }

    /// <summary>The virtual desktop it was absorbed from (Guid.Empty if unknown).</summary>
    public Guid DesktopId { get; init; }
}

/// <summary>
/// The black hole: windows thrown into the island are hidden (off screen, taskbar and Alt+Tab)
/// and kept here until the user pulls them back out.
///
/// Safety net: a hidden window must never get lost. The list is saved to disk on every change,
/// every window is shown again when the app exits or crashes, and any leftovers from a previous
/// run are restored at startup.
/// </summary>
public sealed class WindowVault
{
    private static readonly string StatePath = Path.Combine(Log.LogDirectory, "vault.json");
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly List<AbsorbedWindow> _items = new();
    /// <summary>Taken out of the black hole, still hidden while the island animates them back out.</summary>
    private readonly List<AbsorbedWindow> _inFlight = new();
    private readonly DispatcherTimer _sweep = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _desktopPoll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Guid _currentDesktop = VirtualDesktops.GetCurrentDesktop();

    public WindowVault()
    {
        _sweep.Tick += (_, _) => Sweep();
        _sweep.Start();

        // Each virtual desktop has its own black hole: refresh the shelf when the user switches.
        _desktopPoll.Tick += (_, _) =>
        {
            var desktop = VirtualDesktops.GetCurrentDesktop();
            if (desktop == _currentDesktop) return;
            _currentDesktop = desktop;
            Changed?.Invoke();
        };
        _desktopPoll.Start();
    }

    /// <summary>Everything absorbed, on every desktop. Newest first.</summary>
    public IReadOnlyList<AbsorbedWindow> Items => _items;

    /// <summary>What the black hole shows right now: windows absorbed on the current virtual desktop.</summary>
    public IReadOnlyList<AbsorbedWindow> VisibleItems => _items.Where(IsOnCurrentDesktop).ToList();

    public event Action? Changed;

    private bool IsOnCurrentDesktop(AbsorbedWindow item) =>
        _currentDesktop == Guid.Empty || item.DesktopId == Guid.Empty || item.DesktopId == _currentDesktop;

    public static bool CanAbsorb(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !WindowApi.IsWindow(hwnd) || !WindowApi.IsWindowVisible(hwnd)) return false;
        if (WindowApi.GetRootWindow(hwnd) != hwnd || WindowApi.IsChildOrToolWindow(hwnd)) return false;
        if (WindowApi.GetProcessId(hwnd) == (uint)Environment.ProcessId) return false;
        return !ShellClasses.Contains(WindowApi.GetClassName(hwnd));
    }

    /// <summary>Captures what's needed to show the window in the island. Doesn't hide it yet.</summary>
    public AbsorbedWindow? Prepare(IntPtr hwnd, WindowApi.WINDOWPLACEMENT? restorePlacement)
    {
        if (!CanAbsorb(hwnd) || _items.Any(i => i.Handle == hwnd)) return null;
        uint pid = WindowApi.GetProcessId(hwnd);
        var exePath = WindowApi.GetProcessPath(pid);
        var title = WindowApi.GetTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title)) title = exePath != null ? Path.GetFileNameWithoutExtension(exePath) : "Window";

        return new AbsorbedWindow
        {
            Handle = hwnd,
            ProcessId = pid,
            Title = title,
            Snapshot = WindowApi.IsHung(hwnd) ? null : CaptureSnapshot(hwnd),
            Icon = LoadIcon(hwnd, exePath),
            Placement = restorePlacement ?? WindowApi.GetPlacement(hwnd),
            DesktopId = VirtualDesktops.GetWindowDesktop(hwnd),
        };
    }

    /// <summary>Hides the window and adds it. False if Windows wouldn't let us (e.g. an admin window).</summary>
    public bool Commit(AbsorbedWindow item)
    {
        WindowApi.ShowWindow(item.Handle, WindowApi.SW_HIDE);
        if (WindowApi.IsWindowVisible(item.Handle))
        {
            Log.Info($"Could not absorb '{item.Title}' (probably running as administrator)");
            return false;
        }
        _items.Insert(0, item);
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// First half of an animated restore: takes the window out of the black hole and moves it
    /// (still hidden) to where it will reappear, either its old place or with its title bar under
    /// a point. Returns where it will be on screen (physical pixels), or null if it's gone. Until
    /// <see cref="FinishRestore"/> it's still tracked, so a crash mid-animation can't lose it.
    /// </summary>
    public WindowApi.RECT? BeginRestore(AbsorbedWindow item, (int X, int Y)? at)
    {
        if (!_items.Remove(item)) return null;
        bool alive = WindowApi.IsWindow(item.Handle);
        if (alive) _inFlight.Add(item);
        Save();
        Changed?.Invoke();
        if (!alive) return null;

        if (at is { } p) WindowApi.MoveTitleBarTo(item.Handle, p.X, p.Y);
        else WindowApi.PlaceHidden(item.Handle, item.Placement);
        var rect = WindowApi.GetVisibleBounds(item.Handle);
        if (item.Placement.showCmd == WindowApi.SW_SHOWMAXIMIZED)
            rect = WindowApi.GetWorkArea(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        return rect;
    }

    /// <summary>Second half: shows the window where <see cref="BeginRestore"/> put it and focuses it.</summary>
    public void FinishRestore(AbsorbedWindow item, bool atPoint)
    {
        if (!_inFlight.Remove(item)) return;
        Save();
        if (!WindowApi.IsWindow(item.Handle)) return;
        bool maximized = item.Placement.showCmd == WindowApi.SW_SHOWMAXIMIZED;
        WindowApi.ShowWindow(item.Handle, maximized ? WindowApi.SW_SHOWMAXIMIZED : atPoint ? WindowApi.SW_SHOW : WindowApi.SW_SHOWNORMAL);
        WindowApi.Activate(item.Handle);
    }

    /// <summary>Shows every absorbed window again without stealing focus. Safe to call from a crash handler.</summary>
    public void RestoreAll()
    {
        _items.AddRange(_inFlight);
        _inFlight.Clear();
        foreach (var item in _items)
        {
            try
            {
                if (WindowApi.IsWindow(item.Handle)) WindowApi.ShowWindow(item.Handle, WindowApi.SW_SHOWNA);
            }
            catch
            {
                // Keep going: every other window still needs to come back.
            }
        }
        _items.Clear();
        Save();
    }

    /// <summary>Shows windows a previous run left hidden (e.g. after a crash or a forced kill).</summary>
    public static void RecoverOrphans()
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedWindow>>(File.ReadAllText(StatePath)) ?? new();
            int recovered = 0;
            foreach (var w in saved)
            {
                var hwnd = new IntPtr(w.Handle);
                if (WindowApi.IsWindow(hwnd) && WindowApi.GetProcessId(hwnd) == w.ProcessId && !WindowApi.IsWindowVisible(hwnd))
                {
                    WindowApi.ShowWindow(hwnd, WindowApi.SW_SHOWNA);
                    recovered++;
                }
            }
            if (recovered > 0) Log.Info($"Recovered {recovered} window(s) left hidden by a previous run");
            File.Delete(StatePath);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to recover hidden windows", ex);
        }
    }

    /// <summary>Drops windows that were closed, or that their app showed again by itself.</summary>
    private void Sweep()
    {
        int removed = _items.RemoveAll(i =>
            !WindowApi.IsWindow(i.Handle)
            || WindowApi.GetProcessId(i.Handle) != i.ProcessId
            || WindowApi.IsWindowVisible(i.Handle));
        if (removed == 0) return;
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.LogDirectory);
            if (_items.Count == 0 && _inFlight.Count == 0)
            {
                File.Delete(StatePath);
                return;
            }
            var saved = _items.Concat(_inFlight).Select(i => new SavedWindow(i.Handle.ToInt64(), i.ProcessId)).ToList();
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save vault state", ex);
        }
    }

    private static BitmapSource? CaptureSnapshot(IntPtr hwnd)
    {
        try
        {
            var capture = WindowApi.CaptureWindow(hwnd);
            if (capture == null) return null;
            var (pixels, w, h) = capture.Value;

            // Bgr32 ignores the alpha channel, which GDI leaves at 0 for many apps.
            BitmapSource bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
            const double maxWidth = 960;
            if (w > maxWidth)
            {
                double scale = maxWidth / w;
                bitmap = new WriteableBitmap(new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)));
            }
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("Window snapshot failed", ex);
            return null;
        }
    }

    private static ImageSource? LoadIcon(IntPtr hwnd, string? exePath)
    {
        try
        {
            var handle = WindowApi.GetWindowIcon(hwnd);
            if (handle != IntPtr.Zero)
            {
                var icon = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                return icon;
            }
            if (exePath != null && File.Exists(exePath))
            {
                using var fileIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (fileIcon == null) return null;
                var icon = Imaging.CreateBitmapSourceFromHIcon(fileIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                return icon;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Icon load failed", ex);
        }
        return null;
    }

    private sealed record SavedWindow(long Handle, uint ProcessId);
}
