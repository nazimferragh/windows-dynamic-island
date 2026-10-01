using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace DynamicIsland.Services;

/// <summary>A Wi‑Fi network as the island lists it.</summary>
public sealed record WifiNetwork(string Ssid, int Bars, bool Secured, bool Connected, bool Saved, WiFiAvailableNetwork Raw);

public enum WifiJoinResult { Joined, NeedsPassword, WrongPassword, Failed }

/// <summary>
/// Wi‑Fi for the island's own Wi‑Fi view: scan, list, join (with a password when needed),
/// disconnect, and turn Wi‑Fi on/off. Uses Windows' Wi‑Fi API, the same on Windows 10 and 11.
/// Recent Windows versions only show nearby network names to apps that have location access;
/// <see cref="NamesHidden"/> tells the island to explain that instead of showing an empty list.
/// </summary>
public static class WifiService
{
    private static WiFiAdapter? _adapter;
    private static Radio? _radio;

    public static bool Available => _adapter != null;
    public static bool NamesHidden { get; private set; }
    public static bool RadioOn => _radio?.State != RadioState.Off;

    /// <summary>Finds the Wi‑Fi adapter (once). False if the PC has none or access was refused.</summary>
    public static async Task<bool> InitAsync()
    {
        if (_adapter != null) return true;
        try
        {
            if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed) return false;
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            _adapter = adapters.FirstOrDefault();
            if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
                _radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            return _adapter != null;
        }
        catch (Exception ex)
        {
            Log.Error("Wi‑Fi unavailable", ex);
            return false;
        }
    }

    /// <summary>Scans and returns nearby networks: the connected one first, then by signal. One entry per name.</summary>
    public static async Task<List<WifiNetwork>> ScanAsync()
    {
        var result = new List<WifiNetwork>();
        if (!await InitAsync() || _adapter == null) return result;
        try
        {
            await _adapter.ScanAsync();
            string connected = await ConnectedSsidAsync();
            var saved = SavedNetworks();
            var networks = _adapter.NetworkReport.AvailableNetworks;
            NamesHidden = networks.Count > 0 && networks.All(n => string.IsNullOrEmpty(n.Ssid));
            foreach (var group in networks.Where(n => !string.IsNullOrEmpty(n.Ssid)).GroupBy(n => n.Ssid))
            {
                var best = group.OrderByDescending(n => n.SignalBars).First();
                var auth = best.SecuritySettings.NetworkAuthenticationType;
                bool secured = auth is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None);
                result.Add(new WifiNetwork(best.Ssid, best.SignalBars, secured, best.Ssid == connected, saved.Contains(best.Ssid), best));
            }
            return result.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Bars).ThenBy(n => n.Ssid).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Wi‑Fi scan failed", ex);
            return result;
        }
    }

    private static async Task<string> ConnectedSsidAsync()
    {
        try
        {
            var profile = await _adapter!.NetworkAdapter.GetConnectedProfileAsync();
            return profile?.IsWlanConnectionProfile == true ? profile.WlanConnectionProfileDetails.GetConnectedSsid() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Joins a network. Without a password it works for open networks and ones Windows already
    /// knows; a secured, unknown one returns <see cref="WifiJoinResult.NeedsPassword"/>.
    /// </summary>
    public static async Task<WifiJoinResult> JoinAsync(WifiNetwork network, string? password = null)
    {
        if (_adapter == null) return WifiJoinResult.Failed;
        try
        {
            var result = password == null
                ? await _adapter.ConnectAsync(network.Raw, WiFiReconnectionKind.Automatic)
                : await _adapter.ConnectAsync(network.Raw, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
            Log.Info($"Wi‑Fi join '{network.Ssid}': {result.ConnectionStatus}");
            return result.ConnectionStatus switch
            {
                WiFiConnectionStatus.Success => WifiJoinResult.Joined,
                WiFiConnectionStatus.InvalidCredential => password == null && network.Secured ? WifiJoinResult.NeedsPassword : WifiJoinResult.WrongPassword,
                _ => network.Secured && password == null ? WifiJoinResult.NeedsPassword : WifiJoinResult.Failed,
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't join {network.Ssid}", ex);
            return WifiJoinResult.Failed;
        }
    }

    // ---------------------------------------------------------------- saved networks (Windows' Wi‑Fi profiles)

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanDeleteProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

    /// <summary>Runs <paramref name="work"/> for each Wi‑Fi interface with an open WLAN handle.</summary>
    private static void WithInterfaces(Action<IntPtr, Guid> work)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return;
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return;
            try
            {
                int count = Marshal.ReadInt32(list);
                // WLAN_INTERFACE_INFO_LIST: count, index, then entries of { GUID (16), name (512 bytes), state (4) } = 532 bytes.
                for (int i = 0; i < count; i++)
                {
                    var guidBytes = new byte[16];
                    Marshal.Copy(list + 8 + i * 532, guidBytes, 0, 16);
                    work(handle, new Guid(guidBytes));
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    /// <summary>The networks Windows remembers (names of saved profiles).</summary>
    public static HashSet<string> SavedNetworks()
    {
        var saved = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            WithInterfaces((handle, iface) =>
            {
                if (WlanGetProfileList(handle, ref iface, IntPtr.Zero, out var list) != 0) return;
                try
                {
                    int count = Marshal.ReadInt32(list);
                    // WLAN_PROFILE_INFO_LIST: count, index, then entries of { name (512 bytes), flags (4) } = 516 bytes.
                    for (int i = 0; i < count; i++)
                        saved.Add(Marshal.PtrToStringUni(list + 8 + i * 516) ?? "");
                }
                finally
                {
                    WlanFreeMemory(list);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read saved Wi‑Fi networks", ex);
        }
        return saved;
    }

    /// <summary>Forgets a saved network (its password is removed; joining again asks for it).</summary>
    public static bool Forget(string ssid)
    {
        bool done = false;
        try
        {
            WithInterfaces((handle, iface) =>
            {
                int err = WlanDeleteProfile(handle, ref iface, ssid, IntPtr.Zero);
                if (err == 0) done = true;
                else Log.Info($"Forget Wi‑Fi '{ssid}': error {err}");
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't forget {ssid}", ex);
        }
        return done;
    }

    public static void Disconnect()
    {
        try { _adapter?.Disconnect(); }
        catch (Exception ex) { Log.Error("Wi‑Fi disconnect failed", ex); }
    }

    public static async Task SetRadioAsync(bool on)
    {
        if (_radio == null) return;
        try { await _radio.SetStateAsync(on ? RadioState.On : RadioState.Off); }
        catch (Exception ex) { Log.Error("Couldn't switch Wi‑Fi", ex); }
    }
}
