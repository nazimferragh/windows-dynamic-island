using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>An app currently using the microphone.</summary>
public sealed record MicUse(string Key, string AppName, bool IsCall, DateTime Since, string? ExePath, string? PackageFamily);

/// <summary>
/// Which app is using the microphone right now (a call, a voice note, a recording), read from the
/// same per-app record Windows uses for its own microphone indicator. A call app, or any app also
/// using the camera, counts as a call. Re-reads when Windows changes those registry keys (a
/// background thread waits on them, so it costs nothing while idle) and every 30 s as a safety
/// net; raises <see cref="Changed"/> on the UI thread.
/// Windows 10 (1903+) and 11.
/// </summary>
public sealed class MicActivity
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    private static MicActivity? _current;
    public static MicActivity Current => _current ??= new MicActivity();

    private static readonly string[] CallApps =
    {
        "whatsapp", "teams", "discord", "zoom", "skype", "telegram", "signal", "messenger", "yourphone", "phone link",
        "slack", "webex", "viber", "facetime", "googlemeet", "line", "wechat",
    };

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _pollQueued;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool subtree, uint filter, Microsoft.Win32.SafeHandles.SafeWaitHandle evt, bool async);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app to show (a call wins over other microphone use), or null.</summary>
    public MicUse? Active { get; private set; }

    public event Action? Changed;

    private MicActivity()
    {
        _poll.Tick += (_, _) => Poll();
        Services.GameMode.Tune(_poll, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        _poll.Start();
        Poll();
        new System.Threading.Thread(WatchRegistry) { IsBackground = true, Name = "MicWatch", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Sleeps until Windows writes to the microphone/camera usage keys, then asks for a re-read.</summary>
    private void WatchRegistry()
    {
        try
        {
            using var mic = Registry.CurrentUser.OpenSubKey(Store + "microphone");
            using var cam = Registry.CurrentUser.OpenSubKey(Store + "webcam");
            if (mic == null) return;
            using var micChanged = new System.Threading.AutoResetEvent(false);
            using var camChanged = new System.Threading.AutoResetEvent(false);
            const uint filter = 0x1 | 0x4; // subkey added/removed, value changed
            if (RegNotifyChangeKeyValue(mic.Handle, true, filter, micChanged.SafeWaitHandle, true) != 0) return;
            if (cam != null) RegNotifyChangeKeyValue(cam.Handle, true, filter, camChanged.SafeWaitHandle, true);
            var events = cam != null ? new System.Threading.WaitHandle[] { micChanged, camChanged } : new System.Threading.WaitHandle[] { micChanged };
            while (true)
            {
                // Each registration fires once; re-arm only the one that fired.
                if (System.Threading.WaitHandle.WaitAny(events) == 0)
                    RegNotifyChangeKeyValue(mic.Handle, true, filter, micChanged.SafeWaitHandle, true);
                else
                    RegNotifyChangeKeyValue(cam!.Handle, true, filter, camChanged.SafeWaitHandle, true);
                System.Threading.Thread.Sleep(150); // Windows writes start/stop in a few steps
                if (_pollQueued) continue;
                _pollQueued = true;
                _dispatcher.InvokeAsync(() => { _pollQueued = false; Poll(); });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Can't watch microphone use; checking every 30 s instead", ex);
        }
    }

    private void Poll()
    {
        MicUse? next = null;
        try
        {
            var mic = InUse("microphone");
            // Windows sometimes never records the "stopped" time (an app that crashed or was killed):
            // only trust entries whose app is actually running.
            if (mic.Count > 0) mic = mic.Where(IsRunningCached).ToList();
            if (mic.Count > 0)
            {
                var camera = InUse("webcam").Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var uses = mic.Select(m => m with { IsCall = m.IsCall || camera.Contains(m.Key) }).ToList();
                next = uses.OrderByDescending(u => u.IsCall).ThenByDescending(u => u.Since).First();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read microphone use", ex);
        }

        if (next?.Key == Active?.Key && next?.IsCall == Active?.IsCall) return;
        if (next != null && Active == null) Interop.Microphone.Reset(); // pick up the current default mic
        Active = next;
        Changed?.Invoke();
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr p, ref int length, StringBuilder? name);

    // Whether an entry's app is running, remembered per entry: Windows keeps stale "in use" entries
    // for days, and checking every process each second (OpenProcess on hundreds of them) kept the
    // UI thread busy in the kernel. A verdict is redone when the entry changes (new start time),
    // after 5 s for a running app (it may have crashed), after 60 s for a stale one.
    private readonly Dictionary<string, (DateTime Since, bool Running, DateTime CheckedAt)> _running = new();

    private bool IsRunningCached(MicUse m)
    {
        var now = DateTime.UtcNow;
        if (_running.TryGetValue(m.Key, out var v) && v.Since == m.Since &&
            now - v.CheckedAt < (v.Running ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60)))
            return v.Running;
        bool running = m.ExePath != null ? IsExeRunning(m.ExePath)
            : m.PackageFamily != null && RunningApps().Families.Contains(m.PackageFamily);
        _running[m.Key] = (m.Since, running, now);
        return running;
    }

    /// <summary>Only opens the processes with that exe's name, not every process.</summary>
    private static bool IsExeRunning(string exePath)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (p)
            {
                var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size) && string.Equals(sb.ToString(), exePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                finally
                {
                    CloseHandle(h);
                }
            }
        }
        return false;
    }

    /// <summary>Exe paths and Store package families of everything running.</summary>
    private static (HashSet<string> Paths, HashSet<string> Families) RunningApps()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) paths.Add(sb.ToString());
                    int len = 0;
                    GetPackageFamilyName(h, ref len, null);
                    if (len > 0)
                    {
                        var fam = new StringBuilder(len);
                        if (GetPackageFamilyName(h, ref len, fam) == 0) families.Add(fam.ToString());
                    }
                }
                finally
                {
                    CloseHandle(h);
                }
            }
        }
        return (paths, families);
    }

    /// <summary>Apps whose last use of the capability hasn't ended (LastUsedTimeStop is 0).</summary>
    private List<MicUse> InUse(string capability)
    {
        var list = new List<MicUse>();
        using var root = Registry.CurrentUser.OpenSubKey(Store + capability);
        if (root == null) return list;
        foreach (var name in root.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var desktop = root.OpenSubKey(name);
                if (desktop == null) continue;
                foreach (var app in desktop.GetSubKeyNames())
                {
                    using var key = desktop.OpenSubKey(app);
                    if (IsActive(key, out var since))
                    {
                        var path = app.Replace('#', '\\');
                        if (string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(Make("np:" + app, NameForExe(path), since, path, null));
                    }
                }
            }
            else
            {
                using var key = root.OpenSubKey(name);
                if (IsActive(key, out var since)) list.Add(Make("pk:" + name, NameForPackage(name), since, null, name));
            }
        }
        return list;
    }

    private static bool IsActive(RegistryKey? key, out DateTime since)
    {
        since = DateTime.MinValue;
        if (key?.GetValue("LastUsedTimeStart") is not long start || start == 0) return false;
        if (key.GetValue("LastUsedTimeStop") is long stop && stop != 0) return false;
        since = DateTime.FromFileTimeUtc(start);
        return true;
    }

    private static MicUse Make(string key, string name, DateTime since, string? exe, string? package)
    {
        var lower = (name + " " + key).ToLowerInvariant();
        bool call = CallApps.Any(lower.Contains);
        return new MicUse(key, name, call, since, exe, package);
    }

    private string NameForExe(string path)
    {
        if (_names.TryGetValue(path, out var cached)) return cached;
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(info.ProductName)) name = info.ProductName.Trim();
        }
        catch { }
        return _names[path] = name;
    }

    /// <summary>"5319275A.WhatsAppDesktop_cv1g…" → "WhatsApp".</summary>
    private static string NameForPackage(string family)
    {
        var known = new (string Part, string Name)[]
        {
            ("WhatsApp", "WhatsApp"), ("MSTeams", "Teams"), ("MicrosoftTeams", "Teams"), ("YourPhone", "Phone Link"),
            ("SoundRecorder", "Sound Recorder"), ("WindowsSoundRecorder", "Sound Recorder"), ("Telegram", "Telegram"),
            ("Skype", "Skype"), ("Zoom", "Zoom"), ("Discord", "Discord"), ("Messenger", "Messenger"), ("Signal", "Signal"),
        };
        foreach (var (part, friendly) in known)
            if (family.Contains(part, StringComparison.OrdinalIgnoreCase)) return friendly;
        var name = family.Split('_')[0];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Replace("Desktop", "");
    }
}
