using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>What the Settings window needs from the app.</summary>
internal sealed record SettingsContext(PinnedApps Pins, Int32Rect PrimaryMonitor, Action Restart, Action Quit);

/// <summary>
/// Dynamic Island's settings, laid out like Windows 11's own Settings app: sections on the left,
/// rows of switches on the right. On Windows 11 it has the Mica backdrop; on Windows 10 a plain
/// background (same layout). Follows light/dark app mode and the accent color, live.
/// Every switch applies immediately.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private const int Win11MicaBuild = 22621;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private readonly SettingsContext _ctx;
    private readonly StackPanel _nav = new() { Margin = new Thickness(8, 8, 8, 8) };
    private readonly StackPanel _pane = new() { Margin = new Thickness(28, 6, 32, 28) };
    private readonly ScrollViewer _paneScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _titleText = new() { Text = "Dynamic Island Settings", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly bool _mica = Environment.OSVersion.Version.Build >= Win11MicaBuild;
    private string _page = "General";
    private IntPtr _hwnd;

    // Palette (rebuilt when Windows switches light/dark).
    private Brush _fg = Brushes.White, _fg2 = Brushes.Gray, _card = Brushes.Transparent, _cardLine = Brushes.Transparent,
        _navSel = Brushes.Transparent, _accent = Brushes.DodgerBlue, _onAccent = Brushes.Black, _solidBg = Brushes.Black;

    public static void ShowSingle(SettingsContext ctx)
    {
        if (_open == null)
        {
            _open = new SettingsWindow(ctx);
            _open.Show();
        }
        if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
        _open.BringToFront();
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    /// <summary>
    /// The island never takes focus, so Windows may refuse to bring this window forward on its own.
    /// Briefly topmost plus a tap of Alt (the standard way around the foreground lock) puts it in front.
    /// </summary>
    private void BringToFront()
    {
        Topmost = true;
        keybd_event(0x12 /* VK_MENU */, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, 0x2 /* KEYUP */, IntPtr.Zero);
        if (_hwnd != IntPtr.Zero) SetForegroundWindow(_hwnd);
        Activate();
        Dispatcher.BeginInvoke(() => Topmost = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private SettingsWindow(SettingsContext ctx)
    {
        _ctx = ctx;
        Title = "Dynamic Island Settings";
        Width = 900;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon.ico"));

        if (_mica)
        {
            // Let DWM paint Mica behind the whole window, with Windows' own caption buttons.
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 40,
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = true,
            });
            Background = Brushes.Transparent;
        }

        ApplyPalette(); // before building: every row takes its colors from the palette
        Content = BuildLayout();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyFrame();
        };
        WindowsTheme.Current.Changed += OnThemeChanged;
        _ctx.Pins.Changed += OnPinsChanged;
        Closed += (_, _) =>
        {
            WindowsTheme.Current.Changed -= OnThemeChanged;
            _ctx.Pins.Changed -= OnPinsChanged;
            _open = null;
        };
    }

    private void OnThemeChanged()
    {
        ApplyPalette();
        ApplyFrame();
        ShowPage(_page);
    }

    private void OnPinsChanged()
    {
        if (_page == "Pinned apps") ShowPage(_page);
    }

    private void ApplyPalette()
    {
        bool light = WindowsTheme.Current.AppsLight;
        var t = WindowsTheme.Current;
        _fg = Solid(light ? 0xFF1B1B1Bu : 0xFFFFFFFFu);
        _fg2 = Solid(light ? 0xFF5F5F5Fu : 0xFFA8A8A8u);
        _card = Solid(light ? 0xB3FFFFFFu : 0x0DFFFFFFu);
        _cardLine = Solid(light ? 0x14000000u : 0x0FFFFFFFu);
        _navSel = Solid(light ? 0x0F000000u : 0x0FFFFFFFu);
        _accent = new SolidColorBrush(light ? t.AccentOnLight : t.AccentOnDark);
        _onAccent = Solid(light ? 0xFFFFFFFFu : 0xFF000000u);
        _solidBg = Solid(light ? 0xFFF3F3F3u : 0xFF202020u);
        if (!_mica) Background = _solidBg;
        Foreground = _fg;
        _titleText.Foreground = _fg;
    }

    /// <summary>Dark/light title bar and (Windows 11) the Mica backdrop.</summary>
    private void ApplyFrame()
    {
        if (_hwnd == IntPtr.Zero) return;
        int dark = WindowsTheme.Current.AppsLight ? 0 : 1;
        try { DwmSetWindowAttribute(_hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int)); } catch { }
        if (_mica)
        {
            int mica = 2; // DWMSBT_MAINWINDOW
            try { DwmSetWindowAttribute(_hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref mica, sizeof(int)); } catch { }
        }
    }

    private static SolidColorBrush Solid(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    // ---------------------------------------------------------------- layout

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_mica ? 40 : 4) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (_mica)
        {
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
            title.Children.Add(new Image
            {
                Source = Icon,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            title.Children.Add(_titleText);
            Grid.SetColumnSpan(title, 2);
            root.Children.Add(title);
        }

        Grid.SetRow(_nav, 1);
        root.Children.Add(_nav);

        _paneScroll.Content = _pane;
        Grid.SetRow(_paneScroll, 1);
        Grid.SetColumn(_paneScroll, 1);
        root.Children.Add(_paneScroll);

        ShowPage(_page);
        return root;
    }

    private static readonly string[] Pages =
        { "General", "Status icons", "Now playing", "Notifications", "Black hole", "Downloads", "Pinned apps", "About" };

    private void BuildNav()
    {
        _nav.Children.Clear();

        // App identity, like the account tile at the top of Windows Settings.
        var me = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4, 0, 16) };
        me.Children.Add(new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Black,
            Child = new Border { Width = 20, Height = 7, CornerRadius = new CornerRadius(4), Background = Brushes.White },
        });
        var who = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new TextBlock { Text = "Dynamic Island", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _fg });
        who.Children.Add(new TextBlock { Text = "Version " + App.VersionText, FontSize = 11.5, Foreground = _fg2 });
        me.Children.Add(who);
        _nav.Children.Add(me);

        foreach (var page in Pages)
        {
            bool on = page == _page;
            var marker = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                Background = on ? _accent : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var label = new TextBlock { Text = page, FontSize = 13.5, Foreground = _fg, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var item = new Border
            {
                Height = 36,
                CornerRadius = new CornerRadius(5),
                Background = on ? _navSel : Brushes.Transparent,
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Child = new Grid { Children = { marker, label } },
            };
            item.MouseEnter += (_, _) => { if (page != _page) item.Background = _navSel; };
            item.MouseLeave += (_, _) => { if (page != _page) item.Background = Brushes.Transparent; };
            item.MouseLeftButtonUp += (_, _) => ShowPage(page);
            _nav.Children.Add(item);
        }
    }

    private void ShowPage(string page)
    {
        _page = page;
        BuildNav();
        _pane.Children.Clear();
        _pane.Children.Add(new TextBlock
        {
            Text = page,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = _fg,
            Margin = new Thickness(0, 0, 0, 14),
        });
        var s = AppSettings.Current;
        switch (page)
        {
            case "General": BuildGeneral(s); break;
            case "Status icons":
                Hint("Shown in the top row of the open island.");
                Toggle("Wi‑Fi", "Signal strength. Click: the island's Wi‑Fi list, to join or switch networks", s.StatusWifi, v => AppSettings.Update(x => x.StatusWifi = v));
                Toggle("Bluetooth", "On or off. Click: Bluetooth settings", s.StatusBluetooth, v => AppSettings.Update(x => x.StatusBluetooth = v));
                Toggle("Battery", "Laptops only: the level, charging and time left", s.StatusBattery, v => AppSettings.Update(x => x.StatusBattery = v));
                Toggle("Battery percentage", "The exact % next to the battery, updated the moment it changes", s.StatusBatteryPercent, v => AppSettings.Update(x => x.StatusBatteryPercent = v));
                break;
            case "Now playing":
                Toggle("Show a preview when a new song starts", "The island briefly shows the song's name", s.ShowSongPreview, v => AppSettings.Update(x => x.ShowSongPreview = v));
                Toggle("Show the song in the closed island", "Artwork and moving bars while music plays", s.ShowClosedMedia, v => AppSettings.Update(x => x.ShowClosedMedia = v));
                Hint("Click the artwork in the open island to keep watching the song in the island's player.");
                break;
            case "Notifications":
                Toggle("Show notifications in the island", "Messages, emails and alerts appear at the top of the screen", s.NotificationsInIsland, v => AppSettings.Update(x => x.NotificationsInIsland = v));
                Toggle("Hide Windows' notification pop-ups", "Show them only in the island. They still go to the notification center", s.HideWindowsBanners, v => AppSettings.Update(x => x.HideWindowsBanners = v));
                Choice("Keep each notification on screen for", "", new[] { ("3 s", 3), ("5 s", 5), ("8 s", 8) }, s.NotificationSeconds, v => AppSettings.Update(x => x.NotificationSeconds = v));
                Hint("Click a notification in the island to open it, the same as clicking it in Windows.");
                break;
            case "Black hole":
                Toggle("Drag windows into the island", "Hold a window over the island to tuck it away; hover the island to get it back", s.BlackHoleEnabled, v => AppSettings.Update(x => x.BlackHoleEnabled = v));
                Toggle("Ctrl + Alt + Z", "Throws the active window into the island", s.AbsorbShortcutEnabled, v => AppSettings.Update(x => x.AbsorbShortcutEnabled = v));
                Toggle("Snap layouts in the island", "Drag a window up to the island and drop it on a layout to arrange it", s.SnapLayoutsEnabled, v => AppSettings.Update(x => x.SnapLayoutsEnabled = v));
                Toggle("Fill the rest of the layout", "Your next most recent windows fill the other zones automatically", s.SnapAutoFill, v => AppSettings.Update(x => x.SnapAutoFill = v));
                break;
            case "Downloads":
                Toggle("Show downloads in the island", "Progress while downloading; click a finished one to open it", s.DownloadsEnabled, v => AppSettings.Update(x => x.DownloadsEnabled = v));
                Toggle("Animate when a download starts", "The island pops out with a drop animation", s.AnimateDownloadStart, v => AppSettings.Update(x => x.AnimateDownloadStart = v));
                Choice("Keep finished downloads listed for", "", new[] { ("1 min", 1), ("3 min", 3), ("10 min", 10) }, s.KeepFinishedDownloadsMinutes, v => AppSettings.Update(x => x.KeepFinishedDownloadsMinutes = v));
                break;
            case "Pinned apps": BuildPinned(s); break;
            case "About": BuildAbout(); break;
        }
    }

    private void BuildGeneral(AppSettings s)
    {
        Group("Startup");
        Toggle("Start with Windows", "Opens when you sign in and comes back if it stops. Also in Settings › Apps › Startup",
            !AutoStartTask.DisabledInWindowsStartup(), v => AutoStartTask.SetWindowsStartupEnabled(v));

        // Priority: read the task in the background (schtasks takes a moment).
        var status = new TextBlock { FontSize = 12, Foreground = _fg2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        var seg = Segmented(new[] { ("High", 1), ("Normal", 0) }, -1, null);
        Row("Startup priority", "High priority starts it before other apps and keeps it smooth (Windows asks for administrator permission once)", seg, status);
        Task.Run(() => AutoStartTask.Query()).ContinueWith(t =>
        {
            int current = t.Result is { High: true } ? 1 : 0;
            SetSegmented(seg, current, async picked =>
            {
                status.Visibility = Visibility.Visible;
                status.Text = picked == 1 ? "Waiting for Windows' permission…" : "Changing…";
                bool ok = await Task.Run(() => AutoStartTask.Register(picked == 1));
                if (!ok)
                {
                    status.Text = picked == 1 ? "Permission wasn't given, so it stays on Normal." : "Couldn't change it.";
                    SetSegmented(seg, 1 - picked, null);
                    return;
                }
                status.Inlines.Clear();
                status.Inlines.Add(new System.Windows.Documents.Run("Saved. It applies the next time the island starts.  "));
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Restart now")) { Foreground = _accent };
                link.Click += (_, _) => _ctx.Restart();
                status.Inlines.Add(link);
            });
        }, TaskScheduler.FromCurrentSynchronizationContext());

        Group("Display");
        Choice("Show the island on", "Which monitors get an island", new[] { ("All monitors", 1), ("Main monitor only", 0) },
            s.ShowOnAllMonitors ? 1 : 0, v => AppSettings.Update(x => x.ShowOnAllMonitors = v == 1));
        Choice("Open on hover after", "How long the pointer rests on the island before it expands",
            new[] { ("0.1 s", 100), ("0.2 s", 220), ("0.4 s", 400), ("0.7 s", 700) }, s.HoverDelayMs, v => AppSettings.Update(x => x.HoverDelayMs = v));
        Toggle("Hide over full-screen apps", "Games, videos and presentations in full screen", s.HideOverFullscreen, v => AppSettings.Update(x => x.HideOverFullscreen = v));
        Toggle("Match Windows colors", "Use your accent color and follow light or dark mode, like the taskbar", s.MatchWindowsColors, v => AppSettings.Update(x => x.MatchWindowsColors = v));
    }

    private void BuildPinned(AppSettings s)
    {
        Toggle("Show the apps button", "The grid button at the top right of the open island", s.PinnedAppsEnabled, v => AppSettings.Update(x => x.PinnedAppsEnabled = v));
        Group("Your pinned apps");
        if (_ctx.Pins.Apps.Count == 0) Hint("Nothing pinned yet.");
        foreach (var app in _ctx.Pins.Apps)
        {
            var icon = new Image { Source = ShellIcons.Get(app.ParsingName, 64), Width = 24, Height = 24, Margin = new Thickness(0, 0, 14, 0) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var name = new TextBlock { Text = app.Name, FontSize = 13.5, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name } };
            var remove = ActionButton("Remove", () => _ctx.Pins.Unpin(app));
            Card(left, remove);
        }
        var add = ActionButton("Add an app", () => AppPickerWindow.ShowFor(_ctx.Pins, _ctx.PrimaryMonitor), primary: true);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 10, 0, 0);
        _pane.Children.Add(add);
    }

    private void BuildAbout()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Black,
            Child = new Border { Width = 26, Height = 9, CornerRadius = new CornerRadius(5), Background = Brushes.White },
        });
        var text = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Dynamic Island for Windows", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = _fg });
        text.Children.Add(new TextBlock { Text = $"Version {App.VersionText} · by Nazim Abderahman", FontSize = 12.5, Foreground = _fg2 });
        head.Children.Add(text);
        Card(head, null);

        Card(Label("Project page", "Source code, releases and issues on GitHub"),
            ActionButton("Open", () => OpenUrl("https://github.com/nazimferragh/windows-dynamic-island")));
        Card(Label("Logs", "Helpful when something doesn't work as expected"),
            ActionButton("Open folder", () => OpenUrl(Log.LogDirectory)));
        Card(Label("Quit Dynamic Island", "Stops it until you open it again or restart Windows"),
            ActionButton("Quit", _ctx.Quit));
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error($"Couldn't open {target}", ex); }
    }

    // ---------------------------------------------------------------- building blocks

    private void Group(string text) =>
        _pane.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = _fg, Margin = new Thickness(0, 14, 0, 6) });

    private void Hint(string text) =>
        _pane.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = _fg2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 0) });

    private FrameworkElement Label(string title, string subtitle)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = _fg, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(subtitle))
            stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = _fg2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        return stack;
    }

    /// <summary>A settings card: content on the left, a control on the right.</summary>
    private void Card(FrameworkElement left, FrameworkElement? right, FrameworkElement? below = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        if (right != null)
        {
            right.Margin = new Thickness(20, 0, 0, 0);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        var content = below == null ? (UIElement)grid : new StackPanel { Children = { grid, below } };
        _pane.Children.Add(new Border
        {
            Background = _card,
            BorderBrush = _cardLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 3),
            MinHeight = 66,
            Child = content,
        });
    }

    private void Row(string title, string subtitle, FrameworkElement control, FrameworkElement? below = null) =>
        Card(Label(title, subtitle), control, below);

    private void Toggle(string title, string subtitle, bool value, Action<bool> changed) =>
        Row(title, subtitle, MakeToggle(value, changed));

    private void Choice(string title, string subtitle, (string Label, int Value)[] options, int value, Action<int> changed) =>
        Row(title, subtitle, Segmented(options, value, changed));

    /// <summary>A Windows 11 style on/off switch, with "On"/"Off" beside it like Settings shows.</summary>
    private FrameworkElement MakeToggle(bool value, Action<bool> changed)
    {
        var knob = new Ellipse12();
        var track = new Border { Width = 40, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = knob.Shape };
        var state = new TextBlock { FontSize = 13.5, Foreground = _fg, Width = 28, VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent, Children = { state, track } };
        bool on = value;
        void Render(bool animate)
        {
            state.Text = on ? "On" : "Off";
            track.Background = on ? _accent : Brushes.Transparent;
            track.BorderBrush = on ? _accent : _fg2;
            knob.Shape.Fill = on ? _onAccent : _fg2;
            knob.Move(on ? 22 : 4, animate);
        }
        Render(false);
        panel.MouseLeftButtonUp += (_, _) =>
        {
            on = !on;
            Render(true);
            changed(on);
        };
        return panel;
    }

    /// <summary>The switch's knob (12 px circle that slides).</summary>
    private sealed class Ellipse12
    {
        public readonly System.Windows.Shapes.Ellipse Shape = new() { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        private readonly TranslateTransform _move = new();
        public Ellipse12() => Shape.RenderTransform = _move;

        public void Move(double x, bool animate)
        {
            if (!animate) { _move.BeginAnimation(TranslateTransform.XProperty, null); _move.X = x - 1; return; }
            _move.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x - 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>A row of options, one selected (like a segmented control).</summary>
    private StackPanel Segmented((string Label, int Value)[] options, int value, Action<int>? changed)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Tag = options };
        SetSegmented(panel, value, changed);
        return panel;
    }

    private void SetSegmented(StackPanel panel, int value, Action<int>? changed)
    {
        var options = ((string Label, int Value)[])panel.Tag;
        panel.Children.Clear();
        for (int i = 0; i < options.Length; i++)
        {
            var (label, v) = options[i];
            bool on = v == value;
            var item = new Border
            {
                Padding = new Thickness(12, 5, 12, 6),
                Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                CornerRadius = new CornerRadius(5),
                Background = on ? _accent : _card,
                BorderBrush = on ? _accent : _cardLine,
                BorderThickness = new Thickness(1),
                Cursor = changed == null ? Cursors.Arrow : Cursors.Hand,
                Child = new TextBlock { Text = label, FontSize = 12.5, Foreground = on ? _onAccent : _fg, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal },
            };
            if (changed != null && !on)
                item.MouseLeftButtonUp += (_, _) =>
                {
                    SetSegmented(panel, v, changed);
                    changed(v);
                };
            panel.Children.Add(item);
        }
    }

    private Border ActionButton(string text, Action click, bool primary = false)
    {
        var button = new Border
        {
            Padding = new Thickness(16, 6, 16, 7),
            CornerRadius = new CornerRadius(5),
            Background = primary ? _accent : _card,
            BorderBrush = primary ? _accent : _cardLine,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 13, Foreground = primary ? _onAccent : _fg },
        };
        button.MouseEnter += (_, _) => button.Opacity = 0.85;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        button.MouseLeftButtonUp += (_, _) => click();
        return button;
    }
}
