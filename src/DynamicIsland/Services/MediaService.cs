using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace DynamicIsland.Services;

public sealed class MediaSnapshot
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Source { get; init; }
    /// <summary>The app id (AUMID) reported by Windows, e.g. contains "chrome" / "msedge".</summary>
    public string AppId { get; init; } = "";
    public bool IsBrowser { get; init; }
    public BitmapSource? Artwork { get; init; }
    public Color Accent { get; init; }
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset PositionUpdatedAt { get; init; }
    public double PlaybackRate { get; init; } = 1;

    public string TrackKey => Title + "\u001f" + Artist;

    /// <summary>Apps only report position occasionally, so extrapolate from the last report.</summary>
    public TimeSpan EstimatePosition()
    {
        var pos = Position;
        if (IsPlaying && PositionUpdatedAt.Year > 2000)
            pos += TimeSpan.FromTicks((long)((DateTimeOffset.Now - PositionUpdatedAt).Ticks * PlaybackRate));
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
        return pos;
    }
}

/// <summary>
/// Watches Windows' system media controls (the same source as the volume flyout), so it works with
/// Spotify, browsers, Media Player, VLC, etc. Raises <see cref="Changed"/> on the UI thread.
/// </summary>
public sealed class MediaService : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private SessionManager? _manager;
    private Session? _session;
    private int _refreshVersion;
    private string? _artKey;
    private BitmapSource? _art;
    private Color _accent = ColorExtractor.DefaultAccent;

    private bool _publishedOnce;

    /// <summary>Raised with the new snapshot and whether a different track just started.</summary>
    public event Action<MediaSnapshot?, bool>? Changed;

    public MediaSnapshot? Current { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await SessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.InvokeAsync(AttachSession);
            _manager.SessionsChanged += (_, _) => _dispatcher.InvokeAsync(AttachSession);
            AttachSession();
        }
        catch (Exception ex)
        {
            Log.Error("Media service init failed", ex);
        }
    }

    public Task TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync());
    public Task NextAsync() => Run(s => s.TrySkipNextAsync());
    public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync());

    /// <summary>Jump to a fraction (0..1) of the current track.</summary>
    public Task SeekToFractionAsync(double fraction)
    {
        var s = Current;
        if (s == null || s.Duration <= TimeSpan.Zero) return Task.CompletedTask;
        long ticks = (long)(s.Duration.Ticks * Math.Clamp(fraction, 0, 1));
        return Run(session => session.TryChangePlaybackPositionAsync(ticks));
    }

    /// <summary>Move the playhead by the given offset (e.g. +/- 10 seconds).</summary>
    public Task SeekByAsync(TimeSpan delta)
    {
        var s = Current;
        if (s == null || s.Duration <= TimeSpan.Zero) return Task.CompletedTask;
        var target = s.EstimatePosition() + delta;
        return SeekToFractionAsync(target.TotalSeconds / s.Duration.TotalSeconds);
    }

    public void Dispose() => Detach();

    private async Task Run(Func<Session, IAsyncOperation<bool>> action)
    {
        var session = _session;
        if (session == null) return;
        try
        {
            await action(session);
        }
        catch (Exception ex)
        {
            Log.Error("Media command failed", ex);
        }
    }

    private void AttachSession()
    {
        if (_manager == null) return;
        Session? next = null;
        try
        {
            next = PickSession(_manager);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to pick media session", ex);
        }

        Detach();
        _session = next;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
        _ = RefreshAsync();
    }

    private void Detach()
    {
        if (_session == null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    /// <summary>Prefer whatever is actually playing; otherwise the session Windows considers current.</summary>
    private static Session? PickSession(SessionManager manager)
    {
        var current = manager.GetCurrentSession();
        if (current != null && IsPlaying(current)) return current;
        var sessions = manager.GetSessions();
        return sessions.FirstOrDefault(IsPlaying) ?? current ?? sessions.FirstOrDefault();
    }

    private static bool IsPlaying(Session session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => _ = RefreshAsync());

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => _ = RefreshAsync());

    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        int version = ++_refreshVersion;
        var session = _session;
        if (session == null)
        {
            Publish(null);
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion) return;

            var playback = session.GetPlaybackInfo();
            var status = playback?.PlaybackStatus;
            if (status is PlaybackStatus.Closed or PlaybackStatus.Stopped)
            {
                Publish(null);
                return;
            }

            // Players briefly report empty metadata while switching tracks; wait for the real thing.
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) return;

            string key = props.Title + "\u001f" + props.Artist;
            if (key != _artKey || _art == null)
            {
                // Artwork often arrives a moment after the title, so keep retrying while it's missing.
                var loaded = await LoadThumbnailAsync(props.Thumbnail);
                _artKey = key;
                _art = loaded;
                _accent = ColorExtractor.Extract(loaded);
                if (version != _refreshVersion) return;
            }

            var timeline = session.GetTimelineProperties();
            string appId = session.SourceAppUserModelId ?? "";
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumTitle ?? "" : props.Artist;

            // Browser playback (YouTube etc.) often gives no real cover art. Look up the video's
            // thumbnail from its title, so the island shows it instead of a blank tile.
            var art = _art;
            if (art == null && IsBrowserApp(appId))
            {
                if (_titleThumb.TryGetValue(key, out var cached)) art = cached;
                else { _titleThumb[key] = null; _ = FetchTitleThumbnailAsync(version, key, props.Title, artist); }
            }

            Publish(new MediaSnapshot
            {
                Title = props.Title,
                Artist = artist,
                Source = PrettifySource(appId),
                AppId = appId,
                IsBrowser = IsBrowserApp(appId),
                Artwork = art,
                Accent = _accent,
                IsPlaying = status == PlaybackStatus.Playing,
                // The timeline can be missing while an app is closing its media session.
                Position = timeline?.Position ?? TimeSpan.Zero,
                Duration = timeline != null ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero,
                PositionUpdatedAt = timeline?.LastUpdatedTime ?? default,
                PlaybackRate = playback?.PlaybackRate ?? 1.0,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Media refresh failed", ex);
        }
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        var previous = Current;
        // Whatever was already playing when the app started doesn't count as "new".
        bool isNewTrack = snapshot != null && (previous == null ? _publishedOnce : previous.TrackKey != snapshot.TrackKey);
        _publishedOnce = true;
        Current = snapshot;
        Changed?.Invoke(snapshot, isNewTrack);
    }

    // ---------------------------------------------------------------- thumbnail-by-title (browser playback)

    private static readonly HttpClient Http = CreateHttp();
    private readonly Dictionary<string, BitmapSource?> _titleThumb = new();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return c;
    }

    /// <summary>Finds the top YouTube result for the track and loads its thumbnail. Best-effort.</summary>
    private async Task FetchTitleThumbnailAsync(int version, string key, string title, string artist)
    {
        try
        {
            var query = Uri.EscapeDataString($"{title} {artist}".Trim());
            var html = await Http.GetStringAsync($"https://www.youtube.com/results?search_query={query}");
            var match = System.Text.RegularExpressions.Regex.Match(html, "\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            Log.Info($"Title lookup '{title}' -> videoId {(match.Success ? match.Groups[1].Value : "none")}");
            BitmapSource? thumb = null;
            if (match.Success)
            {
                foreach (var name in new[] { "maxresdefault", "hqdefault" })
                {
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync($"https://i.ytimg.com/vi/{match.Groups[1].Value}/{name}.jpg");
                        if (bytes.Length < 2000) continue;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new MemoryStream(bytes);
                        bmp.EndInit();
                        bmp.Freeze();
                        thumb = bmp;
                        break;
                    }
                    catch { }
                }
            }
            _titleThumb[key] = thumb;
            Log.Info($"Title thumb for '{title}': {(thumb != null ? "loaded" : "none")} (version {version}/{_refreshVersion})");
            // Re-publish with the art if this is still the current track.
            if (thumb != null) _dispatcher.InvokeAsync(() => _ = RefreshAsync());
        }
        catch (Exception ex)
        {
            Log.Error("Thumbnail-by-title lookup failed", ex);
            _titleThumb[key] = null;
        }
    }

    private static async Task<BitmapSource?> LoadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference == null) return null;
        try
        {
            using var winrtStream = await reference.OpenReadAsync();
            using var stream = winrtStream.AsStreamForRead();
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = buffer; // natural size, so we can tell real cover art from an icon
            bitmap.EndInit();
            bitmap.Freeze();

            // Browsers/apps that don't publish real artwork make Windows hand us their app icon
            // (e.g. the Chrome logo). Real cover art is large; an icon is small. Drop the small ones
            // so the island shows a clean placeholder instead of a browser logo.
            if (Math.Min(bitmap.PixelWidth, bitmap.PixelHeight) < 128)
            {
                Log.Info($"Ignoring small artwork ({bitmap.PixelWidth}x{bitmap.PixelHeight}) - looks like an app icon");
                return null;
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load artwork", ex);
            return null;
        }
    }

    private static string PrettifySource(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        var name = appId;
        int bang = name.IndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        var lower = name.ToLowerInvariant();
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("opera")) return "Opera";
        if (lower.Contains("zunemusic")) return "Media Player";
        if (lower.Contains("applemusic")) return "Apple Music";
        if (lower.Contains("vlc")) return "VLC";

        name = Path.GetFileName(name.Replace('/', '\\'));
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name;
    }

    private static readonly string[] Browsers = { "chrome", "msedge", "edge", "firefox", "brave", "opera", "vivaldi", "chromium" };

    private static bool IsBrowserApp(string appId)
    {
        var lower = appId.ToLowerInvariant();
        return Array.Exists(Browsers, b => lower.Contains(b));
    }
}
