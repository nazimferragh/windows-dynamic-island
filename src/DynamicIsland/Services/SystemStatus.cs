using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace DynamicIsland.Services;

public enum NetworkKind { Offline, Wifi, Wired, Cellular }

/// <summary>
/// What the menu bar shows, kept live: battery (exact %, charging, time left), network (Wi‑Fi
/// signal, wired, offline), Bluetooth on/off, and the app in front. Windows pushes most changes
/// as events (battery changes arrive the moment the percentage moves); Wi‑Fi signal is re-read
/// every few seconds. Raises <see cref="Changed"/> on the UI thread. Windows 10 and 11.
/// </summary>
public sealed class SystemStatus
{
    private static SystemStatus? _current;

    /// <summary>Created on first use (on the UI thread), after all static lists are ready.</summary>
    public static SystemStatus Current => _current ??= new SystemStatus();

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(8) };
    private Radio? _bluetooth;

    // Battery
    public bool HasBattery { get; private set; }
    public int BatteryPercent { get; private set; }
    public bool PluggedIn { get; private set; }
    public bool Charging { get; private set; }
    public TimeSpan? TimeLeft { get; private set; }

    // Network
    public NetworkKind Network { get; private set; }
    public string NetworkName { get; private set; } = "";
    /// <summary>Wi‑Fi signal, 0..3 (iOS-style arcs).</summary>
    public int WifiLevel { get; private set; }

    // Bluetooth
    public bool HasBluetooth { get; private set; }
    public bool BluetoothOn { get; private set; }

    // App in front
    public string ActiveApp { get; private set; } = "";

    public event Action? Changed;

    private SystemStatus()
    {
        try
        {
            PowerManager.RemainingChargePercentChanged += (_, _) => Post(ReadBattery);
            PowerManager.BatteryStatusChanged += (_, _) => Post(ReadBattery);
            PowerManager.PowerSupplyStatusChanged += (_, _) => Post(ReadBattery);
            PowerManager.RemainingDischargeTimeChanged += (_, _) => Post(ReadBattery);
        }
        catch (Exception ex)
        {
            Log.Error("Battery events unavailable", ex);
        }
        try
        {
            NetworkInformation.NetworkStatusChanged += _ => Post(ReadNetwork);
        }
        catch (Exception ex)
        {
            Log.Error("Network events unavailable", ex);
        }
        _poll.Tick += (_, _) =>
        {
            ReadNetwork(); // Wi‑Fi signal strength has no change event
            ReadBattery();
            Changed?.Invoke();
        };
        GameMode.Tune(_poll, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30));
        _poll.Start();

        ReadBattery();
        ReadNetwork();
        _ = InitBluetoothAsync();
        WatchForegroundApp();
    }

    private void Post(Action read) => _dispatcher.InvokeAsync(() =>
    {
        read();
        Changed?.Invoke();
    });

    // ---------------------------------------------------------------- battery

    private void ReadBattery()
    {
        try
        {
            var status = PowerManager.BatteryStatus;
            HasBattery = status != BatteryStatus.NotPresent;
            BatteryPercent = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
            PluggedIn = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
            Charging = status == BatteryStatus.Charging;
            var left = PowerManager.RemainingDischargeTime;
            TimeLeft = !PluggedIn && left > TimeSpan.Zero && left < TimeSpan.FromDays(2) ? left : null;
        }
        catch
        {
            HasBattery = false;
        }
    }

    // ---------------------------------------------------------------- network

    private void ReadNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null || profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
            {
                Network = NetworkKind.Offline;
                NetworkName = "";
                WifiLevel = 0;
                return;
            }
            NetworkName = profile.ProfileName ?? "";
            if (profile.IsWlanConnectionProfile)
            {
                Network = NetworkKind.Wifi;
                try { NetworkName = profile.WlanConnectionProfileDetails.GetConnectedSsid(); } catch { }
                byte bars = profile.GetSignalBars() ?? 5; // 0..5
                WifiLevel = bars >= 4 ? 3 : bars >= 2 ? 2 : 1;
            }
            else
            {
                Network = profile.IsWwanConnectionProfile ? NetworkKind.Cellular : NetworkKind.Wired;
                WifiLevel = 0;
            }
        }
        catch
        {
            Network = NetworkKind.Offline;
        }
    }

    // ---------------------------------------------------------------- bluetooth

    private async Task InitBluetoothAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
            foreach (var radio in await Radio.GetRadiosAsync())
            {
                if (radio.Kind != RadioKind.Bluetooth) continue;
                _bluetooth = radio;
                _bluetooth.StateChanged += (_, _) => Post(ReadBluetooth);
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bluetooth status unavailable", ex);
        }
        _dispatcher.Invoke(() =>
        {
            ReadBluetooth();
            Changed?.Invoke();
        });
    }

    private void ReadBluetooth()
    {
        HasBluetooth = _bluetooth != null;
        BluetoothOn = _bluetooth?.State == RadioState.On;
    }

    // ---------------------------------------------------------------- app in front

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "SearchUI", "LockApp", "TextInputHost", "Widgets", "WidgetBoard", "ScreenClippingHost",
    };

    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private WinEventProc? _foregroundProc; // kept alive for the hook
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    private void WatchForegroundApp()
    {
        _foregroundProc = (_, _, hwnd, _, _, _, _) => _dispatcher.InvokeAsync(() =>
        {
            if (UpdateActiveApp(hwnd)) Changed?.Invoke();
        });
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0, 0 /* out of context */);
        UpdateActiveApp(GetForegroundWindow());
    }

    /// <summary>The app's name as people know it ("Google Chrome", "Discord"), from its exe's description.</summary>
    private bool UpdateActiveApp(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownPid) return false; // our own island/strip: keep showing the app the user was in
        string name;
        var path = ImagePath(pid);
        var exe = System.IO.Path.GetFileName(path);
        // Windows' own shell pieces (Start, search, notifications, quick settings, the taskbar) aren't
        // "the app you're using": keep showing the last real app. The desktop itself shows as Desktop.
        if (ShellProcesses.Contains(System.IO.Path.GetFileNameWithoutExtension(exe))) return false;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, 64);
        if (exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            var c = cls.ToString();
            if (c is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland") return false;
            if (c is "Progman" or "WorkerW")
            {
                if (ActiveApp == "Desktop") return false;
                ActiveApp = "Desktop";
                return true;
            }
        }
        if (exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Store apps live inside a frame host; their window title is the app name.
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            name = sb.ToString();
        }
        else if (!_appNames.TryGetValue(path, out name!))
        {
            name = "";
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                     : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : "";
            }
            catch { }
            if (name.Length == 0) name = System.IO.Path.GetFileNameWithoutExtension(exe);
            _appNames[path] = name;
        }
        if (name == ActiveApp) return false;
        ActiveApp = name;
        return true;
    }

    private static string ImagePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
