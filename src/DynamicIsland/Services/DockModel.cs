using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>An app window the dock knows about (what the taskbar would show a button for).</summary>
public sealed record AppWindow(IntPtr Hwnd, string Key, string ExePath, string Aumid, string ClassName, bool Minimized);

/// <summary>One icon in the dock: a kept app, or an open app that isn't kept.</summary>
public sealed class DockEntry
{
    /// <summary>Finder, Apps, Settings, Downloads, Trash: the dock's own items.</summary>
    public string Special { get; init; } = "";
    public PinnedApp? Pin { get; init; }
    public string Name { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string Aumid { get; init; } = "";
    public List<AppWindow> Windows { get; } = new();
    public bool Running => Windows.Count > 0;

    /// <summary>Same app across rescans (keeps animations attached to the right icon).</summary>
    public string Id => Special.Length > 0 ? "special:" + Special
        : Pin != null ? "pin:" + Pin.Kind + ":" + Pin.Target
        : "run:" + (Aumid.Length > 0 ? Aumid : ExePath).ToLowerInvariant();
}

/// <summary>
/// What the dock shows: the kept apps (stored per user, first filled from the taskbar's pinned
/// apps), plus every open app that isn't kept, each with its windows. Uses window properties that
/// exist on Windows 10 and 11 (AppUserModelID, package family), like the taskbar itself.
/// </summary>
public sealed class DockModel
{
    public const string Finder = "finder", AppsGrid = "apps", Settings = "settings", Downloads = "downloads", Trash = "trash";
    private const string ExplorerAumid = "Microsoft.Windows.Explorer";
    private const string SettingsAumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private static readonly string StorePath = Path.Combine(Log.LogDirectory, "dock.json");
    private readonly List<PinnedApp> _kept = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly WindowApi.WinEventProc _hookProc;
    private readonly List<IntPtr> _hooks = new();
    private readonly Dictionary<IntPtr, (string Key, string Exe, string Aumid)> _identity = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public DockModel()
    {
        Load();
        _hookProc = OnWinEvent;
        // Windows appearing, disappearing, minimizing or coming to the front: rescan shortly after.
        foreach (var (min, max) in new (uint, uint)[] { (0x8001, 0x8003), (0x0003, 0x0003), (0x0016, 0x0017) })
            _hooks.Add(WindowApi.SetWinEventHook(min, max, IntPtr.Zero, _hookProc, 0, 0, 0 /* out of context */));
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rescan(); };
        _poll.Tick += (_, _) => Rescan();
        GameMode.Tune(_poll, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));
        _poll.Start();
        Rescan();
    }

    public event Action? Changed;

    /// <summary>Left of the divider: kept apps then open ones. The dock adds Downloads and Trash itself.</summary>
    public List<DockEntry> Entries { get; private set; } = new();

    public IntPtr Foreground { get; private set; }

    public void Dispose()
    {
        foreach (var h in _hooks) if (h != IntPtr.Zero) WindowApi.UnhookWinEvent(h);
        _poll.Stop();
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only
        if (evt is 0x8002 or 0x8003 && WindowApi.GetRootWindow(hwnd) != hwnd) return;
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    // ---------------------------------------------------------------- kept apps

    public IReadOnlyList<PinnedApp> Kept => _kept;

    private void Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                _kept.AddRange(JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(StorePath)) ?? new());
                // File Explorer is Finder's job (older dock.json files copied the taskbar's pin).
                if (_kept.RemoveAll(IsExplorerPin) > 0) Save();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read the dock's apps", ex);
        }
        // First run: Finder, Apps and Settings, then whatever was pinned to the taskbar.
        _kept.Add(new PinnedApp { Kind = "special", Target = Finder, Name = "Finder" });
        _kept.Add(new PinnedApp { Kind = "special", Target = AppsGrid, Name = "Apps" });
        _kept.Add(new PinnedApp { Kind = "special", Target = Settings, Name = "System Settings" });
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(dir))
                foreach (var lnk in Directory.GetFiles(dir, "*.lnk").OrderBy(File.GetCreationTimeUtc))
                {
                    var app = FromShortcut(lnk);
                    if (app == null || IsExplorerPin(app)) continue;
                    _kept.Add(app);
                }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read the taskbar's pinned apps", ex);
        }
        Save();
    }

    private static bool IsExplorerPin(PinnedApp app) =>
        app.Kind != "special" && (string.Equals(app.Aumid, ExplorerAumid, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(app.ExePath).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase));

    private static PinnedApp? FromShortcut(string lnk)
    {
        string exe = "", aumid = "";
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(lnk));
            dynamic item = folder.ParseName(Path.GetFileName(lnk));
            try { aumid = item.ExtendedProperty("System.AppUserModel.ID") as string ?? ""; } catch { }
            try { exe = item.ExtendedProperty("System.Link.TargetParsingPath") as string ?? ""; } catch { }
            Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(shell);
        }
        catch { }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = "";
        return new PinnedApp { Kind = "file", Target = lnk, Name = Path.GetFileNameWithoutExtension(lnk), ExePath = exe, Aumid = aumid };
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.LogDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_kept, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save the dock's apps", ex);
        }
    }

    /// <summary>"Keep in Dock" for an open app that isn't kept yet.</summary>
    public void Keep(DockEntry entry)
    {
        if (entry.Pin != null || entry.Special.Length > 0) return;
        string target = entry.Aumid.Length > 0 && !entry.Aumid.Contains('\\') ? entry.Aumid : entry.ExePath;
        _kept.Add(new PinnedApp
        {
            Kind = entry.Aumid.Length > 0 && target == entry.Aumid ? "app" : "file",
            Target = target, Name = entry.Name, ExePath = entry.ExePath, Aumid = entry.Aumid,
        });
        Save();
        Rescan();
    }

    public void Remove(DockEntry entry)
    {
        if (entry.Pin == null || entry.Special == Finder) return;
        _kept.Remove(entry.Pin);
        Save();
        Rescan();
    }

    /// <summary>Moves a kept app to another place among the kept apps (Finder stays first).</summary>
    public void MoveTo(DockEntry entry, int index)
    {
        if (entry.Pin == null) return;
        int i = _kept.IndexOf(entry.Pin);
        if (i < 0) return;
        index = Math.Clamp(index, 1, _kept.Count - 1);
        if (i == index || i == 0) return;
        _kept.RemoveAt(i);
        _kept.Insert(index, entry.Pin);
        Save();
        Rescan();
    }

    /// <summary>Adds an installed app (from the Apps grid) to the dock.</summary>
    public void KeepInstalled(InstalledApp app)
    {
        if (_kept.Any(k => string.Equals(k.Target, app.Id, StringComparison.OrdinalIgnoreCase))) return;
        _kept.Add(new PinnedApp { Kind = "app", Target = app.Id, Name = app.Name, ExePath = app.ExePath, Aumid = app.Id.Contains('\\') ? "" : app.Id });
        Save();
        Rescan();
    }

    // ---------------------------------------------------------------- open windows

    public void Rescan()
    {
        Foreground = WindowApi.GetForegroundWindow();
        var windows = ListWindows();
        var entries = _kept.Select(k => k.Kind == "special"
            ? new DockEntry { Special = k.Target, Pin = k, Name = k.Name }
            : new DockEntry { Pin = k, Name = k.Name, ExePath = k.ExePath, Aumid = k.Aumid }).ToList();

        var extra = new Dictionary<string, DockEntry>();
        foreach (var w in windows)
        {
            var owner = entries.FirstOrDefault(e => Matches(e, w));
            if (owner == null)
            {
                if (!extra.TryGetValue(w.Key, out owner))
                {
                    owner = new DockEntry { Name = DisplayName(w), ExePath = w.ExePath, Aumid = w.Aumid };
                    extra[w.Key] = owner;
                }
            }
            owner.Windows.Add(w);
        }
        entries.AddRange(extra.Values);
        Entries = entries;
        Changed?.Invoke();
    }

    private static bool Matches(DockEntry e, AppWindow w)
    {
        switch (e.Special)
        {
            case Finder: return w.ClassName == "CabinetWClass";
            case Settings: return string.Equals(w.Aumid, SettingsAumid, StringComparison.OrdinalIgnoreCase);
            case "": break;
            default: return false;
        }
        var pin = e.Pin;
        if (pin != null)
        {
            if (pin.Aumid.Length > 0) return SameApp(pin.Aumid, w.Aumid);
            if (pin.Kind == "app" && string.Equals(pin.Target, w.Aumid, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return e.ExePath.Length > 0 && string.Equals(e.ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase)
            && (w.Aumid.Length == 0 || pin == null || pin.Aumid.Length == 0);
    }

    /// <summary>
    /// Browsers add the profile to their windows' id ("Chrome" pinned, "Chrome.UserData.Profile1"
    /// open); installed web apps add "_crx_" and are apps of their own.
    /// </summary>
    private static bool SameApp(string pinned, string window)
    {
        if (string.Equals(pinned, window, StringComparison.OrdinalIgnoreCase)) return true;
        if (!window.StartsWith(pinned + ".", StringComparison.OrdinalIgnoreCase)) return false;
        return !window.Substring(pinned.Length).Contains("_crx_", StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayName(AppWindow w)
    {
        if (w.ExePath.Length > 0 && !w.ExePath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(w.ExePath).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(w.ExePath);
        }
        var title = WindowApi.GetTitle(w.Hwnd);
        return title.Length > 0 ? title : "App";
    }

    private List<AppWindow> ListWindows()
    {
        var list = new List<AppWindow>();
        var alive = new HashSet<IntPtr>();
        foreach (var hwnd in WindowApi.TopLevelWindowsInZOrder())
        {
            if (!IsAppWindow(hwnd)) continue;
            alive.Add(hwnd);
            if (!_identity.TryGetValue(hwnd, out var id))
            {
                uint pid = WindowApi.GetProcessId(hwnd);
                string exe = WindowApi.GetProcessPath(pid) ?? "";
                string aumid = AppIds.ForWindow(hwnd);
                if (aumid.Length == 0) aumid = AppIds.ForProcess(pid);
                id = (aumid.Length > 0 ? aumid.ToLowerInvariant() : exe.ToLowerInvariant(), exe, aumid);
                // UWP frame windows only learn their app id once the app inside has loaded.
                if (aumid.Length > 0 || !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) _identity[hwnd] = id;
            }
            list.Add(new AppWindow(hwnd, id.Key, id.Exe, id.Aumid, WindowApi.GetClassName(hwnd), WindowApi.IsMinimized(hwnd)));
        }
        foreach (var gone in _identity.Keys.Where(h => !alive.Contains(h)).ToList()) _identity.Remove(gone);
        return list;
    }

    /// <summary>The same rules the taskbar uses for "gets a button".</summary>
    private bool IsAppWindow(IntPtr hwnd)
    {
        if (!WindowApi.IsWindowVisible(hwnd) || WindowApi.IsCloaked(hwnd)) return false;
        long ex = WindowApi.GetExStyle(hwnd);
        bool appWindow = (ex & 0x00040000) != 0;
        if (!appWindow && ((ex & 0x80) != 0 || (ex & 0x08000000) != 0)) return false; // tool / no-activate
        if (!appWindow && WindowApi.GetOwner(hwnd) != IntPtr.Zero) return false;
        if (WindowApi.GetTitle(hwnd).Length == 0) return false;
        if (WindowApi.GetProcessId(hwnd) == _ownPid) return false;
        string cls = WindowApi.GetClassName(hwnd);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow");
    }

    // ---------------------------------------------------------------- actions

    /// <summary>Click: bring the app forward (restoring minimized windows), or start it. Returns true when it was started.</summary>
    public bool Activate(DockEntry e)
    {
        try
        {
            if (e.Running)
            {
                var front = e.Windows[0].Hwnd;
                if (WindowApi.IsMinimized(front)) WindowApi.ShowWindow(front, 9 /* SW_RESTORE */);
                WindowApi.ForceForeground(front);
                return false;
            }
            switch (e.Special)
            {
                case Finder: Start("explorer.exe", ""); return true;
                case Settings: Shell("ms-settings:"); return true;
                case Downloads: Shell(DownloadsFolder()); return false;
                case Trash: Start("explorer.exe", "shell:RecycleBinFolder"); return false;
            }
            if (e.Pin is { } pin)
            {
                if (pin.Kind == "app") Start("explorer.exe", @"shell:AppsFolder\" + pin.Target);
                else Shell(pin.Target);
                return true;
            }
            if (e.ExePath.Length > 0) Shell(e.ExePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't open {e.Name}", ex);
            return false;
        }
    }

    public static void Quit(DockEntry e)
    {
        foreach (var w in e.Windows) WindowApi.PostClose(w.Hwnd);
    }

    public static void Hide(DockEntry e)
    {
        foreach (var w in e.Windows) WindowApi.ShowWindow(w.Hwnd, 6 /* SW_MINIMIZE */);
    }

    public static void ShowInExplorer(DockEntry e)
    {
        string path = e.Pin is { Kind: "file" } p ? p.Target : e.ExePath;
        if (path.Length > 0) Start("explorer.exe", $"/select,\"{path}\"");
    }

    public static string DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                if (s.Length > 0) return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private static void Start(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();

    private static void Shell(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}

/// <summary>AppUserModelIDs: how Windows tells apps apart (what groups taskbar buttons).</summary>
internal static class AppIds
{
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        int SetValue(ref PropertyKey key, ref PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Fmtid; public uint Pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(16)] private IntPtr _pad;
    }

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder? id);

    public static string ForWindow(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return "";
            try
            {
                var key = new PropertyKey { Fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 };
                if (store.GetValue(ref key, out var v) != 0) return "";
                string s = v.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.Pointer) ?? "" : "";
                PropVariantClear(ref v);
                return s;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return "";
        }
    }

    /// <summary>The app id of a packaged (Store/MSIX) process, or "".</summary>
    public static string ForProcess(uint pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            int len = 0;
            GetApplicationUserModelId(h, ref len, null);
            if (len <= 0) return "";
            var sb = new StringBuilder(len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : "";
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
