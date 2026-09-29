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
    private const string AfterCrashArg = "--after-crash";

    private readonly List<IslandWindow> _islands = new();
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly DispatcherTimer _displayChangeDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };

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

        // After a crash the dying instance may still hold the mutex for a moment, so wait for it.
        _afterCrash = e.Args.Contains(AfterCrashArg);
        _mutex = new Mutex(false, MutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(_afterCrash ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        Log.Info($"Starting Dynamic Island {VersionText}{(_afterCrash ? " (restarted after crash)" : "")}");

        // Any windows a previous run left hidden (crash, forced kill) come back first.
        WindowVault.RecoverOrphans();
        // Settings that would get in the island's way (may restart Explorer once, right after install).
        await System.Threading.Tasks.Task.Run(ShellTweaks.ApplyOnce);
        _vault = new WindowVault();
        _dragWatcher = new WindowDragWatcher();
        SessionEnding += (_, _) => _vault.RestoreAll();

        _media = new MediaService();
        CreateIslands();
        CreateTrayIcon();
        RegisterAbsorbHotkey();

        _displayChangeDebounce.Tick += (_, _) =>
        {
            _displayChangeDebounce.Stop();
            CreateIslands();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        await _media.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _vault?.RestoreAll();
        _dragWatcher?.Dispose();
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
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>One island per monitor. Rebuilt whenever monitors are added, removed or rearranged.</summary>
    private void CreateIslands()
    {
        foreach (var island in _islands) island.Close();
        _islands.Clear();

        foreach (var screen in Forms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            var island = new IslandWindow(_media!, _vault!, _dragWatcher!, new Int32Rect(b.X, b.Y, b.Width, b.Height)) { UserHidden = _hidden };
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the island alive; one bad event shouldn't make it vanish from the screen.
        Log.Error("Dispatcher exception", e.Exception);
        e.Handled = true;
    }

    private void OnFatalException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("Fatal exception", e.ExceptionObject as Exception);

        // Never leave absorbed windows stranded, or the top strip reserved.
        try
        {
            _vault?.RestoreAll();
            foreach (var island in _islands) island.ReleaseReservedSpace();
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
