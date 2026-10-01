using System;
using System.IO;
using System.Text.Json;

namespace DynamicIsland.Services;

/// <summary>
/// The user's choices from the Settings window, saved per user. Every change is saved and announced
/// (<see cref="Changed"/>) so the island applies it right away, without a restart.
/// </summary>
public sealed class AppSettings
{
    private static readonly string StorePath = Path.Combine(Log.LogDirectory, "settings.json");

    public static AppSettings Current { get; } = Load();

    // General
    public bool ShowOnAllMonitors { get; set; } = true;
    public int HoverDelayMs { get; set; } = 220;
    public bool HideOverFullscreen { get; set; } = true;
    public bool MatchWindowsColors { get; set; } = true;

    // Now playing
    public bool ShowSongPreview { get; set; } = true;
    public bool ShowClosedMedia { get; set; } = true;

    // Notifications
    public bool NotificationsInIsland { get; set; } = true;
    public bool HideWindowsBanners { get; set; } = true;
    public int NotificationSeconds { get; set; } = 5;

    // Black hole
    public bool BlackHoleEnabled { get; set; } = true;
    public bool AbsorbShortcutEnabled { get; set; } = true;

    // Downloads
    public bool DownloadsEnabled { get; set; } = true;
    public bool AnimateDownloadStart { get; set; } = true;
    public int KeepFinishedDownloadsMinutes { get; set; } = 3;

    // Pinned apps
    public bool PinnedAppsEnabled { get; set; } = true;

    /// <summary>Raised after any change was saved.</summary>
    public static event Action? Changed;

    /// <summary>Changes a setting, saves, and tells everyone.</summary>
    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        Current.Save();
        Changed?.Invoke();
    }

    /// <summary>Banners are only hidden while the island actually shows notifications (never leave the user with none).</summary>
    public bool ShouldHideBanners => NotificationsInIsland && HideWindowsBanners;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StorePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read settings; using defaults", ex);
        }
        return new AppSettings();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.LogDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save settings", ex);
        }
    }
}
