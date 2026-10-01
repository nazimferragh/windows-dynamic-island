using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace DynamicIsland.Services;

/// <summary>A Wi‑Fi network as the island lists it.</summary>
public sealed record WifiNetwork(string Ssid, int Bars, bool Secured, bool Connected, WiFiAvailableNetwork Raw);

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
            var networks = _adapter.NetworkReport.AvailableNetworks;
            NamesHidden = networks.Count > 0 && networks.All(n => string.IsNullOrEmpty(n.Ssid));
            foreach (var group in networks.Where(n => !string.IsNullOrEmpty(n.Ssid)).GroupBy(n => n.Ssid))
            {
                var best = group.OrderByDescending(n => n.SignalBars).First();
                var auth = best.SecuritySettings.NetworkAuthenticationType;
                bool secured = auth is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None);
                result.Add(new WifiNetwork(best.Ssid, best.SignalBars, secured, best.Ssid == connected, best));
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
