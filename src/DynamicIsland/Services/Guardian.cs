using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// Keeps the island running like part of Windows: it starts with the user's session, and a small
/// companion process (the same exe with <see cref="WatchdogArg"/>) brings it back whenever it dies,
/// whether it crashed or was ended from Task Manager. The island in turn restarts the companion,
/// so ending one of the two is undone within seconds. A frozen island is detected and restarted too.
/// Behind both, the start-with-Windows task (<see cref="AutoStartTask"/>) checks every minute, so
/// even killing both at once is undone. Only "Quit" from the tray (or signing out) stops them, by
/// setting a shared quit event first; installers ask through <see cref="RequestQuit"/>.
/// </summary>
public static class Guardian
{
    public const string WatchdogArg = "--watchdog";
    public const string AfterCrashArg = "--after-crash";

    private const string WatchdogMutexName = @"Local\DynamicIsland.Watchdog";
    private const string QuitEventName = @"Local\DynamicIsland.Quit";
    private const string OurKey = @"Software\DynamicIsland";
    public const string QuitArg = "--quit";

    /// <summary>The island's process name.</summary>
    public const string IslandProcessName = "DynamicIsland";

    /// <summary>
    /// The watchdog runs under its own name (a hard link to the same exe, so no extra disk space).
    /// Killing everything called DynamicIsland.exe, or "End process tree" on the island, then leaves
    /// the watchdog alive to bring the island back within seconds.
    /// </summary>
    public const string GuardProcessName = "DynamicIslandGuard";

    private static string InstallDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    private static string IslandExe => Path.Combine(InstallDir, IslandProcessName + ".exe");
    private static string GuardExe => Path.Combine(InstallDir, GuardProcessName + ".exe");

    /// <summary>Stop reviving the island if it dies this many times within <see cref="CrashWindow"/>.</summary>
    private const int MaxRestarts = 10;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(2);

    private static EventWaitHandle? _quit;
    private static DateTime _lastWatchdogSpawn = DateTime.MinValue;

