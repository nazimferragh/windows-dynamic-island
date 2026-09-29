# Dynamic Island for Windows

A Dynamic Island for Windows that sits at the top of your screen and looks like it came with the OS. It stays small until something is going on, then grows to show it.

> **Status:** v0.1 prototype. The base is in place; the features that go further than Apple's island come next.

## What it does today

- **macOS-style notch** at the top-center of **every monitor**. It's attached to the top edge, with flared top corners and rounded bottom corners, and each one is sized for its monitor's scaling.
- **Spring animations.** The notch grows and shrinks with a small bounce. Content follows it in with a soft blur, fade and scale.
- **Behaves like part of Windows.** It always starts with Windows and has no Quit button (the tray menu only hides it). It restarts itself after a crash, never takes focus, and doesn't show in Alt+Tab or the taskbar.
- **Now Playing from any app.** It reads the Windows media controls, so it works with Spotify, YouTube in any browser, Media Player, VLC and others.
  - Closed: album art on the left and animated bars on the right, tinted with a color taken from the artwork.
  - Sneak peek: when a new song starts, the notch drops down briefly to show the title and artist.
  - Hover or click to open: large artwork, title, artist, a progress bar, and filled previous / play-pause / next controls.
- **Black hole for your windows.** Drag any window by its title bar toward the notch and it starts to glow. Hold it over the notch until it says "Release to absorb", then let go, and the window is pulled inside. It disappears from the screen, the taskbar and Alt+Tab, but the app keeps running. You can also press **Ctrl+Alt+Z** to throw the active window in.
  - Hover the island to see the absorbed windows as cards. **Click** a card to bring its window back where it was, or **drag the card out** to put the window wherever you drop it.
  - Safety net: absorbed windows come back if the island exits or crashes, and any window left hidden by an earlier run is restored at startup.
- **Week calendar** with today highlighted, and **battery** status on laptops.
- **A menu bar like the Mac's.** A thin strip at the top of each screen is reserved, the same way the taskbar reserves its space, so maximized apps sit below the island instead of under it. While an app is maximized, the strip turns black and the notch blends into it.
- **On every virtual desktop**, and it **hides on a monitor while an app is fullscreen on it** (videos, games, presentations).
- Windows 11's "snap layouts" bar, which drops down right where the island is when you drag a window to the top, is turned off once. Snap layouts still work from the maximize button and Win+Z.

## Install

Download `DynamicIsland-Setup-x.y.z.exe` from [Releases](../../releases) and run it (Next → Next → Install). You don't need to install .NET separately.

## Build from source

Requirements: [.NET 9 SDK](https://dotnet.microsoft.com/download) and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install Microsoft.DotNet.SDK.9 JRSoftware.InnoSetup`).

```powershell
dotnet run --project src/DynamicIsland      # run it
.\build.ps1                                 # build dist\DynamicIsland-Setup-<version>.exe
```

To release a version, push a tag such as `v0.1.0`. GitHub Actions builds the installer and attaches it to a GitHub Release.

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

- Notifications (Windows toasts) shown in the island
- Timers, alarms and a Pomodoro timer
- Volume and brightness indicators that replace the Windows flyouts
- Calendar: next meeting, with a one-click join
- Downloads, file copy progress, battery and charging
- Drag-and-drop file shelf
- Themes and a settings UI
- Auto-update
- Code signing (so Smart App Control and SmartScreen don't block the installer)
