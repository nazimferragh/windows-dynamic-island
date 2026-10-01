using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    // The video currently loaded in the panel, so the island can show its real YouTube thumbnail
    // (Windows itself often only hands us the browser icon for browser playback).
    private static readonly HttpClient Http = new();
    private static BitmapSource? _nowThumb;
    private static string _nowTitle = "";
    public static event Action? NowPlayingChanged;

    /// <summary>The YouTube thumbnail for the panel's current video, if its title matches what's playing.</summary>
    public static BitmapSource? ThumbnailFor(string playingTitle)
    {
        if (_nowThumb == null || string.IsNullOrWhiteSpace(playingTitle)) return null;
        return TitlesMatch(_nowTitle, playingTitle) ? _nowThumb : null;
    }

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    private static bool TitlesMatch(string webTitle, string playing)
    {
        var a = Normalize(webTitle);
        var b = Normalize(playing);
        if (a.Length == 0 || b.Length == 0) return false;
        return a.Contains(b) || b.Contains(a);
    }

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

    /// <summary>True when the panel itself is what's playing this title.</summary>
    public static bool IsPlaying(string title) =>
        _instance != null && !string.IsNullOrWhiteSpace(title) && TitlesMatch(_nowTitle, title);

    /// <summary>
    /// Opens the panel on a video (the song playing in the island), continuing from the given
    /// position. If the panel is already on it, it's just brought up without restarting playback.
    /// Without a video id it falls back to a search for the title.
    /// </summary>
    public static async void ShowVideo(Int32Rect monitor, string? videoId, string title, TimeSpan at)
    {
        _instance ??= new MediaBrowserWindow();
        var panel = _instance;
        bool alreadyThere = (videoId != null && videoId == panel._lastVideoId) || IsPlaying(title);
        panel.ShowFor(monitor);
        if (alreadyThere) return;

        try
        {
            await panel.InitWebViewAsync();
            if (!panel._ready)
            {
                if (panel._initFailed) OpenInBrowser(title);
                return;
            }
            var url = videoId != null
                ? $"https://www.youtube.com/watch?v={videoId}&t={(int)Math.Max(0, at.TotalSeconds)}s"
                : "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(title);
            panel._web.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the song in the panel", ex);
        }
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

        // Top bar: [ 🔍 search box ] [ x ]
        var bar = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var magnifier = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 7,0 A 7,7 0 1 0 7,14 A 7,7 0 0 0 7,0 M 12,12 L 18,18"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
            StrokeThickness = 1.7,
            Stretch = Stretch.Uniform,
            Width = 15,
            Height = 15,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0),
        };
        Grid.SetColumn(magnifier, 0);
        searchRow.Children.Add(magnifier);

        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        _search.Foreground = Brushes.White;
        _search.CaretBrush = Brushes.White;
        _search.FontSize = 13.5;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.KeyDown += Search_KeyDown;
        _search.GotKeyboardFocus += (_, _) => { if (_search.Text == PlaceholderText) ClearPlaceholder(); };
        _search.LostKeyboardFocus += (_, _) => { if (string.IsNullOrEmpty(_search.Text)) SetPlaceholder(); };
        Grid.SetColumn(_search, 1);
        searchRow.Children.Add(_search);

        var searchHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Height = 38,
            Padding = new Thickness(13, 0, 13, 0),
            Child = searchRow,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(searchHost, 0);
        bar.Children.Add(searchHost);

        var close = new Border
        {
            Width = 38,
            Height = 38,
            Margin = new Thickness(10, 0, 0, 0),
            CornerRadius = new CornerRadius(999),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 11,11 M 11,0 L 0,11"),
                Stroke = Brushes.White,
                StrokeThickness = 1.6,
                Stretch = Stretch.Uniform,
                Width = 11,
                Height = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            },
        };
        close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, _) => Hide();
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

    private const string PlaceholderText = "Search for a song…";

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

    /// <summary>Ad and tracking servers YouTube pages call; refused while ad blocking is on.</summary>
    private static readonly string[] AdServerPatterns =
    {
        "*://*.doubleclick.net/*",
        "*://*.googlesyndication.com/*",
        "*://*.googleadservices.com/*",
        "*://*.google-analytics.com/*",
        "*://www.youtube.com/pagead/*",
        "*://www.youtube.com/api/stats/ads*",
        "*://www.youtube.com/ptracking*",
        "*://www.youtube.com/get_midroll_*",
        "*://*.youtube.com/youtubei/v1/log_event*",
    };

    private string? _adBlockScriptId;
    private bool _adBlockOn;
    private bool _adBlockConfirmed;

    private static readonly Lazy<string> AdBlockScript = new(() =>
    {
        using var stream = typeof(MediaBrowserWindow).Assembly.GetManifestResourceStream("YouTubeAdBlock.js");
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Adds or removes the ad-blocking page script to match the setting (reloading the page when it changes).</summary>
    private async System.Threading.Tasks.Task ApplyAdBlockAsync(bool reload)
    {
        if (_web.CoreWebView2 == null) return;
        bool want = AppSettings.Current.BlockYouTubeAds;
        if (want == _adBlockOn && (_adBlockScriptId != null) == want) return;
        try
        {
            if (want && _adBlockScriptId == null && AdBlockScript.Value.Length > 0)
                _adBlockScriptId = await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockScript.Value);
            else if (!want && _adBlockScriptId != null)
            {
                _web.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_adBlockScriptId);
                _adBlockScriptId = null;
            }
            _adBlockOn = want;
            Log.Info($"YouTube ad blocking {(want ? "on" : "off")}");
            if (reload) _web.CoreWebView2.Reload();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't change YouTube ad blocking", ex);
        }
    }

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

            // Ads: blocked and skipped (Settings › Now playing). The page script strips and skips ads;
            // requests to ad servers are refused here.
            await ApplyAdBlockAsync(reload: false);
            foreach (var pattern in AdServerPatterns)
                _web.CoreWebView2.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
            _web.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                if (!AppSettings.Current.BlockYouTubeAds) return;
                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            AppSettings.Changed += () => Dispatcher.InvokeAsync(() => _ = ApplyAdBlockAsync(reload: true));
            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try { await _web.CoreWebView2.ExecuteScriptAsync(HideMastheadScript); } catch { }
                _ = UpdateNowPlayingAsync();
                if (_adBlockOn && !_adBlockConfirmed)
                {
                    try
                    {
                        _adBlockConfirmed = await _web.CoreWebView2.ExecuteScriptAsync("!!window.__islandAdBlock") == "true";
                        Log.Info($"YouTube ad blocking active in the page: {_adBlockConfirmed}");
                    }
                    catch { }
                }
            };
            // SPA navigations (clicking a video) don't reload the document, so also watch the URL.
            _web.CoreWebView2.SourceChanged += (_, _) => _ = UpdateNowPlayingAsync();

            // Downloads started from the in-island browser get exact progress in the island.
            _web.CoreWebView2.DownloadStarting += (_, e) =>
            {
                var op = e.DownloadOperation;
                var id = "wv:" + Guid.NewGuid().ToString("N");
                var name = Path.GetFileName(op.ResultFilePath);
                void Update(bool complete) =>
                    DownloadWatcher.Instance?.Report(id, name, (long)op.BytesReceived, (long)(op.TotalBytesToReceive ?? 0), complete, op.ResultFilePath);
                Update(false);
                op.BytesReceivedChanged += (_, _) => Update(false);
                op.StateChanged += (_, _) => Update(op.State == CoreWebView2DownloadState.Completed);
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

    // ---------------------------------------------------------------- now-playing thumbnail

    private string _lastVideoId = "";

    private async System.Threading.Tasks.Task UpdateNowPlayingAsync()
    {
        try
        {
            if (!_ready) return;
            var id = ParseVideoId(_web.CoreWebView2.Source);
            if (id == null || id == _lastVideoId) return;
            _lastVideoId = id;

            // The page title (updates a beat after an SPA navigation).
            await System.Threading.Tasks.Task.Delay(400);
            var raw = await _web.CoreWebView2.ExecuteScriptAsync("document.title");
            _nowTitle = raw.Trim('"').Replace("\\u0026", "&").Replace(" - YouTube", "");

            _nowThumb = await DownloadThumbnailAsync(id);
            if (_nowThumb != null) Dispatcher.Invoke(() => NowPlayingChanged?.Invoke());
        }
        catch (Exception ex)
        {
            Log.Error("Could not update now-playing thumbnail", ex);
        }
    }

    private static string? ParseVideoId(string url)
    {
        var m = Regex.Match(url ?? "", @"[?&]v=([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static async System.Threading.Tasks.Task<BitmapSource?> DownloadThumbnailAsync(string id)
    {
        // hqdefault always exists; maxres doesn't for every video.
        foreach (var name in new[] { "maxresdefault", "hqdefault" })
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync($"https://i.ytimg.com/vi/{id}/{name}.jpg");
                if (bytes.Length < 2000) continue; // YouTube returns a tiny grey placeholder when missing
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                // hqdefault is 4:3 with black bars baked in around the 16:9 picture; cut them off.
                if (name == "hqdefault" && bmp.PixelHeight * 4 == bmp.PixelWidth * 3)
                {
                    int h = bmp.PixelWidth * 9 / 16;
                    var cropped = new CroppedBitmap(bmp, new Int32Rect(0, (bmp.PixelHeight - h) / 2, bmp.PixelWidth, h));
                    cropped.Freeze();
                    return cropped;
                }
                return bmp;
            }
            catch
            {
                // try the next size
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- show / hide

    private void ToggleFor(Int32Rect monitor)
    {
        if (IsVisible && _monitor.X == monitor.X && _monitor.Y == monitor.Y)
        {
            Hide();
            return;
        }
        ShowFor(monitor);
        _search.Focus();
    }

    private void ShowFor(Int32Rect monitor)
    {
        _monitor = monitor;
        Show();
        PositionUnderNotch();
        Activate();
        _ = InitWebViewAsync();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Re-center once Windows has resized the panel for the new monitor's scale.
        Dispatcher.BeginInvoke(PositionUnderNotch, System.Windows.Threading.DispatcherPriority.Loaded);
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