    private static EventWaitHandle QuitEvent =>
        _quit ??= new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);

    private static bool QuitSignaled()
    {
        try { return QuitEvent.WaitOne(0); }
        catch { return false; }
    }

    // ---------------------------------------------------------------- island side

    /// <summary>Called once the island owns its single-instance mutex.</summary>
    public static void OnIslandStarted()
    {
        try { QuitEvent.Reset(); } catch { }
        ClearQuitRequest();
        // schtasks takes a moment; don't hold up the island appearing.
        Task.Run(AutoStartTask.EnsureRegistered);
        EnsureWatchdog();
    }

    /// <summary>Tells the companion this exit is intended, so it doesn't bring the island back.</summary>
    public static void SignalQuit()
    {
        try { QuitEvent.Set(); } catch { }
    }

    /// <summary>Starts the companion if it isn't running. Cheap enough to call on a timer.</summary>
    public static void EnsureWatchdog()
    {
        try
        {
            if (QuitSignaled()) return;
            try
            {
                if (Mutex.TryOpenExisting(WatchdogMutexName, out var existing))
                {
                    existing.Dispose();
                    return;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return; // it exists, created by a process with higher rights
            }
            // A companion we just launched may still be starting up.
            if (DateTime.Now - _lastWatchdogSpawn < TimeSpan.FromSeconds(15)) return;
            _lastWatchdogSpawn = DateTime.Now;
            StartDetached(PrepareGuardExe(), WatchdogArg);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start the watchdog", ex);
        }
    }

    // ---------------------------------------------------------------- quit requests (installer/uninstaller)

    /// <summary>
    /// Asks a running island to quit for good (used before updating/uninstalling). Goes through the
    /// registry because the installer may not have the rights to touch a high-priority island directly.
    /// </summary>
    public static void RequestQuit()
    {
        AutoStartTask.RememberQuit();
        try
        {
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            ours.SetValue("QuitRequested", 1, RegistryValueKind.DWord);
        }
        catch { }
        SignalQuit();
    }

    public static bool QuitRequested()
    {
        try
        {
            using var ours = Registry.CurrentUser.OpenSubKey(OurKey);
            return ours?.GetValue("QuitRequested") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    public static void ClearQuitRequest()
    {
        try
        {
            using var ours = Registry.CurrentUser.OpenSubKey(OurKey, writable: true);
            ours?.DeleteValue("QuitRequested", throwOnMissingValue: false);
        }
        catch { }
    }

    // ---------------------------------------------------------------- companion side

    /// <summary>The companion's whole life: watch the island and relaunch it until told to quit.</summary>
    public static void RunWatchdog()
    {
        using var mutex = new Mutex(false, WatchdogMutexName);
        try
        {
            if (!mutex.WaitOne(0)) return; // one companion is enough
        }
        catch (AbandonedMutexException)
        {
            // The previous companion was killed; we take over.
        }

        try
        {
            Log.Info("Watchdog started");
            var restarts = new Queue<DateTime>();
            while (!QuitSignaled() && !QuitRequested())
            {
                var island = FindIsland();
                if (island != null)
                {
                    // Wake up on quit too, so a tray Quit ends the companion right away. Meanwhile make
                    // sure it's still responsive: a frozen island is ended so it can be brought back.
                    int hungChecks = 0, tick = 0;
                    while (!island.WaitForExit(1000))
                    {
                        if (QuitSignaled() || QuitRequested()) return;
                        if (++tick % 5 != 0) continue;
                        hungChecks = IsHung(island.Id) ? hungChecks + 1 : 0;
                        if (hungChecks >= 3) // ~20 s without responding
                        {
                            Log.Info("The island stopped responding; the watchdog is restarting it");
                            try { island.Kill(); } catch { }
                        }
                    }
                    island.Dispose();
                }

                // Short grace period: on sign-out, or the island's own crash restart, the situation
                // settles (or we get told to quit) before we act.
                if (WaitForQuit(TimeSpan.FromSeconds(1))) return;
                if (FindIsland() is { } back)
                {
                    back.Dispose();
                    continue;
                }

                while (restarts.Count > 0 && DateTime.Now - restarts.Peek() > CrashWindow) restarts.Dequeue();
                if (restarts.Count >= MaxRestarts)
                {
                    Log.Info("The island keeps dying; the watchdog is giving up so it doesn't loop forever");
                    return;
                }
                restarts.Enqueue(DateTime.Now);
                Log.Info("The island is gone; the watchdog is bringing it back");
                // Directly, not through the task: Task Scheduler silently ignores "run" while it still
                // thinks the task is running. The watchdog was started by the island, so the new
                // island gets the same rights (and sets its own priority).
                StartDetached(IslandExe, AfterCrashArg);
                // Give it time to start and take its mutex before looking again.
                if (WaitForQuit(TimeSpan.FromSeconds(4))) return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Watchdog failed", ex);
        }
        finally
        {
            Log.Info("Watchdog stopped");
            mutex.ReleaseMutex();
        }
    }

    private static bool WaitForQuit(TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            if (QuitSignaled() || QuitRequested()) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>True if the process has a visible window and it doesn't answer within 2 seconds.</summary>
    private static bool IsHung(int pid)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid && IsWindowVisible(hwnd)) windows.Add(hwnd);
            return windows.Count == 0; // one is enough: all its windows share one UI thread
        }, IntPtr.Zero);
        if (windows.Count == 0) return false; // hidden (fullscreen app, user hid it): nothing to judge
        const uint SMTO_ABORTIFHUNG = 0x2;
        return SendMessageTimeout(windows[0], 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out _) == IntPtr.Zero;
    }

    /// <summary>The running island, if any.</summary>
    private static Process? FindIsland()
    {
        using var self = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(IslandProcessName).Where(p => p.Id != self.Id).ToList();
        var island = others.FirstOrDefault();
        foreach (var p in others.Skip(1)) p.Dispose();
        return island;
    }

    /// <summary>
    /// (Re)creates DynamicIslandGuard.exe next to the island as a hard link to the island's exe, so
    /// it always matches the installed version. Falls back to the island's own exe if that's not
    /// possible (the watchdog then simply shares its name, as before).
    /// </summary>
    private static string PrepareGuardExe()
    {
        try
        {
            if (Process.GetProcessesByName(GuardProcessName).Length == 0)
            {
                if (File.Exists(GuardExe)) File.Delete(GuardExe);
                if (!CreateHardLink(GuardExe, IslandExe, IntPtr.Zero)) File.Copy(IslandExe, GuardExe, overwrite: true);
            }
            return File.Exists(GuardExe) ? GuardExe : IslandExe;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't prepare the watchdog exe; using the island's", ex);
            return IslandExe;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>
    /// Launches this exe through a throwaway cmd, so the new process has no living parent. That keeps
    /// the island and the companion from being grouped together, where Task Manager's
    /// "End task" would kill both at once.
    /// </summary>
    private static void StartDetached(string path, string arg)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c start \"\" \"{path}\" {arg}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }
}
