using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynamicIsland;

public partial class App : Application
{
    private const string MutexName = @"Local\DynamicIsland.SingleInstance";
    private const string AfterCrashArg = Guardian.AfterCrashArg;

    private readonly List<IslandWindow> _islands = new();
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly DispatcherTimer _displayChangeDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _watchdogCheck = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _quitRequestCheck = new() { Interval = TimeSpan.FromSeconds(1) };

    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _afterCrash;
    private const int AbsorbHotkeyId = 0xB1;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, VK_Z = 0x5A;
    private const int WM_HOTKEY = 0x0312;

    private Forms.NotifyIcon? _tray;
    private MediaService? _media;
    private WindowVault? _vault;
    private WindowDragWatcher? _dragWatcher;
    private DownloadWatcher? _downloads;
    private NotificationService? _notifications;
    private PinnedApps? _pins;
    private HwndSource? _hotkeySink;
    private bool _hidden;

    public static string VersionText
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Run by the uninstaller: give Windows its notification pop-ups back.
        if (e.Args.Contains(NotificationBanners.RestoreArg))
        {
            NotificationBanners.RestoreAll();
            Shutdown();
            return;
        }

        // Opens a panel of the running island: DynamicIsland.exe --panel wifi|bluetooth|apps|player|close|restore
        int panelArg = Array.IndexOf(e.Args, "--panel");
        if (panelArg >= 0 && panelArg + 1 < e.Args.Length)
        {
            Guardian.RequestPanel(e.Args[panelArg + 1]);
            Shutdown();
            return;
        }

        // Support/diagnostics: logs what the Wi‑Fi and Bluetooth panels would show, then exits.
        if (e.Args.Contains("--diag"))
        {
            await RunDiagnosticsAsync();
            Shutdown();
            return;
        }

        // The companion process that brings the island back if it dies. It has no UI of its own.
        if (e.Args.Contains(Guardian.WatchdogArg))
        {
            Guardian.RunWatchdog();
            Shutdown();
            return;
        }

        // Run by the installer: set up start-with-Windows ("high" asks Windows for admin rights once;
        // if that's declined it falls back to a normal task, which still keeps the island running).
        int register = Array.IndexOf(e.Args, AutoStartTask.RegisterArg);
        if (register >= 0)
        {
            string mode = register + 1 < e.Args.Length ? e.Args[register + 1] : "auto";
            if (mode == "auto")
            {
                // Silent installs/updates: keep what the user chose before, without any prompt.
                if (AutoStartTask.Query() is not { High: true }) AutoStartTask.Register(high: false);
            }
            else
            {
                bool high = mode == "high";
                if (!AutoStartTask.Register(high) && high) AutoStartTask.Register(high: false);
            }
            Shutdown();
            return;
        }
        if (e.Args.Contains(AutoStartTask.UnregisterArg))
        {
            AutoStartTask.Unregister();
            Shutdown();
            return;
        }

        // Run by the installer/uninstaller: stop the running island (and keep it stopped) so its files can be replaced.
        if (e.Args.Contains(Guardian.QuitArg))
        {
            Guardian.RequestQuit();
            WaitForOtherInstancesToExit(TimeSpan.FromSeconds(10));
            Shutdown();
            return;
        }

        bool autostart = e.Args.Contains(AutoStartTask.AutostartArg);
        bool startupEntry = e.Args.Contains(AutoStartTask.StartupEntryArg);
        bool restart = e.Args.Contains(RestartArg);
        _afterCrash = e.Args.Contains(AfterCrashArg);
        if ((autostart || startupEntry) && (AutoStartTask.QuitThisBoot() || AutoStartTask.DisabledInWindowsStartup()))
        {
            // An automatic start, but the user quit the island this session, or turned it off in
            // Windows' Startup apps: respect that.
            Shutdown();
            return;
        }
        if (startupEntry)
        {
            // Windows' Startup apps entry: hand off to the task (it starts the island with the rights
            // the user chose). Nothing to do if the island is already up.
            if (IslandRunning() || (AutoStartTask.Query() != null && AutoStartTask.RunNow()))
            {
                Shutdown();
                return;
            }
            autostart = true; // no task: start it here
        }
        if (!autostart && !_afterCrash) AutoStartTask.ForgetQuit(); // opened by hand: it's wanted again

