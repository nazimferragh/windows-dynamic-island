using System;
using System.Runtime.InteropServices;

namespace DynamicIsland.Interop;

/// <summary>Reads and sets the Windows master output volume via Core Audio.</summary>
public static class SystemVolume
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float db);
        int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    private static Guid _iidEndpointVolume = typeof(IAudioEndpointVolume).GUID;
    private static Guid _empty = Guid.Empty;

    private static IAudioEndpointVolume? _cached;

    private static IAudioEndpointVolume? Endpoint()
    {
        if (_cached != null) return _cached;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // eRender = 0, eMultimedia = 1
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) return null;
            if (device.Activate(ref _iidEndpointVolume, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var o) != 0) return null;
            return _cached = (IAudioEndpointVolume)o;
        }
        catch (Exception ex)
        {
            Log_Error("Could not open the audio endpoint", ex);
            return null;
        }
    }

    /// <summary>Master volume as 0..1, or a sensible default if it can't be read.</summary>
    public static float Get()
    {
        try
        {
            var ep = Endpoint();
            if (ep != null && ep.GetMasterVolumeLevelScalar(out float level) == 0) return level;
        }
        catch (Exception ex) { Log_Error("Could not read the volume", ex); _cached = null; }
        return 0.5f;
    }

    public static void Set(float level)
    {
        try
        {
            Endpoint()?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _empty);
        }
        catch (Exception ex) { Log_Error("Could not set the volume", ex); _cached = null; }
    }

    public static bool GetMute()
    {
        try { var ep = Endpoint(); if (ep != null && ep.GetMute(out bool m) == 0) return m; }
        catch (Exception ex) { Log_Error("Could not read mute", ex); _cached = null; }
        return false;
    }

    public static void SetMute(bool mute)
    {
        try { Endpoint()?.SetMute(mute, ref _empty); }
        catch (Exception ex) { Log_Error("Could not set mute", ex); _cached = null; }
    }

    // Small indirection so this file has no compile dependency order issue with Services.Log.
    private static void Log_Error(string message, Exception ex) => Services.Log.Error(message, ex);
}
