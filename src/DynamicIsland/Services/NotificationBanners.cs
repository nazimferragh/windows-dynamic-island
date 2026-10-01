using System;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// Turns off Windows' own notification pop-ups (the banners in the corner) per app, so notifications
/// show only in the island. Notifications still land in the notification center, which is where the
/// island reads them from. Every app's original setting is remembered and put back on quit/uninstall.
/// </summary>
public static class NotificationBanners
{
    public const string RestoreArg = "--restore-banners";

    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string BackupKey = @"Software\DynamicIsland\HiddenBanners";
    private const string ShowBanner = "ShowBanner";
    /// <summary>Recorded for apps that had no ShowBanner value (Windows' default: shown).</summary>
    private const int NoValue = -1;

    /// <summary>Alarms and calls need their buttons on screen, so they keep their pop-ups.</summary>
    private static readonly string[] Keep = { "Microsoft.WindowsAlarms", "Calling", "Microsoft.YourPhone" };

    /// <summary>Hides the banner of every app Windows knows about. Cheap; safe to call repeatedly.</summary>
    public static void HideAll()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (settings == null) return;
            foreach (var app in settings.GetSubKeyNames()) Hide(app);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to hide notification banners", ex);
        }
    }

    /// <summary>Hides one app's banner (e.g. an app that just sent its first notification).</summary>
    public static void Hide(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || Array.Exists(Keep, k => appId.Contains(k, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            using var app = Registry.CurrentUser.CreateSubKey($@"{SettingsKey}\{appId}");
            var current = app.GetValue(ShowBanner);
            if (current is int v && v == 0) return;

            using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);
            if (backup.GetValue(appId) == null)
                backup.SetValue(appId, current is int original ? original : NoValue, RegistryValueKind.DWord);
            app.SetValue(ShowBanner, 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to hide the notification banner of {appId}", ex);
        }
    }

    /// <summary>Puts every app's pop-up setting back the way it was.</summary>
    public static void RestoreAll()
    {
        try
        {
            using var backup = Registry.CurrentUser.OpenSubKey(BackupKey);
            if (backup == null) return;
            foreach (var appId in backup.GetValueNames())
            {
                try
                {
                    using var app = Registry.CurrentUser.OpenSubKey($@"{SettingsKey}\{appId}", writable: true);
                    if (app == null) continue;
                    if (backup.GetValue(appId) is int original && original != NoValue)
                        app.SetValue(ShowBanner, original, RegistryValueKind.DWord);
                    else
                        app.DeleteValue(ShowBanner, throwOnMissingValue: false);
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to restore the notification banner of {appId}", ex);
                }
            }
            backup.Close();
            Registry.CurrentUser.DeleteSubKeyTree(BackupKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore notification banners", ex);
        }
    }
}
