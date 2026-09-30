using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicIsland.Overlays;

/// <summary>
/// The in-island "Search &amp; play" panel: an embedded browser (WebView2) showing YouTube, docked
/// under the notch. It's a separate opaque window because WebView2 can't render inside the island's
/// transparent window. Audio keeps playing while it's tucked away (hidden, not closed), so the notch
/// then shows the song like any other media.
/// </summary>
internal sealed class MediaBrowserWindow : Window
{
    private const string Home = "https://www.youtube.com";

    // Injects a stylesheet that hides YouTube's masthead (its logo + search + sign-in) and closes the
    // gap it leaves, so the island's own search box is the only search. Re-applies on every navigation.
    private const string HideMastheadScript =
        "(function(){var css='ytd-masthead,#masthead,#masthead-container,#container.ytd-searchbox," +
        "tp-yt-app-header{display:none!important}#page-manager{margin-top:0!important}" +
        "ytd-app{--ytd-masthead-height:0px!important}';" +
        "var apply=function(){var s=document.getElementById('di-hide')||document.createElement('style');" +
        "s.id='di-hide';s.textContent=css;(document.head||document.documentElement).appendChild(s);};" +
        "apply();document.addEventListener('DOMContentLoaded',apply);})();";
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static MediaBrowserWindow? _instance;

    private readonly WebView2 _web = new();
    private readonly TextBox _search = new();
    private readonly Grid _fallback = new() { Visibility = Visibility.Collapsed };
    private readonly double _dipWidth = 780, _dipHeight = 560;
    private IntPtr _hwnd;
    private bool _ready;
    private bool _initFailed;
    private System.Threading.Tasks.Task? _initTask;
    private Int32Rect _monitor;

    /// <summary>Opens the panel under the given monitor's notch, or tucks it away if already open there.</summary>
    public static void Toggle(Int32Rect monitor)
    {
        _instance ??= new MediaBrowserWindow();
        _instance.ToggleFor(monitor);
    }

    public static void ShutDown()
    {
        _instance?.Close();
        _instance = null;
    }

    private MediaBrowserWindow()
    {
        Title = "Search & play";
        Width = _dipWidth;
        Height = _dipHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F));
        Content = BuildLayout();

        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => Hide(); // click away → tuck it away; audio keeps playing
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top bar: a grabber, the search box, and a close button.
        var bar = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(14, 7, 14, 7),
        };
        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        _search.Foreground = Brushes.White;
        _search.CaretBrush = Brushes.White;
        _search.FontSize = 14;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.KeyDown += Search_KeyDown;
        _search.GotKeyboardFocus += (_, _) => { if (_search.Text == PlaceholderText) ClearPlaceholder(); };
        searchHost.Child = _search;
        Grid.SetColumn(searchHost, 0);
        bar.Children.Add(searchHost);

        var close = new Button
        {
            Content = "✕",
            Width = 34,
            Height = 34,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 14,
        };
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 1);
        bar.Children.Add(close);

        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        Grid.SetRow(_web, 1);
        _web.DefaultBackgroundColor = System.Drawing.Color.Black;
        root.Children.Add(_web);

        BuildFallback();
        Grid.SetRow(_fallback, 1);
        root.Children.Add(_fallback);

        SetPlaceholder();
        return root;
    }

    private void BuildFallback()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "The in-island player needs the WebView2 runtime, which isn't installed.",
            Foreground = Brushes.White,
            FontSize = 14,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
        });
        var open = new Button
        {
            Content = "Open your search in the browser instead",
            Padding = new Thickness(16, 8, 16, 8),
            Cursor = Cursors.Hand,
        };
        open.Click += (_, _) => { OpenInBrowser(_search.Text); Hide(); };
        panel.Children.Add(open);
        _fallback.Children.Add(panel);
    }

    // ---------------------------------------------------------------- placeholder

    private const string PlaceholderText = "Search YouTube for a song, then click it to play here";

    private void SetPlaceholder()
    {
        _search.Text = PlaceholderText;
        _search.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x88));
    }

    private void ClearPlaceholder()
    {
        _search.Text = "";
        _search.Foreground = Brushes.White;
    }

    // ---------------------------------------------------------------- lifecycle

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Rounded corners on Windows 11 (ignored on Windows 10).
        int pref = DwmwcpRound;
        try { DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int)); } catch { }
        _ = InitWebViewAsync();
    }

    // Runs exactly once, no matter how many times it's called (window-create and first open both call it).
    private System.Threading.Tasks.Task InitWebViewAsync() => _initTask ??= DoInitWebViewAsync();

    private async System.Threading.Tasks.Task DoInitWebViewAsync()
    {
        try
        {
            var dataFolder = Path.Combine(Log.LogDirectory, "WebView2");
            Directory.CreateDirectory(dataFolder);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await _web.EnsureCoreWebView2Async(env);

            // Open normal links in-place; block extra popup windows.
            _web.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!string.IsNullOrEmpty(args.Uri)) _web.CoreWebView2.Navigate(args.Uri);
            };

            // Hide YouTube's own top bar (logo + search + sign-in) so only the island's search box
            // drives it. Runs both before page scripts and after each load finishes (YouTube is an SPA).
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HideMastheadScript);
            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try { await _web.CoreWebView2.ExecuteScriptAsync(HideMastheadScript); } catch { }
            };

            _web.CoreWebView2.Navigate(Home);
            _ready = true;
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 could not start; falling back to the browser", ex);
            _initFailed = true;
            _web.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
        }
    }

    // ---------------------------------------------------------------- show / hide

    private void ToggleFor(Int32Rect monitor)
    {
        if (IsVisible && _monitor.X == monitor.X && _monitor.Y == monitor.Y)
        {
            Hide();
            return;
        }
        _monitor = monitor;
        Show();
        PositionUnderNotch();
        Activate();
        _search.Focus();
        _ = InitWebViewAsync();
    }

    private void PositionUnderNotch()
    {
        if (_hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(_dipWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(_dipHeight * dpi.DpiScaleY);
        int x = _monitor.X + (_monitor.Width - w) / 2;
        int y = _monitor.Y + (int)Math.Round(40 * dpi.DpiScaleY); // just below the notch
        WindowApi.PlaceTopmost(_hwnd, x, y, w, h);
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var query = _search.Text.Trim();
        if (string.IsNullOrEmpty(query) || query == PlaceholderText) return;

        if (_ready)
            _web.CoreWebView2.Navigate("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
        else if (_initFailed)
            OpenInBrowser(query);
    }

    private static void OpenInBrowser(string query)
    {
        var q = string.IsNullOrWhiteSpace(query) || query == PlaceholderText ? "" : query;
        var url = q == "" ? Home : "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not open the browser", ex); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _web.Dispose();
        base.OnClosed(e);
    }
}
