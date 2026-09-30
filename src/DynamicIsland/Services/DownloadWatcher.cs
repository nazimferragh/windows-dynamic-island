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

    private static readonly TimeSpan KeepCompleted = TimeSpan.FromSeconds(5);

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

    public event Action? Changed;

    /// <summary>Report an in-island (WebView2) download; total may be 0/unknown.</summary>
    public void Report(string id, string name, long received, long total, bool complete)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (!_items.TryGetValue(id, out var item))
            {
                item = new DownloadItem { Id = id, Name = name };
                _items[id] = item;
            }
            item.Name = name;
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
                item = new DownloadItem { Id = path, Name = DisplayName(path) };
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
            // Partial file renamed to its final name = finished.
            if (_items.TryGetValue(e.OldFullPath, out var item))
            {
                item.Received = SafeSize(e.FullPath);
                item.Total = item.Received;
                item.Name = Path.GetFileName(e.FullPath);
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
            if (File.Exists(item.Id)) // folder items are keyed by path
            {
                long size = SafeSize(item.Id);
                if (size != item.Received) { item.Received = size; changed = true; }
            }
        }

        // Drop completed items after a short grace period.
        var stale = _items.Values.Where(i => i.Complete && i.CompletedAt is { } t && DateTime.Now - t > KeepCompleted).ToList();
        foreach (var i in stale) { _items.Remove(i.Id); changed = true; }

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
