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
- Animations must be smooth (driven per frame or with easing; no janky jumps or Windows'
  own snap previews over the island).

## Working on it

- Build and package: `.\build.ps1` → `dist\DynamicIsland-Setup-<version>.exe` (needs Inno Setup 6).
- Install silently to test the real thing: `dist\DynamicIsland-Setup-<v>.exe /VERYSILENT`, then run
  `%LOCALAPPDATA%\Programs\Dynamic Island\DynamicIsland.exe`. Logs: `%LOCALAPPDATA%\DynamicIsland\log.txt`.
- Verify UI changes on screen (screenshots) before calling them done.
