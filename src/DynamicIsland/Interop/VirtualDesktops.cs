using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicIsland.Interop;

/// <summary>Which virtual desktop a window lives on, and which one the user is looking at.</summary>
internal static class VirtualDesktops
{
    [ComImport]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManagerClass
    {
    }

    private static IVirtualDesktopManager? _manager;

    private static IVirtualDesktopManager? Manager
    {
        get
        {
            try
            {
                return _manager ??= (IVirtualDesktopManager)new VirtualDesktopManagerClass();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>The desktop a (visible) window belongs to, or Guid.Empty if unknown.</summary>
    public static Guid GetWindowDesktop(IntPtr hwnd)
    {
        try
        {
            return Manager != null && Manager.GetWindowDesktopId(hwnd, out var id) == 0 ? id : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    /// <summary>
    /// The desktop currently shown, or Guid.Empty if unknown (e.g. only one desktop was ever used).
    /// Explorer records it in the registry: per user on Windows 11, per session on Windows 10.
    /// </summary>
    public static Guid GetCurrentDesktop()
    {
        const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
        return ReadGuid(Explorer + "VirtualDesktops")
            ?? ReadGuid(Explorer + $@"SessionInfo\{Process.GetCurrentProcess().SessionId}\VirtualDesktops")
            ?? Guid.Empty;
    }

    private static Guid? ReadGuid(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } bytes ? new Guid(bytes) : null;
        }
        catch
        {
            return null;
        }
    }
}