        // After a crash the dying instance may still hold the mutex for a moment, so wait for it.
        try
        {
            _mutex = new Mutex(false, MutexName);
            // After a crash or a restart from Settings, the previous instance may still be exiting.
            _ownsMutex = _mutex.WaitOne(_afterCrash || restart ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        catch (UnauthorizedAccessException)
        {
            _ownsMutex = false; // held by a high-priority (elevated) island: it's already running
        }
        if (!_ownsMutex)
        {
            // Opened by hand while it's already running: show its Settings, the way launching a
            // running app brings up its window. (Through the registry: the running island may have
            // higher rights than this launch.)
            if (!autostart && !_afterCrash && !restart)
            {
                Guardian.RequestOpenSettings();
                AllowSetForegroundWindow(-1); // let the running island bring its window to the front
            }
            Shutdown();
            return;
        }

        // Opened by hand (Start menu, installer) while high priority is set up: start through the
        // task instead, so the island runs with the rights and priority the user granted.
        if (!autostart && !AutoStartTask.IsElevated && HighPriorityChosen())
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
            if (AutoStartTask.RunNow())
            {
                Shutdown();
                return;
            }
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero); // task unavailable: just run normally
        }
        if (HighPriorityChosen())
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        Log.Info($"Starting Dynamic Island {VersionText}{(_afterCrash ? " (restarted after crash)" : "")}");
        Guardian.OnIslandStarted();
        _watchdogCheck.Tick += (_, _) => Guardian.EnsureWatchdog();
        _watchdogCheck.Start();
        // An installer asking the island to stop (it may lack the rights to end a high-priority one).
        _quitRequestCheck.Tick += (_, _) =>
        {
            if (Guardian.TakeOpenSettingsRequest()) OpenSettings();
            if (Guardian.TakePanelRequest() is { } panel)
            {
                var primary = Forms.Screen.PrimaryScreen!.Bounds;
                var target = _islands.FirstOrDefault(i => i.Monitor.X == primary.X && i.Monitor.Y == primary.Y) ?? _islands.FirstOrDefault();
                // "name@N": ask the island on the Nth screen instead (tests of multi-screen behavior).
                int at = panel.LastIndexOf('@');
                if (at > 0 && int.TryParse(panel.AsSpan(at + 1), out int screen) && screen >= 0 && screen < _islands.Count)
                {
                    target = _islands[screen];
                    panel = panel[..at];
                }
                target?.ShowPanel(panel);
            }
            if (!Guardian.QuitRequested()) return;
            Log.Info("Quit requested by the installer");
            Guardian.SignalQuit();
            Shutdown();
        };
        _quitRequestCheck.Start();

        // Any windows a previous run left hidden (crash, forced kill) come back first.
        WindowVault.RecoverOrphans();
        // Settings that would get in the island's way (may restart Explorer once, right after install).
        await System.Threading.Tasks.Task.Run(ShellTweaks.ApplyOnce);
        _vault = new WindowVault();
        _dragWatcher = new WindowDragWatcher();
        _downloads = new DownloadWatcher();
        _notifications = new NotificationService();
        _pins = new PinnedApps();
        _ = WindowsTheme.Current; // start listening for accent/light-dark changes
        AppSettings.Changed += OnSettingsChanged;
        _lastAllMonitors = AppSettings.Current.ShowOnAllMonitors;
        EdgeSnapping.Apply(AppSettings.Current.SnapLayoutsEnabled);
        GameMode.Start();
        SessionEnding += (_, _) =>
        {
            Guardian.SignalQuit(); // signing out or shutting down isn't a crash
            _vault.RestoreAll();
        };

        _media = new MediaService();
        CreateIslands();
        CreateTrayIcon();
        RegisterAbsorbHotkey();
        CursorFence.Start();

