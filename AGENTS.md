# Dynamic Island for Windows: notes for AI agents and contributors

A macOS-style dynamic island for Windows (WPF, .NET 9, per-monitor DPI v2). It shows now-playing,
notifications, downloads, and a "black hole" that windows can be dragged into. It's meant to feel
like part of Windows: it starts at sign-in, survives crashes and Task Manager kills, and runs on
every monitor.

## Rule 1: every feature must work on Windows 10 AND Windows 11

The minimum is Windows 10 2004 (build 19041); see `MinVersion` in `installer/DynamicIsland.iss` and
`TargetFramework` in the csproj. Before adding anything:

- **Only use APIs that exist on 19041**, or check the build at runtime and fall back gracefully
  (see `ShellTweaks.SnapBarBuild` for the pattern). Win11-only things include rounded DWM corners,
  Mica/Acrylic backdrops, the snap-layouts bar, and some notification-center UI. They must be
  optional; if one is missing, nothing may break.
- **Shell UI differs between versions** (notification center, taskbar, Start, drag-to-top snapping).
  Anything that automates or works around shell UI (e.g. `NotificationActivator`, `CursorFence`)
  needs a fallback path when the element isn't found.
- **No hard-coded screen geometry.** Monitors can have any size, orientation and scale (100–300%),
  mixed in one setup. Position in physical pixels from the monitor bounds, and re-center after
  DPI changes (Windows resizes the window *after* `OnDpiChanged`; see
  `IslandWindow.PositionOnMonitor` / `KeepCentered`).
- **Locale-independent.** Don't match UI text (Windows may be in French, etc.); match class names,
  process names, control types.
- CI (`.github/`) smoke-tests the installer on both Windows 10 and 11 code bases. Keep it passing.

## Product rules the owner has set (don't regress these)

- **Never show a browser/app logo as now-playing artwork** (Chrome hands Windows its 256 px logo).
  Always show the song's real thumbnail, including when the next song autoplays
  (`MediaService.LooksLikeAppIcon`, title-based YouTube thumbnail lookup).
- **Notifications appear only in the island**, not as Windows banners (`NotificationBanners` turns
  banners off per app and restores them on quit/uninstall). Clicking one in the island opens it
  like Windows would.
- **Always centered at the top middle of every screen**, on any PC.
- **Always running, whatever happens.** Layers, all Windows 10 + 11:
  - `AutoStartTask`: a Task Scheduler task: logon trigger (starts at sign-in, before Run-key
    apps) + a separate every-minute time trigger (revives the island even if every process was
    killed; extra launches exit at once via the single-instance mutex). Installer page lets the
    user pick **High priority** (task runs with highest rights, above-normal priority; one UAC
    prompt at install, like Wallpaper Engine) or **Normal** (no admin needed, same protection).
  - `Guardian`: a watchdog companion process; the island and it revive each other within
    seconds, and the watchdog restarts a frozen island (~15 s).
  - In-process crash handler relaunches immediately.
  - "Quit" (tray) sticks for the current Windows session (`AutoStartTask.RememberQuit`); opening
    the app by hand or restarting Windows brings it back. Installers stop it with `--quit`, which
    goes through the registry because a high-priority island can't be touched by a normal-rights
    installer.
  - Known limit: in high priority the island runs elevated, so Windows blocks drag-and-drop from
    Explorer onto it (pinning via the "+" picker still works). Don't "fix" this by relaxing
    Windows' message filtering (UIPI); that lowers a security boundary.
- **Always visible, like the iPhone's island** (owner's rule, 2026-10-02): it never disappears
  over maximized apps or full-screen games, and there is no bar behind it. Nothing is reserved at
  the top (`TopEdge`): apps use the whole screen and the island floats over them; a faint rim
  keeps it visible on dark apps. Hiding over full-screen apps is an opt-in setting, off by default.
- **Light on resources, especially in games** (`GameMode`): when a full-screen app (exclusive or
  borderless) is in front, the island stays but nothing animates, it only re-raises itself when the
  game really got above it, background checks slow to 4–30 s (register new timers with `GameMode.Tune`), and
  the process drops to below-normal priority. The hidden YouTube panel sleeps (`TrySuspendAsync`)
  unless it's playing audio. Never add always-running per-frame work; measured target in game
  mode: well under 1% of one core.
- **Feels native.** `WindowsTheme` reads the accent color (`UISettings`: the taskbar/Start accent,
  not the DWM title-bar one) and light/dark mode, live. `AppSettings` + `SettingsWindow` (Windows 11
  Settings look, Mica on 22621+, solid on Windows 10) control every feature, applied instantly.
  Opening the app again while it runs opens Settings. A Run entry (`--startup-entry`) lists the
  island in Settings › Apps › Startup / Task Manager; it only hands off to the task, and every
  automatic start honors that switch (`AutoStartTask.DisabledInWindowsStartup`).
- **A window comes back out on the screen whose island was clicked** (`WindowVault.BeginRestore`
  with `onWork`, `WindowApi.MovePlacementTo`), same relative spot and size; dragged out, it lands
  under the cursor. Keep the black hole smooth: no per-frame effects (shadow/blur) on the flying
  snapshot, and the snapshot of a dragged window is taken on a worker thread.
