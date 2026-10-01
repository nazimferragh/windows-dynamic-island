using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace DynamicIsland.Services;

public enum BtKind { Headphones, Speaker, Keyboard, Mouse, Phone, Computer, Gamepad, Other }

/// <summary>A Bluetooth device as the island lists it.</summary>
public sealed record BtDevice(string Id, string Name, BtKind Kind, bool Paired, bool Connected, bool IsLowEnergy, ulong Address);

/// <summary>What a device asks for while pairing.</summary>
public enum BtPairPrompt { ConfirmOnly, ConfirmPin, EnterPin, ShowPin }

/// <summary>
/// Bluetooth for the island's own Bluetooth view: on/off, paired devices (connect, disconnect,
/// forget), and nearby devices to pair, including the PIN step. Windows' Bluetooth APIs, same on
/// Windows 10 and 11. Connecting/disconnecting a paired classic device (headphones, speakers,
/// keyboards…) toggles its Bluetooth services, the standard way Windows tools do it.
/// </summary>
public static class BluetoothService
{
    private static Radio? _radio;
    private static DeviceWatcher? _watcher;
    private static readonly Dictionary<string, DeviceInformation> _nearby = new();

    /// <summary>Raised (on a background thread) when the list of nearby devices changes.</summary>
    public static event Action? NearbyChanged;

    public static bool Available => _radio != null;
    public static bool RadioOn => _radio?.State == RadioState.On;