        _displayChangeDebounce.Tick += (_, _) =>
        {
            _displayChangeDebounce.Stop();
            CreateIslands();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        await _media.InitializeAsync();
        await _notifications.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdogCheck.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Overlays.MediaBrowserWindow.ShutDown();
        _vault?.RestoreAll();
        foreach (var island in _islands) island.ReleaseReservedSpace();
        // Only the copy that runs the island gives Windows' snapping back: the extra launches
        // (the every-minute safety task, opening the app again) exit at once and must not.
        if (_ownsMutex) EdgeSnapping.Release();
        _dragWatcher?.Dispose();
        _downloads?.Dispose();
        if (_hotkeySink != null)
        {
            WindowApi.UnregisterHotKey(_hotkeySink.Handle, AbsorbHotkeyId);
            _hotkeySink.Dispose();
        }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _media?.Dispose();
        // While the island isn't running, Windows shows its own notification pop-ups again.
        if (_ownsMutex) NotificationBanners.RestoreAll();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>One island per monitor. Rebuilt whenever monitors are added, removed or rearranged.</summary>
    private void CreateIslands()
    {
        foreach (var island in _islands) island.Close();
        _islands.Clear();

        var screens = AppSettings.Current.ShowOnAllMonitors
            ? Forms.Screen.AllScreens
            : Forms.Screen.AllScreens.Where(s => s.Primary).ToArray();
        foreach (var screen in screens)
        {
            var b = screen.Bounds;
            var island = new IslandWindow(_media!, _vault!, _dragWatcher!, _downloads!, _notifications!, _pins!, new Int32Rect(b.X, b.Y, b.Width, b.Height)) { UserHidden = _hidden };
            if (!_hidden) island.Show();
            _islands.Add(island);
        }
        Log.Info($"Islands created for {_islands.Count} monitor(s)");
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            _displayChangeDebounce.Stop();
            _displayChangeDebounce.Start();
        });