- **Eating is drag-and-drop, no hold** (owner's request, as in the preview): dragging a window
  near the island grows it; in the zone under it a "Let go to tuck it away" pill shows; releasing
  eats it at once. Nothing else may pop up over the island during that drag.
- **Snap layouts = edges and corners** (owner chose option C in the Black Hole Lab preview): no
  panel. Left/right edge → half, corner → quarter, top beside the island → full screen; the top
  middle is always the black hole's. White outline + dashed other half (`SnapPreview`), the window
  glides in, the most recent other window takes the other half (`SnapAutoFill`). Windows' own
  drag-to-edge docking is paused for the session while this is on (`EdgeSnapping`, never saved,
  given back on quit/crash/setting off; Win+arrows still work). Works on Windows 10 and 11.
- **Hover to open**: hovering Wi‑Fi, Bluetooth or Apps in the open island opens it at once (90 ms,
  only to ignore the pointer passing through; Settings waits 450 ms since it leaves the island).
  The owner wants it instant; don't add a visible countdown. A click right after a hover-open
  doesn't toggle the view shut. Switching views slides the new one in.
- **Black hole motion = "Morph", approved by the owner in the Black Hole Lab preview**
  (https://claude.ai/artifact/8qDiTu3jGKZ7iD1XnMgm26, style A, speed 1.05, bounce 0.60). The window
  slides and shrinks into the island's own pill shape, draining to black, and merges; the island
  opens 1.45×/1.55× while it works and gulps after. Coming out is the same path on an underdamped
  spring. The curves live in `Morph` (`AbsorbAnimation.cs`) and must stay identical to the
  preview. Owner rejected: orbit/accretion disk, sliced genie, mesh genie. For any change to this
  motion, update the preview first and get approval, then port.
- **Mac dock instead of the taskbar** (owner's request, approved in the Dock Lab preview
  https://claude.ai/artifact/L17cXCPHWhc4qd1YwJNSFt: Liquid Glass, size 56, magnification 1.6, spread
  2.6, follow 0.24, bounce 0.55). `DockWindow` (+ a blur window under the bar, `GlassBlur`), `DockModel`
  (kept apps in dock.json, first copied from the taskbar's pins; open apps grouped by AppUserModelID
  like the taskbar), `TaskbarHider` (auto-hide + hidden while the dock is on, always given back on
  quit/crash/uninstall/setting off), `LaunchpadWindow` (Apps grid), `DockMenu`. The dock reserves its
  strip (`AppBar`) so maximized windows end above it, hides over full-screen apps, and only animates
  while something moves. Apple's real icons are allowed for the owner's own use but never committed:
  they're loaded from `%LOCALAPPDATA%\DynamicIsland\DockIcons` (`DockIcons`). Apps are launched through
  explorer.exe so they never inherit the elevated island's admin rights.
- **Pinned apps rearrange like iOS**: hold (or drag) an app, the others jiggle and slide aside.
- **Status in the island** (`SystemStatus` + `StatusIcons`; the owner rejected a separate top bar and any strip behind the island). Wi‑Fi and Bluetooth are fully controlled inside the island (`WifiService`: join/disconnect/forget/password; `BluetoothService`: on/off, connect/disconnect/forget, discover + pair with PIN), never by sending the user to Windows' panels. The island's top row shows
  Wi‑Fi/Ethernet, Bluetooth, battery (exact %, live via `PowerManager` events). Wi‑Fi opens the
  island's own Wi‑Fi view (`WifiService`, `WifiPasswordWindow`), not Windows' flyout. Icons are our own drawings in the
  iOS style (don't ship Apple's artwork). Desktop PCs simply have no battery item.
- High-priority islands run elevated: tests that inject clicks from a normal-rights process can't
  click them (Windows UIPI). Test with an elevated helper or the open-settings-on-relaunch path.
- Animations must be smooth (driven per frame or with easing; no janky jumps or Windows'
  own snap previews over the island).

## Working on it

- Build and package: `.\build.ps1` → `dist\DynamicIsland-Setup-<version>.exe` (needs Inno Setup 6).
- Install silently to test the real thing: `dist\DynamicIsland-Setup-<v>.exe /VERYSILENT`, then run
  `%LOCALAPPDATA%\Programs\Dynamic Island\DynamicIsland.exe`. Logs: `%LOCALAPPDATA%\DynamicIsland\log.txt`.
- Verify UI changes on screen (screenshots) before calling them done.

## Testing helpers

- `DynamicIsland.exe --panel wifi|bluetooth|apps|player|close|restore` asks the running island to open a
  panel, or (`restore`) to bring the newest black-hole window back out, or (`absorb:<hwnd>`) to
  swallow a given window (use your own test window, never the owner's). Append `@N` to send it to the
  island on the Nth screen (e.g. `restore@1`). Works even when the island
  runs elevated and test clicks can't reach it.
- `DynamicIsland.exe --diag` logs what the Wi‑Fi and Bluetooth panels would list, then exits.
- Opening the app again while it runs opens Settings.
