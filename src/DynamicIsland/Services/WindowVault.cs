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
    private readonly DispatcherTimer _sweep = new() { Interval = TimeSpan.FromSeconds(2) };

    public WindowVault()
    {
        _sweep.Tick += (_, _) => Sweep();
        _sweep.Start();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<AbsorbedWindow> Items => _items;

    public event Action? Changed;

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

    /// <summary>Puts the window back exactly where it was before it was absorbed.</summary>
    public void Restore(AbsorbedWindow item)
    {
        if (!Remove(item) || !WindowApi.IsWindow(item.Handle)) return;
        WindowApi.RestorePlacement(item.Handle, item.Placement);
        WindowApi.Activate(item.Handle);
    }

    /// <summary>Brings the window back with its title bar under the given point (physical pixels).</summary>
    public void RestoreAt(AbsorbedWindow item, int x, int y)
    {
        if (!Remove(item) || !WindowApi.IsWindow(item.Handle)) return;
        if (item.Placement.showCmd == WindowApi.SW_SHOWMAXIMIZED)
        {
            WindowApi.MoveTitleBarTo(item.Handle, x, y);
            WindowApi.ShowWindow(item.Handle, WindowApi.SW_SHOWMAXIMIZED);
        }
        else
        {
            WindowApi.MoveTitleBarTo(item.Handle, x, y);
            WindowApi.ShowWindow(item.Handle, WindowApi.SW_SHOW);
        }
        WindowApi.Activate(item.Handle);
    }

    /// <summary>Shows every absorbed window again without stealing focus. Safe to call from a crash handler.</summary>
    public void RestoreAll()
    {
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

    private bool Remove(AbsorbedWindow item)
    {
        if (!_items.Remove(item)) return false;
        Save();
        Changed?.Invoke();
        return true;
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
            if (_items.Count == 0)
            {
                File.Delete(StatePath);
                return;
            }
            var saved = _items.Select(i => new SavedWindow(i.Handle.ToInt64(), i.ProcessId)).ToList();
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
