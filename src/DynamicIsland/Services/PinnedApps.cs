using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DynamicIsland.Services;

/// <summary>A pinned app: an installed app (by its Apps-folder id) or any file/program path.</summary>
public sealed class PinnedApp
{
    /// <summary>"app": <see cref="Target"/> is a shell:AppsFolder id. "file": a path to open.</summary>
    public string Kind { get; set; } = "app";
    public string Target { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The program behind it when known, so a running copy can be brought to the front.</summary>
    public string ExePath { get; set; } = "";

    /// <summary>What the shell calls it, for icons.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ParsingName => Kind == "app" ? @"shell:AppsFolder\" + Target : Target;
}

/// <summary>An entry in the "pin an app" list.</summary>
public sealed record InstalledApp(string Name, string Id, string ExePath);

/// <summary>
/// The island's pinned apps: stored per user, launched through the shell. Everything here uses
/// shell features that exist on both Windows 10 and 11 (the Apps folder, ShellExecute).
/// </summary>
public sealed class PinnedApps
{
    private static readonly string StorePath = Path.Combine(Log.LogDirectory, "pinned-apps.json");
    private readonly List<PinnedApp> _apps = new();

    public event Action? Changed;

    public IReadOnlyList<PinnedApp> Apps => _apps;

    public PinnedApps()
    {
        try
        {
            if (File.Exists(StorePath))
                _apps.AddRange(JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(StorePath)) ?? new());
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read pinned apps", ex);
        }
    }

    public bool IsPinned(string kind, string target) =>
        _apps.Any(a => a.Kind == kind && string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase));

    public void Pin(PinnedApp app)
    {
        if (string.IsNullOrWhiteSpace(app.Target) || IsPinned(app.Kind, app.Target)) return;
        _apps.Add(app);
        Save();
    }

    public void PinInstalled(InstalledApp app) =>
        Pin(new PinnedApp { Kind = "app", Target = app.Id, Name = app.Name, ExePath = app.ExePath });

    /// <summary>Pins something dropped on the island: a shortcut, a program, or any file/folder.</summary>
    public void PinPath(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string exe = "";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".lnk") exe = ResolveShortcut(path);
        else if (ext == ".exe")
        {
            exe = path;
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
            }
            catch { }
        }
        if (string.IsNullOrEmpty(name)) name = path; // a drive root, e.g. "D:\"
        Pin(new PinnedApp { Kind = "file", Target = path, Name = name, ExePath = exe });
    }

    public void Unpin(PinnedApp app)
    {
        if (_apps.Remove(app)) Save();
    }

    /// <summary>Moves an app one place left (-1) or right (+1).</summary>
    public void Move(PinnedApp app, int direction)
    {
        int i = _apps.IndexOf(app), j = i + direction;
        if (i < 0 || j < 0 || j >= _apps.Count) return;
        (_apps[i], _apps[j]) = (_apps[j], _apps[i]);
        Save();
    }

    /// <summary>Moves an app to a position in the row (drag to reorder).</summary>
    public void MoveTo(PinnedApp app, int index)
    {
        int i = _apps.IndexOf(app);
        if (i < 0) return;
        index = Math.Clamp(index, 0, _apps.Count - 1);
        if (i == index) return;
        _apps.RemoveAt(i);
        _apps.Insert(index, app);
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.LogDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_apps, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save pinned apps", ex);
        }
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- launching

    /// <summary>Brings the app's window to the front if it's already running, otherwise starts it.</summary>
    public static void Launch(PinnedApp app)
    {
        try
        {
            if (app.ExePath.Length > 0 && WindowFinder.ActivateWindowOf(app.ExePath)) return;
            if (app.Kind == "app")
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + app.Target) { UseShellExecute = false })?.Dispose();
            else
                Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't open {app.Name}", ex);
        }
    }

    // ---------------------------------------------------------------- installed apps

    /// <summary>
    /// Everything in the Start menu's app list (desktop programs and Store apps alike), sorted by name.
    /// Reads the shell's Apps folder, so call it on an STA thread.
    /// </summary>
    public static List<InstalledApp> ListInstalled()
    {
        var result = new List<InstalledApp>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name ?? "", id = item.Path ?? "";
                if (name.Length == 0 || id.Length == 0) continue;
                string exe = "";
                try { exe = item.ExtendedProperty("System.Link.TargetParsingPath") as string ?? ""; } catch { }
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = "";
                result.Add(new InstalledApp(name, id, exe));
            }
            Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't list installed apps", ex);
        }
        return result
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string ResolveShortcut(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            string target = shortcut.TargetPath ?? "";
            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : "";
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>Finds a program's main window and brings it forward (restoring it if minimized).</summary>
internal static class WindowFinder
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref int length, StringBuilder? name);

    /// <summary>Brings forward the window of a Store app (by package family, e.g. WhatsApp).</summary>
    public static bool ActivateWindowOfPackage(string family)
    {
        var families = new Dictionary<uint, string>();
        string FamilyOf(uint pid)
        {
            if (families.TryGetValue(pid, out var f)) return f;
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            f = "";
            if (h != IntPtr.Zero)
            {
                int len = 0;
                GetPackageFamilyName(h, ref len, null);
                if (len > 0)
                {
                    var sb = new StringBuilder(len);
                    if (GetPackageFamilyName(h, ref len, sb) == 0) f = sb.ToString();
                }
                CloseHandle(h);
            }
            return families[pid] = f;
        }
        return Activate(pid => string.Equals(FamilyOf(pid), family, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ActivateWindowOf(string exePath)
    {
        var names = new Dictionary<uint, string>();
        return Activate(pid =>
        {
            if (!names.TryGetValue(pid, out var image)) names[pid] = image = ImagePath(pid);
            return string.Equals(image, exePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool Activate(Func<uint, bool> isTheApp)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(hwnd) == 0) return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true; // other desktop / hidden
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!isTheApp(pid)) return true;
            found = hwnd;
            return false; // top-most in z-order = the one used last
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return false;
        if (IsIconic(found)) ShowWindow(found, SW_RESTORE);
        // The island never takes focus, so Windows may refuse to hand the foreground to another app.
        // A tap of Alt lifts that lock (the standard workaround); Alt+Tab-style switching is the fallback.
        keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        if (!SetForegroundWindow(found) || GetForegroundWindow() != found) SwitchToThisWindow(found, true);
        return true;
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;

    private static string ImagePath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
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
