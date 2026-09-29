# Dynamic Island for Windows

A Dynamic Island for Windows that sits at the top of your screen and looks like it came with the OS. It stays small until something is going on, then grows to show it.

> **Status:** v0.1 prototype. The base is in place; the features that go further than Apple's island come next.

## What it does today

- **Pill at the top-center** of the screen. It stays on top, doesn't take focus from your apps, and doesn't show in Alt+Tab or the taskbar.
- **Spring animations.** The pill changes size with a small bounce, and the content fades and scales in after it.
- **Now Playing from any app.** It reads the Windows media controls, so it works with Spotify, YouTube in any browser, Media Player, VLC and others.
  - Compact view: album art and animated bars tinted with a color taken from the artwork.
  - Hover to expand: title, artist, source app, a progress bar, and previous / play-pause / next.
  - When a new song starts, the island opens for a few seconds, like on iPhone.
- **Clock and date** when nothing is playing.
- **Hides during fullscreen** games, videos and presentations.
- **Tray icon:** hide/show, start with Windows, open the log folder, quit.

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
  App.xaml(.cs)          startup, single instance, tray icon
  IslandWindow.xaml(.cs) the island: layout, spring animation, states
  Controls/Equalizer.cs  the animated "now playing" bars
  Services/
    MediaService.cs      Windows media session watcher and controls
    ColorExtractor.cs    accent color from album art
    StartupManager.cs    "start with Windows"
    Log.cs               %LOCALAPPDATA%\DynamicIsland\log.txt
  Interop/NativeMethods.cs  Win32: overlay window style, topmost, fullscreen detection
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
- Multi-monitor support, themes, settings UI
- Auto-update
- Code signing (so Smart App Control and SmartScreen don't block the installer)
