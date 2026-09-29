using System;
using System.IO;
using System.Linq;
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
                var art = await LoadThumbnailAsync(props.Thumbnail);
                _artKey = key;
                _art = art;
                _accent = ColorExtractor.Extract(art);
                if (version != _refreshVersion) return;
            }

            var timeline = session.GetTimelineProperties();
            Publish(new MediaSnapshot
            {
                Title = props.Title,
                Artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumTitle ?? "" : props.Artist,
                Source = PrettifySource(session.SourceAppUserModelId),
                Artwork = _art,
                Accent = _accent,
                IsPlaying = status == PlaybackStatus.Playing,
                Position = timeline.Position,
                Duration = timeline.EndTime - timeline.StartTime,
                PositionUpdatedAt = timeline.LastUpdatedTime,
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
            bitmap.DecodePixelWidth = 200;
            bitmap.StreamSource = buffer;
            bitmap.EndInit();
            bitmap.Freeze();
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
}
