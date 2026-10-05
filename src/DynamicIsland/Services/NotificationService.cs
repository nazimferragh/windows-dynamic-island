using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services;

public sealed class NotificationInfo
{
    public required uint Id { get; init; }
    public required string AppName { get; init; }
    /// <summary>The sending app's AUMID, used to open the app if the notification itself can't be.</summary>
    public string AppId { get; init; } = "";
    public required string Title { get; init; }
    public required string Body { get; init; }
    public BitmapSource? Icon { get; set; }
    public DateTime When { get; } = DateTime.Now;
}

/// <summary>
/// Reads Windows notifications (the same ones that go to the Action Center) and raises
/// <see cref="Received"/> for each new one, so the island can show it. Uses the
/// UserNotificationListener, which works unpackaged once the user allows notification access.
/// </summary>
public sealed class NotificationService
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly HashSet<uint> _seen = new();
    private readonly List<NotificationInfo> _recent = new();
    private UserNotificationListener? _listener;
    private UserNotificationListener? _bgListener;
    private volatile bool _polling;
    private bool _first = true;
    private int _pollCount;

    public event Action<NotificationInfo>? Received;

    /// <summary>The most recent notifications (newest first), capped.</summary>
    public IReadOnlyList<NotificationInfo> Recent => _recent;

    public async Task InitializeAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Log.Info($"Notification access not granted ({access}); the island won't show notifications");
                return;
            }
            // Only now that the island can read them is it safe to silence Windows' own pop-ups.
            if (AppSettings.Current.ShouldHideBanners) NotificationBanners.HideAll();
            // Polled on a background (MTA) thread with its own listener: objects fetched on the UI
            // thread are tied to it, and every one of them (dozens per poll) then has to be released
            // through a call back into the UI thread, which kept it waking up ~300 times a second.
            _bgListener = await Task.Run(() => UserNotificationListener.Current);
            _poll.Tick += (_, _) =>
            {
                // Apps that start sending notifications later get their pop-ups turned off too.
                if (++_pollCount % 10 == 0 && AppSettings.Current.ShouldHideBanners) NotificationBanners.HideAll();
                if (_polling) return;
                _polling = true;
                _ = Task.Run(PollAsync).ContinueWith(_ => _polling = false);
            };
            GameMode.Tune(_poll, TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(4));
            await Task.Run(PollAsync); // prime the "seen" set without animating existing ones
            _poll.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Notification listener init failed", ex);
        }
    }

    private async Task PollAsync()
    {
        if (_bgListener == null) return;
        IReadOnlyList<UserNotification> notifications;
        try
        {
            notifications = await _bgListener.GetNotificationsAsync(NotificationKinds.Toast);
        }
        catch (Exception ex)
        {
            Log.Error("Reading notifications failed", ex);
            return;
        }

        var current = new HashSet<uint>();
        var fresh = new List<UserNotification>();
        foreach (var n in notifications)
        {
            current.Add(n.Id);
            if (_seen.Add(n.Id) && !_first) fresh.Add(n);
        }
        // Forget ids that are gone so a re-posted notification can show again.
        _seen.RemoveWhere(id => !current.Contains(id));
        _first = false;

        foreach (var n in fresh)
        {
            try { if (AppSettings.Current.ShouldHideBanners) NotificationBanners.Hide(n.AppInfo?.AppUserModelId ?? ""); } catch { }
            var info = Parse(n);
            if (info == null) continue;
            info.Icon = await LoadIconAsync(n);
            _dispatcher.Invoke(() =>
            {
                _recent.Insert(0, info);
                if (_recent.Count > 20) _recent.RemoveAt(_recent.Count - 1);
                Received?.Invoke(info);
            });
        }
    }

    private static NotificationInfo? Parse(UserNotification n)
    {
        try
        {
            string appName = "", appId = "";
            try { appName = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
            try { appId = n.AppInfo?.AppUserModelId ?? ""; } catch { }

            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements();
            string title = "", body = "";
            if (texts is { Count: > 0 })
            {
                title = texts[0].Text ?? "";
                var parts = new List<string>();
                for (int i = 1; i < texts.Count; i++)
                    if (!string.IsNullOrWhiteSpace(texts[i].Text)) parts.Add(texts[i].Text);
                body = string.Join("  ", parts);
            }
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return null;

            return new NotificationInfo { Id = n.Id, AppName = appName, AppId = appId, Title = title, Body = body };
        }
        catch (Exception ex)
        {
            Log.Error("Parsing a notification failed", ex);
            return null;
        }
    }

    private static async Task<BitmapSource?> LoadIconAsync(UserNotification n)
    {
        try
        {
            var logo = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(48, 48));
            if (logo == null) return null;
            using var winrt = await logo.OpenReadAsync();
            using var stream = winrt.AsStreamForRead();
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = buffer;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
