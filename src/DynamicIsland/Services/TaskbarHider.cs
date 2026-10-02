using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// While the dock is on, the Windows taskbar steps aside: it's set to auto-hide (so it reserves no
/// space) and its windows are hidden on every monitor. Everything is given back when the dock is
/// switched off, the island quits or crashes, or the app is uninstalled; a registry flag remembers
/// the user's own auto-hide choice so even a restarted island restores it. Works on Windows 10 and 11.
/// </summary>
public static class TaskbarHider
{
    private const uint ABM_GETSTATE = 0x4, ABM_SETSTATE = 0xA;
    private const int SW_HIDE = 0, SW_SHOWNA = 8;
    private const string OurKey = @"Software\DynamicIsland";
    /// <summary>The taskbar's own state before we hid it, plus one (so 0 never means "unset").</summary>
    private const string FlagValue = "TaskbarStateBeforeDock";

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Interop.WindowApi.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

    private static readonly DispatcherTimer Keeper = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private static bool _hidden, _started;

    public static bool Hidden => _hidden;

    /// <summary>Hides the taskbar (when <paramref name="hide"/>) or gives it back.</summary>
    public static void Apply(bool hide)
    {
        try
        {
            if (hide) Hide();
            else Release();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't change the taskbar", ex);
        }
    }

    private static void Hide()
    {
        if (!_started)
        {
            _started = true;
            // Explorer shows its taskbar again after a restart (and sometimes with Start): hide it again.
            Keeper.Tick += (_, _) => { if (_hidden) HideWindows(); };
            GameMode.Tune(Keeper, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
        }
        using (var ours = Registry.CurrentUser.CreateSubKey(OurKey))
        {
            if (ours.GetValue(FlagValue) is not int)
            {
                var data = NewData();
                int before = (int)SHAppBarMessage(ABM_GETSTATE, ref data).ToUInt32();
                ours.SetValue(FlagValue, before + 1, RegistryValueKind.DWord);
                data.lParam = new IntPtr(before | 0x1 /* ABS_AUTOHIDE */);
                SHAppBarMessage(ABM_SETSTATE, ref data);
                Log.Info("Taskbar hidden while the dock is on");
            }
        }
        _hidden = true;
        HideWindows();
        Keeper.Start();
    }

    /// <summary>Gives the taskbar back exactly as it was (also after a crash or from the uninstaller).</summary>
    public static void Release()
    {
        _hidden = false;
        Keeper.Stop();
        using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
        if (ours.GetValue(FlagValue) is not int flag) return;
        var data = NewData();
        data.lParam = new IntPtr(Math.Max(0, flag - 1));
        SHAppBarMessage(ABM_SETSTATE, ref data);
        foreach (var bar in TaskbarWindows()) ShowWindow(bar, SW_SHOWNA);
        ours.DeleteValue(FlagValue, throwOnMissingValue: false);
        Log.Info("Taskbar given back");
    }

    private static void HideWindows()
    {
        foreach (var bar in TaskbarWindows())
            if (IsWindowVisible(bar)) ShowWindow(bar, SW_HIDE);
    }

    private static List<IntPtr> TaskbarWindows()
    {
        var list = new List<IntPtr>();
        var main = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);
        for (var w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); w != IntPtr.Zero;
             w = FindWindowEx(IntPtr.Zero, w, "Shell_SecondaryTrayWnd", null))
            list.Add(w);
        return list;
    }

    private static APPBARDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null),
    };
}
