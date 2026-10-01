using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace DynamicIsland.Services;

public sealed class DownloadItem
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public long Received { get; set; }
    public long? Total { get; set; }
    public bool Complete { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Where the file is on disk: the partial file while downloading, the real file once done.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>0..1 when the total is known, else null (indeterminate).</summary>
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;
}

/// <summary>
/// Surfaces active downloads for the island. Two sources:
///  - Any browser/app that writes a partial file to the Downloads folder (.crdownload/.part/…),
///    tracked by watching that folder and polling the file's size.
///  - The island's own in-panel browser (reported with an exact total via <see cref="Report"/>).
/// It can't see downloads that never touch the Downloads folder (e.g. a game/app self-updater).
/// </summary>
public sealed class DownloadWatcher : IDisposable
{
    private static readonly string[] PartialExtensions =
        { ".crdownload", ".part", ".partial", ".download", ".opdownload" };

    /// <summary>Finished downloads stay listed this long (Settings › Downloads), so there's time to click them.</summary>
    private static TimeSpan KeepCompleted => TimeSpan.FromMinutes(Math.Max(1, AppSettings.Current.KeepFinishedDownloadsMinutes));

    public static DownloadWatcher? Instance { get; private set; }

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, DownloadItem> _items = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly string _downloadsPath;
    private FileSystemWatcher? _fsw;

    public DownloadWatcher()
    {
        Instance = this;
        _downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        try
        {
            if (Directory.Exists(_downloadsPath))
            {
                _fsw = new FileSystemWatcher(_downloadsPath) { IncludeSubdirectories = false, EnableRaisingEvents = true };
                _fsw.Created += (_, e) => OnFileEvent(e.FullPath);
                _fsw.Changed += (_, e) => OnFileEvent(e.FullPath);
                _fsw.Renamed += OnRenamed;
                _fsw.Deleted += (_, e) => OnFileGone(e.FullPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not watch the Downloads folder", ex);
        }

        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    /// <summary>Active downloads plus recently-completed ones, newest first.</summary>
    public IReadOnlyList<DownloadItem> Items => _items.Values.OrderByDescending(i => i.CompletedAt ?? DateTime.MaxValue).ToList();

    public bool HasActive => _items.Values.Any(i => !i.Complete);

    public bool HasFinished => _items.Values.Any(i => i.Complete);

    /// <summary>Removes finished downloads from the list (the files themselves are untouched).</summary>
    public void ClearFinished()
    {
        var done = _items.Where(kv => kv.Value.Complete).Select(kv => kv.Key).ToList();
        if (done.Count == 0) return;
        foreach (var key in done) _items.Remove(key);
        Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>Report an in-island (WebView2) download; total may be 0/unknown.</summary>
    public void Report(string id, string name, long received, long total, bool complete, string filePath = "")
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (!_items.TryGetValue(id, out var item))
            {
                item = new DownloadItem { Id = id, Name = name };
                _items[id] = item;
            }
            item.Name = name;
            if (filePath.Length > 0) item.FilePath = filePath;
            item.Received = received;
            item.Total = total > 0 ? total : null;
            if (complete && !item.Complete) { item.Complete = true; item.CompletedAt = DateTime.Now; }
            Changed?.Invoke();
        });
    }

    private static bool IsPartial(string path) =>
        PartialExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string DisplayName(string partialPath) =>
        Path.GetFileNameWithoutExtension(partialPath); // "movie.mp4.crdownload" -> "movie.mp4"

    private void OnFileEvent(string path)
    {
        if (!IsPartial(path)) return;
        _dispatcher.InvokeAsync(() =>
        {
            if (!_items.TryGetValue(path, out var item))
            {
                item = new DownloadItem { Id = path, Name = DisplayName(path), FilePath = path };
                _items[path] = item;
            }
            item.Received = SafeSize(path);
            Changed?.Invoke();
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            // Partial renamed to another partial (Chrome: "Unconfirmed 123.crdownload" → "song.mp3.crdownload"):
            // still downloading, just under its real name now.
            if (_items.TryGetValue(e.OldFullPath, out var renamed) && IsPartial(e.FullPath))
            {
                _items.Remove(e.OldFullPath);
                renamed.Name = DisplayName(e.FullPath);
                renamed.FilePath = e.FullPath;
                _items[e.FullPath] = renamed;
                Changed?.Invoke();
                return;
            }
            // Partial file renamed to its final name = finished.
            if (_items.TryGetValue(e.OldFullPath, out var item))
            {
                item.Received = SafeSize(e.FullPath);
                item.Total = item.Received;
                item.Name = Path.GetFileName(e.FullPath);
                item.FilePath = e.FullPath;
                item.Complete = true;
                item.CompletedAt = DateTime.Now;
                _items.Remove(e.OldFullPath);
                _items[e.FullPath] = item;
                Changed?.Invoke();
            }
            else if (IsPartial(e.FullPath))
            {
                OnFileEvent(e.FullPath);
            }
        });
    }

    private void OnFileGone(string path)
    {
        _dispatcher.InvokeAsync(() =>
        {
            // Partial vanished (Chrome swaps it for the final file) = finished.
            if (_items.TryGetValue(path, out var item) && !item.Complete)
            {
                item.Complete = true;
                item.CompletedAt = DateTime.Now;
                item.Total ??= item.Received;
                // "movie.mp4.crdownload" gone: the finished file is "movie.mp4" next to it.
                item.FilePath = Path.Combine(Path.GetDirectoryName(path) ?? "", DisplayName(path));
                Changed?.Invoke();
            }
        });
    }

    private void Poll()
    {
        bool changed = false;
        foreach (var item in _items.Values)
        {
            if (item.Complete) continue;
            // Folder downloads: follow the partial file's size (it may have been renamed since it started).
            if (item.FilePath.Length > 0 && File.Exists(item.FilePath))
            {
                long size = SafeSize(item.FilePath);
                if (size != item.Received) { item.Received = size; changed = true; }
            }
        }

        // Drop completed items after a grace period (by their current key, which a rename may have changed).
        var stale = _items.Where(kv => kv.Value.Complete && kv.Value.CompletedAt is { } t && DateTime.Now - t > KeepCompleted)
            .Select(kv => kv.Key).ToList();
        foreach (var key in stale) { _items.Remove(key); changed = true; }

        if (changed) Changed?.Invoke();
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    public void Dispose()
    {
        _poll.Stop();
        _fsw?.Dispose();
        if (Instance == this) Instance = null;
    }
}
