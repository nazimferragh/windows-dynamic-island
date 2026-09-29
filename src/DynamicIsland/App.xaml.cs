using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
    private Forms.NotifyIcon? _tray;
    private MediaService? _media;
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

        _media = new MediaService();
        CreateIslands();
        CreateTrayIcon();

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
            var island = new IslandWindow(_media!, new Int32Rect(b.X, b.Y, b.Width, b.Height)) { UserHidden = _hidden };
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
