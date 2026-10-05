# Dynamic Island for Windows

A Dynamic Island for Windows that sits at the top of your screen and looks like it came with the OS. It stays small until something is going on, then grows to show it.

> **Status:** preview (v0.8). Works on Windows 10 (2004 and later) and Windows 11.

## What it does

- **A Mac-style notch on every monitor**, always centered at the top, sized for each monitor's scaling, with spring animations. It never takes focus and stays out of Alt+Tab and the taskbar.
- **Nothing sits under it, like on a Mac.** A thin band at the top of each screen is kept free (the way the taskbar keeps its space), so maximized apps start just below the island. While an app is maximized, the band takes the color of the app's own top edge, so the app seems to reach up to the island; over the desktop you see your wallpaper. Full-screen games and videos still use the whole screen, with the island floating on top.
- **Now playing from any app** (Spotify, YouTube in any browser, Media Player, VLC…): artwork and moving bars when closed, full controls, progress and volume when open. It always shows the song's real thumbnail, never a browser logo.
- **Search & play:** an embedded YouTube panel under the notch. Search, play, and tuck it away while the music keeps going.
- **Notifications in the island** instead of Windows' pop-ups, and **calls / voice recordings** (which app has the microphone, with a timer and a mute button).
- **Downloads:** a progress line in the closed island (with the percent when the size is known), and the list of recent files when open.
- **Wi‑Fi and Bluetooth** controlled from inside the island: join, disconnect, forget, pair.
- **Black hole for your windows:** drag a window onto the notch and let go, and it's tucked inside (hidden from the screen, taskbar and Alt+Tab while the app keeps running). Click it in the island to bring it back. Each virtual desktop has its own.
- **Snap to edges and corners:** drag a window to a side for half the screen, into a corner for a quarter, or to the top beside the island for full screen. Edges between two monitors work too: push on through to move the window to the other screen.
- **Pinned apps** in the island, rearranged like on an iPhone.
- **Always running:** starts at sign-in, comes back after a crash or a Task Manager kill, and "Quit" in the tray only lasts until the next restart.
- **Settings** in the Windows 11 style, following your accent color and light/dark mode.

## Light on your PC

It runs all day, often next to games, so it's built to stay out of the way:

- **Game mode:** when a full-screen game or video is in front, nothing animates, background checks slow down to every few seconds, and the island drops to below-normal priority, so the game always gets the CPU first.
- **No always-on mouse hook:** the low-level mouse hook used while dragging windows is only installed during the drag.
- **Small windows:** while closed, each island's window is just around the notch, so the music bars redraw ~12× fewer pixels.
- **The YouTube panel starts only when you open it**, sleeps when hidden, and is closed for real after a few minutes unused.
- **Event-driven** wherever Windows allows it (microphone use, window changes) instead of constant polling.

Measured on a 3-monitor PC (Windows 11, v0.8.0): about 1% of one CPU core in normal use, about 0.3% in game mode, around 200 MB of RAM, no GPU time of its own, and no disk writes while running.

## Install

Download `DynamicIsland-Setup-x.y.z.exe` from [Releases](../../releases) and run it (Next → Next → Install). You don't need to install .NET separately.

## Build from source

Requirements: [.NET 9 SDK](https://dotnet.microsoft.com/download) and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install Microsoft.DotNet.SDK.9 JRSoftware.InnoSetup`).

```powershell
dotnet run --project src/DynamicIsland      # run it
.\build.ps1                                 # build dist\DynamicIsland-Setup-<version>.exe
```

To release a version, push a tag such as `v0.3.0`. GitHub Actions builds the installer, signs it (see below) and attaches it to a GitHub Release.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Release builds are made only by the [GitHub Actions workflow](.github/workflows/release.yml) from the source code in this repository. Both the app (`DynamicIsland.exe`) and the installer are signed.
- Committers and reviewers: [@nazimferragh](https://github.com/nazimferragh)
- Approvers: [@nazimferragh](https://github.com/nazimferragh)

## Privacy

The island has no accounts, analytics or tracking. It only goes online for YouTube: when the playing app doesn't provide a real thumbnail, it searches YouTube for the song's title to get one, and for the Search & play panel, an embedded YouTube page you use like a browser (sign-in there is with Google, directly). Everything else it knows (the media that's playing, which windows are in the black hole, notifications, its log file in `%LOCALAPPDATA%\DynamicIsland`) stays on your PC.

## License

[MIT](LICENSE)

## Project layout

```
src/DynamicIsland/
  App.xaml(.cs)          startup, single instance, one island per monitor, tray, crash restart
  IslandWindow.xaml(.cs) the notch: shape geometry, spring animation, states, player, calendar
  Controls/Equalizer.cs  the animated "now playing" bars
  Services/
    MediaService.cs      Windows media session watcher and controls
    ColorExtractor.cs    accent color from album art
    WindowDragWatcher.cs notices windows being dragged (for the black hole)
    WindowVault.cs       absorbed windows: hide, restore, snapshots, crash-safe state
    Log.cs               %LOCALAPPDATA%\DynamicIsland\log.txt
  Overlays/                 the absorb animation and the drag-out ghost card
  Interop/NativeMethods.cs  Win32: overlay window style, placement, fullscreen detection, battery
  Interop/WindowApi.cs      Win32: inspecting, capturing, hiding and restoring other windows
installer/DynamicIsland.iss Inno Setup script
scripts/make-icon.ps1       regenerates assets/icon.ico
```

## Roadmap ideas

- Timers, alarms and a Pomodoro timer
- Volume and brightness indicators that replace the Windows flyouts
- Calendar: next meeting, with a one-click join
- Drag-and-drop file shelf
- Auto-update
