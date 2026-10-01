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
/// using the camera, counts as a call. Polls once a second; raises <see cref="Changed"/> on the UI thread.
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

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app to show (a call wins over other microphone use), or null.</summary>
    public MicUse? Active { get; private set; }

    public event Action? Changed;

    private MicActivity()
    {
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Poll();
    }

    private void Poll()
    {
        MicUse? next = null;
        try
        {
            var mic = InUse("microphone");
            // Windows sometimes never records the "stopped" time (an app that crashed or was killed):
            // only trust entries whose app is actually running.
            if (mic.Count > 0)
            {
                var running = RunningApps();
                mic = mic.Where(m => m.ExePath != null ? running.Paths.Contains(m.ExePath) : m.PackageFamily != null && running.Families.Contains(m.PackageFamily)).ToList();
            }
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
