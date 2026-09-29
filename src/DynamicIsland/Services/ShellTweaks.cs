using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>Small Windows settings the island needs to feel built in.</summary>
public static class ShellTweaks
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string OurKey = @"Software\DynamicIsland";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>
    /// Windows 11 drops a "snap layouts" bar from the top-center of the screen when you drag a window
    /// to the top: exactly where the island's black hole is. Turn that one option off (Settings →
    /// System → Multitasking → "Show snap layouts when I drag a window to the top of my screen").
    /// Done once only, so if the user turns it back on we respect that.
    /// </summary>
    public static void DisableSnapBarOnDragToTop()
    {
        try
        {
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            if (ours.GetValue("SnapBarDisabled") is int done && done == 1) return;

            using var advanced = Registry.CurrentUser.CreateSubKey(AdvancedKey);
            advanced.SetValue("EnableSnapBar", 0, RegistryValueKind.DWord);
            ours.SetValue("SnapBarDisabled", 1, RegistryValueKind.DWord);

            // Tell Explorer the setting changed.
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "TraySettings", 0x0002, 1000, out _);
            Log.Info("Turned off Windows' snap layouts bar on drag-to-top (it appears where the island is)");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to turn off the snap layouts bar", ex);
        }
    }
}
