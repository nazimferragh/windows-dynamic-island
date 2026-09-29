using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// Windows settings that get in the way of the island, adjusted automatically so users never have
/// to go hunting in Settings. Applied once per tweak version; a user who changes a setting back
/// afterwards is respected.
/// </summary>
public static class ShellTweaks
{
    /// <summary>Bump when a new tweak is added so existing installs apply it once.</summary>
    private const int TweaksVersion = 2;

    /// <summary>Windows 11 22H2 introduced the drag-to-top snap layouts bar.</summary>
    private const int SnapBarBuild = 22621;

    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string OurKey = @"Software\DynamicIsland";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Applies pending tweaks. May restart Explorer (a couple of seconds), so call it before creating the islands.</summary>
    public static void ApplyOnce()
    {
        try
        {
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            if (ours.GetValue("TweaksVersion") is int applied && applied >= TweaksVersion) return;

            bool restartExplorer = DisableSnapBarOnDragToTop();
            ours.SetValue("TweaksVersion", TweaksVersion, RegistryValueKind.DWord);
            if (restartExplorer) RestartExplorer();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to apply shell tweaks", ex);
        }
    }

    /// <summary>
    /// Windows 11 drops a "snap layouts" bar from the top-center of the screen when a window is dragged
    /// to the top: exactly where the island's black hole is. Snap layouts stay available from the
    /// maximize button and Win+Z. Explorer only reads this setting at startup.
    /// </summary>
    /// <returns>True if Explorer must restart for it to take effect.</returns>
    private static bool DisableSnapBarOnDragToTop()
    {
        if (Environment.OSVersion.Version.Build < SnapBarBuild) return false;
        using var advanced = Registry.CurrentUser.CreateSubKey(AdvancedKey);
        advanced.SetValue("EnableSnapBar", 0, RegistryValueKind.DWord);
        Log.Info("Turned off Windows' snap layouts bar on drag-to-top (it appears where the island is)");
        return true;
    }

    /// <summary>
    /// Restarts the Windows shell so it reloads its settings. The taskbar must always come back:
    /// every step checks for it and falls back to starting a fresh shell.
    /// </summary>
    private static void RestartExplorer()
    {
        const uint WM_EXIT_EXPLORER = 0x5B4; // WM_USER + 436: "Exit Explorer" from the taskbar's Ctrl+Shift menu
        if (!TaskbarExists()) return; // no shell running (unusual); nothing to reload

        Log.Info("Restarting Explorer so it picks up the new settings");

        // 1) Ask the shell to exit cleanly so it saves its state.
        PostMessage(FindWindow("Shell_TrayWnd", null), WM_EXIT_EXPLORER, IntPtr.Zero, IntPtr.Zero);
        WaitFor(() => !TaskbarExists(), TimeSpan.FromSeconds(5));

        // 2) Explorer processes can survive that (they host File Explorer windows). While one is alive,
        //    launching explorer.exe just opens a folder in it instead of starting the taskbar, so end them.
        EndExplorerProcesses();

        // 3) Windows usually restarts the shell by itself; otherwise start it.
        if (WaitFor(TaskbarExists, TimeSpan.FromSeconds(4))) return;
        StartShell();
        if (WaitFor(TaskbarExists, TimeSpan.FromSeconds(15))) return;

        // 4) Last resort: one more clean start.
        Log.Error("Taskbar did not come back; starting Explorer again");
        EndExplorerProcesses();
        StartShell();
        if (!WaitFor(TaskbarExists, TimeSpan.FromSeconds(15))) Log.Error("Explorer did not come back after restart");
    }

    private static bool TaskbarExists() => FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

    private static void EndExplorerProcesses()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (p.SessionId != session) continue;
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Log.Error("Could not end an Explorer process", ex);
            }
        }
    }

    private static void StartShell() =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = true,
        });

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }
}
