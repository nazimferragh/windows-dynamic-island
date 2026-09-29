using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DynamicIsland.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynamicIsland;

public partial class App : Application
{
    private const string MutexName = @"Local\DynamicIsland.SingleInstance";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private Forms.NotifyIcon? _tray;
    private IslandWindow? _island;
    private MediaService? _media;

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

        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Already running.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        Log.Info($"Starting Dynamic Island {VersionText}");

        _media = new MediaService();
        _island = new IslandWindow(_media);
        _island.Show();
        CreateTrayIcon();

        await _media.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add(new Forms.ToolStripMenuItem($"Dynamic Island {VersionText}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var hide = new Forms.ToolStripMenuItem("Hide island") { CheckOnClick = true };
        hide.CheckedChanged += (_, _) =>
        {
            if (_island != null) _island.UserHidden = hide.Checked;
        };
        menu.Items.Add(hide);

        var startup = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartupManager.IsEnabled };
        startup.CheckedChanged += (_, _) => StartupManager.SetEnabled(startup.Checked);
        menu.Items.Add(startup);

        var logs = new Forms.ToolStripMenuItem("Open log folder");
        logs.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(Log.LogDirectory);
            Process.Start(new ProcessStartInfo(Log.LogDirectory) { UseShellExecute = true });
        };
        menu.Items.Add(logs);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var quit = new Forms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Shutdown();
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the island alive; a prototype shouldn't vanish from the screen on a single bad event.
        Log.Error("Dispatcher exception", e.Exception);
        e.Handled = true;
    }
}
