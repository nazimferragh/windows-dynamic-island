using System;
using System.Runtime.InteropServices;

namespace DynamicIsland.Interop;

/// <summary>
/// The default communications microphone through Core Audio: its live input level (for the
/// island's level meter, read-only, nothing is recorded) and mute. Same on Windows 10 and 11.
/// </summary>
public static class Microphone
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
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out int count);
        int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float db);
        int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    private const int eCapture = 1, eCommunications = 2;
    private static Guid _iidMeter = typeof(IAudioMeterInformation).GUID;
    private static Guid _iidVolume = typeof(IAudioEndpointVolume).GUID;
    private static Guid _empty = Guid.Empty;

    private static IAudioMeterInformation? _meter;
    private static IAudioEndpointVolume? _volume;
    private static DateTime _lastOpen = DateTime.MinValue;

    private static bool Open()
    {
        if (_meter != null && _volume != null) return true;
        if (DateTime.UtcNow - _lastOpen < TimeSpan.FromSeconds(3)) return false; // no mic: don't retry every frame
        _lastOpen = DateTime.UtcNow;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(eCapture, eCommunications, out var device) != 0) return false;
            if (device.Activate(ref _iidMeter, 1, IntPtr.Zero, out var meter) == 0) _meter = (IAudioMeterInformation)meter;
            if (device.Activate(ref _iidVolume, 1, IntPtr.Zero, out var volume) == 0) _volume = (IAudioEndpointVolume)volume;
            return _meter != null && _volume != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Forget the device (e.g. the default microphone changed).</summary>
    public static void Reset()
    {
        _meter = null;
        _volume = null;
        _lastOpen = DateTime.MinValue;
    }

    /// <summary>Current input level, 0..1.</summary>
    public static float Level()
    {
        try
        {
            if (Open() && _meter!.GetPeakValue(out float peak) == 0) return peak;
        }
        catch
        {
            Reset();
        }
        return 0;
    }

    public static bool IsMuted()
    {
        try { return Open() && _volume!.GetMute(out bool muted) == 0 && muted; }
        catch { Reset(); return false; }
    }

    public static void SetMuted(bool muted)
    {
        try { if (Open()) _volume!.SetMute(muted, ref _empty); }
        catch { Reset(); }
    }
}