    /// <summary>Ctrl+Alt+Z throws the active window into the island on its monitor.</summary>
    private void RegisterAbsorbHotkey()
    {
        _hotkeySink = new HwndSource(new HwndSourceParameters("DynamicIslandHotkeys") { ParentWindow = new IntPtr(-3) /* message-only */ });
        _hotkeySink.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == AbsorbHotkeyId)
            {
                AbsorbForegroundWindow();
                handled = true;
            }
            return IntPtr.Zero;
        });
        if (!WindowApi.RegisterHotKey(_hotkeySink.Handle, AbsorbHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_Z))
            Log.Info("Ctrl+Alt+Z is already used by another app; the absorb shortcut is disabled");
    }

    private void AbsorbForegroundWindow()
    {
        if (!AppSettings.Current.AbsorbShortcutEnabled || !AppSettings.Current.BlackHoleEnabled) return;
        var hwnd = WindowApi.GetForegroundWindow();
        if (!WindowVault.CanAbsorb(hwnd) || _hidden) return;
        var r = WindowApi.GetVisibleBounds(hwnd);
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        var island = _islands.FirstOrDefault(i =>
            cx >= i.Monitor.X && cx < i.Monitor.X + i.Monitor.Width && cy >= i.Monitor.Y && cy < i.Monitor.Y + i.Monitor.Height)
            ?? _islands.FirstOrDefault();
        island?.AbsorbWindow(hwnd, null);
    }

    private void SetHidden(bool hidden)
    {
        _hidden = hidden;
        foreach (var island in _islands) island.UserHidden = hidden;
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem($"Dynamic Island {VersionText}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var settings = new Forms.ToolStripMenuItem("Settings…");
        settings.Font = new Drawing.Font(settings.Font, Drawing.FontStyle.Bold);
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        var hide = new Forms.ToolStripMenuItem("Hide island") { CheckOnClick = true };
        hide.CheckedChanged += (_, _) => SetHidden(hide.Checked);
        menu.Items.Add(hide);

        var logs = new Forms.ToolStripMenuItem("Open log folder");
        logs.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(Log.LogDirectory);
            Process.Start(new ProcessStartInfo(Log.LogDirectory) { UseShellExecute = true });
        };
        menu.Items.Add(logs);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var quit = new Forms.ToolStripMenuItem("Quit Dynamic Island");
        quit.Click += (_, _) => QuitIsland();
        menu.Items.Add(quit);

        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico")).Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "Dynamic Island",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) hide.Checked = !hide.Checked;
        };
    }

    private const string RestartArg = "--restart";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
    private bool _lastAllMonitors;

    public void OpenSettings()
    {
        var primary = Forms.Screen.PrimaryScreen!.Bounds;
        Overlays.SettingsWindow.ShowSingle(new Overlays.SettingsContext(
            _pins!,
            new Int32Rect(primary.X, primary.Y, primary.Width, primary.Height),
            Restart: RestartIsland,
            Quit: QuitIsland));
    }

    /// <summary>Quit for good (tray Quit, Settings › About › Quit).</summary>
    public void QuitIsland()
    {
        // Tell the watchdog (and the every-minute task) this is on purpose; the island comes back
        // at the next restart, or when it's opened again.
        AutoStartTask.RememberQuit();
        Guardian.SignalQuit();
        Shutdown();
    }

    /// <summary>Restart now (e.g. after changing the startup priority): a new instance takes over once this one exits.</summary>
    private void RestartIsland()
    {
        if (Environment.ProcessPath is not { } path) return;
        Guardian.SignalQuit();
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c start \"\" \"{path}\" {RestartArg}") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        Shutdown();
    }

    private static bool IslandRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // held by a high-priority island
        }
    }

    private void OnSettingsChanged()
    {
        var s = AppSettings.Current;
        if (s.ShowOnAllMonitors != _lastAllMonitors)
        {
            _lastAllMonitors = s.ShowOnAllMonitors;
            CreateIslands();
        }
        EdgeSnapping.Apply(s.SnapLayoutsEnabled);
        // Never leave the user without notifications: banners are hidden only while the island shows them.
        if (s.ShouldHideBanners) NotificationBanners.HideAll();
        else NotificationBanners.RestoreAll();
    }

    private static async System.Threading.Tasks.Task RunDiagnosticsAsync()
    {
        try
        {
            Log.Info("DIAG Wi‑Fi available: " + await WifiService.InitAsync() + ", radio on: " + WifiService.RadioOn);
            foreach (var n in await WifiService.ScanAsync())
                Log.Info($"DIAG Wi‑Fi '{n.Ssid}' bars={n.Bars} secured={n.Secured} connected={n.Connected} saved={n.Saved}");
            Log.Info("DIAG Wi‑Fi names hidden: " + WifiService.NamesHidden + ", saved profiles: " + string.Join(", ", WifiService.SavedNetworks()));
            Log.Info("DIAG Bluetooth available: " + await BluetoothService.InitAsync() + ", radio on: " + BluetoothService.RadioOn);
            foreach (var d in await BluetoothService.GetPairedAsync())
                Log.Info($"DIAG BT paired '{d.Name}' kind={d.Kind} connected={d.Connected} le={d.IsLowEnergy}");
            BluetoothService.StartDiscovery();
            await System.Threading.Tasks.Task.Delay(8000);
            foreach (var d in BluetoothService.Nearby()) Log.Info($"DIAG BT nearby '{d.Name}' kind={d.Kind}");
            BluetoothService.StopDiscovery();
        }
        catch (Exception ex)
        {
            Log.Error("DIAG failed", ex);
        }
    }

    private static bool HighPriorityChosen()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\DynamicIsland");
            return key?.GetValue("HighPriority") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForOtherInstancesToExit(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        using var self = Process.GetCurrentProcess();
        while (DateTime.UtcNow < until)
        {
            var others = Process.GetProcessesByName(Guardian.IslandProcessName)
                .Concat(Process.GetProcessesByName(Guardian.GuardProcessName))
                .Where(p => p.Id != self.Id).ToList();
            bool any = others.Count > 0;
            foreach (var p in others) p.Dispose();
            if (!any) return;
            Thread.Sleep(200);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the island alive; one bad event shouldn't make it vanish from the screen.
        Log.Error("Dispatcher exception", e.Exception);
        e.Handled = true;
    }

    private void OnFatalException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("Fatal exception", e.ExceptionObject as Exception);

        // Never leave absorbed windows stranded.
        try
        {
            _vault?.RestoreAll();
            EdgeSnapping.Release();
            foreach (var island in _islands)
            {
                island.ReleaseCursor();
                island.ReleaseReservedSpace();
            }
        }
        catch
        {
            // The next start recovers them from vault.json anyway.
        }

        // Behave like part of Windows: come back on our own, unless we're crashing right after a restart.
        bool crashLoop = _afterCrash && DateTime.Now - _startedAt < TimeSpan.FromSeconds(30);
        if (crashLoop || Environment.ProcessPath is not { } path) return;
        try
        {
            Process.Start(path, AfterCrashArg);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restart after crash", ex);
        }
    }
}