    public static async Task<bool> InitAsync()
    {
        if (_radio != null) return true;
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return false;
            _radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return _radio != null;
        }
        catch (Exception ex)
        {
            Log.Error("Bluetooth unavailable", ex);
            return false;
        }
    }

    public static async Task SetRadioAsync(bool on)
    {
        if (_radio == null) return;
        try { await _radio.SetStateAsync(on ? RadioState.On : RadioState.Off); }
        catch (Exception ex) { Log.Error("Couldn't switch Bluetooth", ex); }
    }

    // ---------------------------------------------------------------- paired devices

    public static async Task<List<BtDevice>> GetPairedAsync()
    {
        var list = new List<BtDevice>();
        try
        {
            foreach (var info in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
            {
                using var dev = await BluetoothDevice.FromIdAsync(info.Id);
                if (dev == null) continue;
                list.Add(new BtDevice(info.Id, Name(dev.Name, info.Name), KindOf(dev.ClassOfDevice, dev.Name), true,
                    dev.ConnectionStatus == BluetoothConnectionStatus.Connected, false, dev.BluetoothAddress));
            }
            foreach (var info in await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)))
            {
                using var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (dev == null || list.Any(d => d.Address == dev.BluetoothAddress)) continue;
                list.Add(new BtDevice(info.Id, Name(dev.Name, info.Name), KindOf(dev.Appearance, dev.Name), true,
                    dev.ConnectionStatus == BluetoothConnectionStatus.Connected, true, dev.BluetoothAddress));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't list Bluetooth devices", ex);
        }
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    private static string Name(string a, string b) => !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : "Bluetooth device";

    // ---------------------------------------------------------------- nearby devices (to pair)

    private const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";

    /// <summary>Starts looking for devices in pairing mode; <see cref="Nearby"/> fills in live.</summary>
    public static void StartDiscovery()
    {
        if (_watcher != null) return;
        try
        {
            string aqs = $"(System.Devices.Aep.ProtocolId:=\"{ClassicProtocol}\" OR System.Devices.Aep.ProtocolId:=\"{LowEnergyProtocol}\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#False";
            _watcher = DeviceInformation.CreateWatcher(aqs, new[] { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" }, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, info) =>
            {
                if (string.IsNullOrWhiteSpace(info.Name)) return;
                lock (_nearby) _nearby[info.Id] = info;
                NearbyChanged?.Invoke();
            };
            _watcher.Updated += (_, update) =>
            {
                lock (_nearby) if (_nearby.TryGetValue(update.Id, out var info)) info.Update(update);
            };
            _watcher.Removed += (_, update) =>
            {
                lock (_nearby) _nearby.Remove(update.Id);
                NearbyChanged?.Invoke();
            };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't start Bluetooth discovery", ex);
            _watcher = null;
        }
    }

    public static void StopDiscovery()
    {
        try { if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted }) _watcher.Stop(); }
        catch { }
        _watcher = null;
        lock (_nearby) _nearby.Clear();
    }

    public static List<BtDevice> Nearby()
    {
        lock (_nearby)
            return _nearby.Values
                .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                .GroupBy(i => i.Name).Select(g => g.First())
                .Select(i => new BtDevice(i.Id, i.Name, KindOf(i.Name), false, false, i.Id.Contains("BluetoothLE", StringComparison.OrdinalIgnoreCase), 0))
                .OrderBy(d => d.Name).ToList();
    }

    /// <summary>
    /// Pairs with a nearby device. <paramref name="ask"/> is called when the device needs the user
    /// (confirm a PIN, type a PIN, or see one): return true to go ahead, plus the typed PIN if any.
    /// </summary>
    public static async Task<bool> PairAsync(BtDevice device, Func<BtPairPrompt, string, Task<(bool Ok, string Pin)>> ask)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(device.Id);
            var custom = info.Pairing.Custom;
            custom.PairingRequested += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    switch (args.PairingKind)
                    {
                        case DevicePairingKinds.ConfirmOnly:
                            args.Accept();
                            break;
                        case DevicePairingKinds.DisplayPin:
                            await ask(BtPairPrompt.ShowPin, args.Pin);
                            args.Accept();
                            break;
                        case DevicePairingKinds.ConfirmPinMatch:
                            if ((await ask(BtPairPrompt.ConfirmPin, args.Pin)).Ok) args.Accept();
                            break;
                        case DevicePairingKinds.ProvidePin:
                            var (ok, pin) = await ask(BtPairPrompt.EnterPin, "");
                            if (ok && pin.Length > 0) args.Accept(pin);
                            break;
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };
            var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin;
            var result = await custom.PairAsync(kinds, DevicePairingProtectionLevel.Default);
            Log.Info($"Bluetooth pair '{device.Name}': {result.Status}");
            return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't pair {device.Name}", ex);
            return false;
        }
    }

    public static async Task<bool> ForgetAsync(BtDevice device)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(device.Id);
            var result = await info.Pairing.UnpairAsync();
            Log.Info($"Bluetooth forget '{device.Name}': {result.Status}");
            return result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't forget {device.Name}", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------- connect / disconnect (classic devices)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public SYSTEMTIME LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothGetDeviceInfo(IntPtr radio, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothEnumerateInstalledServices(IntPtr radio, ref BLUETOOTH_DEVICE_INFO info, ref int count, [Out] Guid[]? services);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothSetServiceState(IntPtr radio, ref BLUETOOTH_DEVICE_INFO info, ref Guid service, int flags);

    // The services that carry a connection for audio and input devices.
    private static readonly Guid[] ConnectionServices =
    {
        new("0000110b-0000-1000-8000-00805f9b34fb"), // audio sink (A2DP: music)
        new("0000111e-0000-1000-8000-00805f9b34fb"), // hands-free (calls)
        new("00001108-0000-1000-8000-00805f9b34fb"), // headset
        new("0000110e-0000-1000-8000-00805f9b34fb"), // remote control (AVRCP)
        new("00001124-0000-1000-8000-00805f9b34fb"), // input (keyboards, mice, gamepads)
    };

    /// <summary>Connects (true) or disconnects (false) a paired classic device by switching its connection services.</summary>
    public static Task<bool> SetConnectedAsync(BtDevice device, bool connect) => Task.Run(() =>
    {
        if (device.IsLowEnergy || device.Address == 0) return false; // LE devices connect on their own
        try
        {
            var info = new BLUETOOTH_DEVICE_INFO { Size = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(), Address = device.Address, Name = "" };
            BluetoothGetDeviceInfo(IntPtr.Zero, ref info);

            // Only touch services the device actually has.
            int count = 0;
            BluetoothEnumerateInstalledServices(IntPtr.Zero, ref info, ref count, null);
            var installed = new Guid[Math.Max(count, 0)];
            if (count > 0) BluetoothEnumerateInstalledServices(IntPtr.Zero, ref info, ref count, installed);
            var targets = ConnectionServices.Where(g => !connect ? installed.Contains(g) : true).ToList();

            bool any = false;
            foreach (var service in targets)
            {
                var guid = service;
                int err = BluetoothSetServiceState(IntPtr.Zero, ref info, ref guid, connect ? 1 : 0);
                if (err == 0) any = true;
            }
            Log.Info($"Bluetooth {(connect ? "connect" : "disconnect")} '{device.Name}': {(any ? "done" : "no service changed")}");
            return any;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't {(connect ? "connect" : "disconnect")} {device.Name}", ex);
            return false;
        }
    });

    // ---------------------------------------------------------------- device types

    private static BtKind KindOf(BluetoothClassOfDevice cod, string name)
    {
        switch (cod.MajorClass)
        {
            case BluetoothMajorClass.AudioVideo:
                return cod.MinorClass is BluetoothMinorClass.AudioVideoLoudspeaker or BluetoothMinorClass.AudioVideoPortableAudio
                    ? BtKind.Speaker : BtKind.Headphones;
            case BluetoothMajorClass.Phone: return BtKind.Phone;
            case BluetoothMajorClass.Computer: return BtKind.Computer;
            case BluetoothMajorClass.Peripheral:
                var minor = cod.MinorClass.ToString();
                if (minor.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return BtKind.Keyboard;
                if (minor.Contains("Pointer", StringComparison.OrdinalIgnoreCase)) return BtKind.Mouse;
                if (minor.Contains("Gamepad", StringComparison.OrdinalIgnoreCase) || minor.Contains("Joystick", StringComparison.OrdinalIgnoreCase)) return BtKind.Gamepad;
                break;
        }
        return KindOf(name);
    }

    private static BtKind KindOf(BluetoothLEAppearance appearance, string name)
    {
        var category = appearance.Category;
        if (category == BluetoothLEAppearanceCategories.Phone) return BtKind.Phone;
        if (category == BluetoothLEAppearanceCategories.Computer) return BtKind.Computer;
        if (category == BluetoothLEAppearanceCategories.HumanInterfaceDevice)
        {
            if (appearance.SubCategory == BluetoothLEAppearanceSubcategories.Keyboard) return BtKind.Keyboard;
            if (appearance.SubCategory == BluetoothLEAppearanceSubcategories.Mouse) return BtKind.Mouse;
            if (appearance.SubCategory == BluetoothLEAppearanceSubcategories.Gamepad) return BtKind.Gamepad;
        }
        return KindOf(name);
    }

    /// <summary>A guess from the name when Windows doesn't say what the device is.</summary>
    private static BtKind KindOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("bud") || n.Contains("pod") || n.Contains("headphone") || n.Contains("headset") || n.Contains("ear") || n.Contains("wh-") || n.Contains("wf-")) return BtKind.Headphones;
        if (n.Contains("speaker") || n.Contains("jbl") || n.Contains("boom") || n.Contains("sound")) return BtKind.Speaker;
        if (n.Contains("keyboard") || n.Contains("keys")) return BtKind.Keyboard;
        if (n.Contains("mouse") || n.Contains("mx ") || n.Contains("trackpad")) return BtKind.Mouse;
        if (n.Contains("iphone") || n.Contains("galaxy") || n.Contains("pixel") || n.Contains("phone") || n.Contains("redmi")) return BtKind.Phone;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense")) return BtKind.Gamepad;
        return BtKind.Other;
    }
}
