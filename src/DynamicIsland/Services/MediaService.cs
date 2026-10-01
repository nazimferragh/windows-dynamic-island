using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
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
    /// <summary>Set when the player says its metadata (including artwork) changed, so art is re-read.</summary>
    private bool _artDirty = true;

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
    public Task PauseAsync() => Run(s => s.TryPauseAsync());

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
        _dispatcher.InvokeAsync(() =>
        {
            _artDirty = true;
            _ = RefreshAsync();
        });

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
            if (key != _artKey)
            {
                // A new song never inherits the previous song's art.
                _artKey = key;
                _art = null;
                _artDirty = true;
            }
            if (_artDirty || _art == null)
            {
                // Browsers publish the title first and the real artwork a moment later (often with
                // their logo in between), so art is re-read on every metadata change, and retried
                // while it's missing.
                _artDirty = false;
                var loaded = await LoadThumbnailAsync(props.Thumbnail);
                if (version != _refreshVersion) return;
                if (loaded != null || _art == null)
                {
                    _art = loaded;
                    _accent = ColorExtractor.Extract(loaded);
                }
            }

            var timeline = session.GetTimelineProperties();
            string appId = session.SourceAppUserModelId ?? "";
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumTitle ?? "" : props.Artist;

            // Browser playback (YouTube etc.) often gives no real cover art. Look up the video's
            // thumbnail from its title, so the island shows it instead of a blank tile.
            // Browsers also hand over tiny thumbnails, so look up a sharp one for those too.
            var art = _art;
            if (IsBrowserApp(appId) && (art == null || art.PixelWidth < 300))
            {
                if (_titleThumb.TryGetValue(key, out var cached)) art = cached ?? art;
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
    private readonly Dictionary<string, string?> _videoIds = new();

    /// <summary>The YouTube video for a track (top search result for "title artist"), cached.</summary>
    public async Task<string?> FindVideoIdAsync(string title, string artist)
    {
        string key = title + "\u001f" + artist;
        if (_videoIds.TryGetValue(key, out var known) && known != null) return known;
        try
        {
            var query = Uri.EscapeDataString($"{title} {artist}".Trim());
            var html = await Http.GetStringAsync($"https://www.youtube.com/results?search_query={query}");
            var match = System.Text.RegularExpressions.Regex.Match(html, "\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            var id = match.Success ? match.Groups[1].Value : null;
            _videoIds[key] = id;
            return id;
        }
        catch (Exception ex)
        {
            Log.Error("Video lookup failed", ex);
            return null;
        }
    }

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
            if (match.Success) _videoIds[key] = match.Groups[1].Value;
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
                        // hqdefault is 4:3 with black bars baked in around the 16:9 picture; cut them off.
                        if (name == "hqdefault" && bmp.PixelHeight * 4 == bmp.PixelWidth * 3)
                        {
                            int h = bmp.PixelWidth * 9 / 16;
                            var cropped = new CroppedBitmap(bmp, new Int32Rect(0, (bmp.PixelHeight - h) / 2, bmp.PixelWidth, h));
                            cropped.Freeze();
                            thumb = cropped;
                        }
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

            // Browsers/apps that don't publish real artwork make Windows hand us their app logo
            // (e.g. the Chrome logo, which can be 256px and up). Never show a logo: the island
            // shows the song's thumbnail or a clean placeholder instead.
            if (LooksLikeAppIcon(bitmap))
            {
                Log.Info($"Ignoring artwork that looks like an app logo ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
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

    /// <summary>
    /// Logos are tiny or have transparent surroundings; cover art and video thumbnails are opaque
    /// all the way to their edges.
    /// </summary>
    private static bool LooksLikeAppIcon(BitmapSource bitmap)
    {
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        if (Math.Min(w, h) < 64) return true;

        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int transparent = 0, samples = 0;
        var pixel = new byte[4];
        // Sample the border and just inside it (a round logo leaves its corners empty).
        for (int i = 0; i <= 8; i++)
        {
            int x = (w - 1) * i / 8, y = (h - 1) * i / 8;
            foreach (var (px, py) in new[] { (x, 0), (x, h - 1), (0, y), (w - 1, y), (x, h / 20), (w / 20, y) })
            {
                bgra.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
                samples++;
                if (pixel[3] < 200) transparent++;
            }
        }
        return transparent * 5 >= samples; // a fifth of the border is see-through: it's a logo
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
