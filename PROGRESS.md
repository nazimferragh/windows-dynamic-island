# PROGRESS — Dynamic Island for Windows

**Any Claude session: read this file first. When the owner says "let's continue", resume from Now / Next.**

Rule: after every change that is committed, pushed and deployed, update this file in the same
session (adjust Now / Next, add one line at the top of the Done log) and commit + push it.

"Deployed" here means: installer rebuilt with `.\build.ps1` and installed on the owner's PC
(`dist\DynamicIsland-Setup-<version>.exe /VERYSILENT`, then the island relaunched). There is no
public release step yet.

---

## Now — the last thing we were working on

Session of 2026-10-01 (morning). Last shipped commit: `0f5f4b8` (pushed, installed on the
owner's PC as the current 0.7.0 build).

- **Game mode** (`0f5f4b8`, owner saw FIFA hitches + island vanishing): full-screen app in front →
  only that screen's island hides, no animations/topmost churn, slow polls, below-normal priority;
  YouTube panel sleeps when hidden + silent. Measured 4.3% → 0.63% of one core (test full-screen
  window). Also fixed: extra launches were giving Windows' snapping back. **Owner should re-test FIFA.**
  Idea not done: shrink the 760×370 island windows (each music-bar frame redraws the whole window).
- **Snap layouts = edges & corners** (`42b094c`, owner picked option C in the preview): halves at the
  side edges, quarters in corners, full screen at the top beside the island; drop panel removed;
  Windows' docking paused per session (verified SPI value 0 + log). **The drag itself was not
  test-driven** (owner was using the PC the whole time) — owner should try it.
- **Black hole optimized + per-screen restore** (`4312b52`): no per-frame shadow, background snapshot
  capture; a window clicked in an island comes back on that island's screen. Verified: restore@1
  moved the test window to the portrait screen; emerge ~120 fps (was 10–30).
- **Drop to eat** (`9a47ceb`): as in the preview, drag near → island grows → "Let go to tuck it away"
  → release eats at once. Snap layouts panel now only with **Shift** held during the drag.
  (Superseded: snap layouts are now edges & corners.)
- **Black hole = approved "Morph"** (`f50b88d`): prototyped in the Black Hole Lab artifact
  (https://claude.ai/artifact/8qDiTu3jGKZ7iD1XnMgm26); owner picked style A, speed 1.05, bounce 0.60,
  then it was ported 1:1 (`Morph` in AbsorbAnimation.cs). Verified by frame bursts (~60 fps). The
  genie/orbit versions below are superseded. New working rule: preview look changes in an artifact first.
- **Smooth genie** (`39bdbe1`): the funnel is one warped GPU mesh with rounded corners (the sliced version looked
  stepped/"broken Android" to the owner); verified by frame bursts, ~70 fps.
- **The island eats windows like a vacuum** (genie/funnel into the notch; the owner drew it:
  the black island itself should suck the app in). Replaces the orbiting accretion-disk version
  from `390334f`, which the owner didn't want. Island opens its mouth while eating, gulps after;
  coming out pours back down from the notch. Verified with frame bursts on a test window
  (absorb 49 frames/0.73 s, emerge 53 frames/0.65 s).
- **Hover opens instantly** (`390334f`): Wi‑Fi/Bluetooth/Apps 90 ms, Settings 450 ms (it leaves
  the island); first version (`6e01e57`) used 1.9 s + fill bar, owner said too slow. Views slide
  in; island opens with a snappier spring, default hover delay 0.12 s, no blur on the open view.
  Fast-hover not re-screenshotted (owner was using the PC); only the delay changed since the
  verified version.
- **Snap panel**: bigger tiles spread out from the hole, forgiving aim (column per tile, zone by
  position + hysteresis), aimed tile grows and shows auto-fill zones, spring outline with the
  layout's other zones, window glides into its zone. Panel/aim/outline verified by screenshot;
  **the glide itself was not run** (dropping on a zone would auto-fill the owner's windows).
- **Pinned apps rearrange like iOS** (hold or drag, others jiggle and slide). **Not click-tested**:
  the elevated island blocks test clicks — the owner must try it.

Earlier (2026-09-30 → 10-01, `a66dba9`):
- **YouTube panel (Search & play): account + full navigation.** Navigation row (Back, Forward,
  Home, Subscriptions, You, History, Playlists) and an account button (left-click: sign in /
  account; right-click: Switch account, Sign out). Panel enlarged to 880×640. State: done,
  deployed, navigation row verified by screenshot. **Google sign-in itself NOT tested** — Google
  sometimes blocks sign-in inside embedded browsers; the owner must try it.
- **Calls and voice recordings live in the island.** Reads which app holds the microphone from
  Windows' CapabilityAccessManager registry (the source of Windows' own mic indicator), only
  trusting apps that are actually running (Windows leaves stale entries — a crashed EA FC 26 showed
  as "in use" for 3 days). Closed island: green dot (call) / red dot (recording) + app + timer +
  live mic level bars; open island: a row with Mute mic and Open app. State: done, deployed,
  tested with a real mic (test script opened the mic, nothing recorded). Why this approach: it's the
  same data Windows uses, works on Windows 10 and 11, no hooks into other apps. Answering/hanging up
  calls is not possible for a third-party app.
- **YouTube ad blocker** (previous commit `26831fe`): strips the ad schedule from player data,
  skips/mutes leftovers, hides ad boxes, refuses ad servers. Done, deployed, script verified with
  Node and confirmed active in the page; not yet seen against a real video ad.

## Next — waiting on the owner

In order:
0. **Try the new things from 6e01e57**: drag an app in the Apps view left/right (hold or just
   drag); drop a window on a snap zone and watch it glide; absorb a window and click it on the
   shelf to see it come back out. Check the island opens fast and smooth enough on hover.
1. **Try YouTube sign-in** in the Search & play panel (Sign in, top right). Report if Google says
   "this browser or app may not be secure".
2. **Try the Wi‑Fi panel actions** (click the Wi‑Fi icon in the open island): Join, Disconnect,
   Forget, the password box for a new network. Not click-tested yet (elevated island blocks test clicks).
3. **Try the Bluetooth panel actions**: Connect / Disconnect the DualSense (least certain feature —
   uses BluetoothSetServiceState like other Windows tools), Forget, and Pair the HUAWEI WATCH GT 3
   (PIN box). Report which action fails, if any.
4. **Restart the PC once** to confirm the island appears at sign-in (scheduled task, high priority).
5. Optional: check Settings pages other than General (only General was screenshotted).
6. Decide whether **Google Chrome** (pinned during a test) stays pinned in the apps dock.

**Last unanswered question from Claude:** the repo is public and the YouTube ad blocker goes
against YouTube's terms — keep it on by default, make it off by default, or remove it from the
public version? (Also still open, lower priority: keep "Nazim Abderahman" as the exe publisher name,
or use "Nazim Ferragh" like the GitHub account / copyright line?)

## Ideas discussed, not started

- Bump the version (code still says 0.7.0 although several features shipped since) and publish a
  proper GitHub release / installer.
- Snap layouts: glide the real window into its zone (currently it jumps; only the outline animates).
- Drag-and-drop pinning while the island runs elevated (blocked by Windows UIPI; the fix would weaken
  a security boundary — see Context).
- A translucent full-width menu bar was built and then **removed at the owner's request** — status
  icons live inside the island instead. Don't bring the bar back.
- Making "kill every process" recover in <5 s: a Windows service and a third "sentinel" process were
  both **refused by the safety checks** (unkillable-persistence pattern). Don't revisit.

## Context worth knowing

- Read `AGENTS.md` (loaded via CLAUDE.md): Windows 10 + 11 rule, owner's product rules, architecture.
- Build: `.\build.ps1` → `dist\DynamicIsland-Setup-<version>.exe` (Inno Setup 6). Silent install keeps
  the owner's High-priority choice ("auto" mode). Logs: `%LOCALAPPDATA%\DynamicIsland\log.txt`.
- The installed island runs **elevated** (High priority): Windows blocks injected clicks from a
  normal-rights shell. Test with `DynamicIsland.exe --panel wifi|bluetooth|apps|search|player|close`,
  `--diag` (logs Wi‑Fi/Bluetooth data), and opening the exe again (opens Settings). Hover works.
- Kill-recovery tests need an elevated helper (UAC prompt the owner must accept).
- Verify UI on screen (CopyFromScreen screenshots, primary monitor 3440×1440, island at x≈1720).
  Before driving the mouse, check idle time (`GetLastInputInfo`); the owner often uses the PC.
  Drag tests must use their own topmost test windows (a test once absorbed the owner's CRM window;
  snap auto-fill once rearranged the owner's windows on the portrait monitor).
- Monitors: 3440×1440 primary, 1920×1200 at 125 %, 1440×2560 portrait. Mixed DPI and portrait
  layouts must keep working.
- Editing gotcha: in Bash heredocs passed to Python, `\\` collapses — prefer the Edit tool or a
  Python script written with the Write tool for code with backslashes.
- Safety boundaries hit this project: no unkillable persistence (service/sentinel), no relaxing UIPI
  message filtering. Respect them.
- Owner's style: wants decisive building, on-screen verification, prototypes (artifacts) for new UI
  ideas before building, and everything pushed to GitHub (`nazimferragh/windows-dynamic-island`).

## Done log (newest first)

"deployed" = installed on the owner's PC. Earlier entries (before 2026-09-30's session) were pushed;
their deployment state isn't recorded.

- 2026-10-01 · 0f5f4b8 · Game mode (per-screen hide, no animations, slow polls, low priority), sleeping YouTube panel, snapping-release fix · pushed, deployed
- 2026-10-01 · 42b094c · Snap layouts as edges & corners (option C), drop panel removed, Windows docking paused per session · pushed, deployed
- 2026-10-01 · 4312b52 · Smoother black hole (no per-frame shadow, background snapshot); windows come back on the clicked island's screen · pushed, deployed
- 2026-10-01 · 9a47ceb · Drop-to-eat like the preview; snap panel only with Shift · pushed, deployed
- 2026-10-01 · f50b88d · Black hole uses the owner-approved Morph animation (from the preview artifact) · pushed, deployed
- 2026-10-01 · 39bdbe1 · Genie funnel rendered as one smooth mesh (no slices), neck tied to the mouth · pushed, deployed
- 2026-10-01 · c13684c · Island sucks windows in like a vacuum (genie funnel into the notch) and pours them back out · pushed, deployed
- 2026-10-01 · 390334f · Real black-hole eat/spit animation (accretion disk spiral, spaghettification, redshift), instant hover-open, smoother island opening · pushed, deployed
- 2026-10-01 · 6e01e57 · Hover-to-open icons, new absorb + emerge animations, reworked snap panel with gliding windows, drag-to-rearrange apps · pushed, deployed
- 2026-10-01 · a66dba9 · YouTube account button + full navigation row; calls & mic recordings live in the island (timer, level, mute, open app) · pushed, deployed
- 2026-10-01 · 26831fe · Block and skip YouTube ads in the Search & play panel · pushed, deployed
- 2026-10-01 · 6cb9445 · MacBook-style notch: top corners flare out of the screen edge · pushed, deployed
- 2026-10-01 · 2950aed · Full Wi‑Fi (join/disconnect/forget/password) and Bluetooth (on/off, connect/disconnect/forget, pair with PIN) inside the island · pushed, deployed
- 2026-10-01 · 84b8d09 · Status icons + Wi‑Fi view inside the island, snap auto-fill, top bar removed · pushed, deployed
- 2026-10-01 · 0ea686b · v0.7.0: snap layouts in the island's drop panel, menu bar (later removed), portrait-aware layouts · pushed, deployed
- 2026-10-01 · 856f1c6 · CI: tolerate a missing Run registry key on fresh machines · pushed
- 2026-10-01 · b8aed4c · v0.6.0: Windows accent/light-dark colors, Settings window (Mica), Startup apps listing · pushed, deployed
- 2026-10-01 · 24dd324 · v0.5.0: always-running (scheduled task, watchdog, high priority), notifications in the island, pinned apps, downloads, many fixes · pushed, deployed
- 2026-09-30 · 33a43c6 · Rounded panel instead of concave-flared corners (reverted in spirit by 6cb9445) · pushed
- 2026-09-30 · 21a945d · Cleaner search bar, real thumbnails, volume, seek, downloads indicator · pushed
- 2026-09-30 · a112417 · Hide YouTube's own top bar in the panel; drive it from the island search box · pushed
- 2026-09-30 · 6a98321 · Ignore icon-sized artwork so the island never shows a browser logo · pushed
- 2026-09-30 · 9cce0d4 · In-island YouTube: Search & play panel (WebView2) · pushed
- 2026-09-30 · 42ead52 · Installer author welcome page; hide browser name for now-playing · pushed
- 2026-09-29 · 25f0f12 · One-click Windows Sandbox launcher to try the installer · pushed
- 2026-09-29 · 33e5e9d · Open source (MIT) and SignPath code signing in the release pipeline · pushed
- 2026-09-29 · a8436c1 · Automatic setup, per-desktop black hole, Windows 10 checks · pushed
- 2026-09-29 · d8fed45 · Mac-style menu bar strip, sticky across virtual desktops, no snap bar clash · pushed
- 2026-09-29 · f227d14 · Black hole: absorb windows into the island and pull them back out · pushed
- 2026-09-29 · 424f648 · macOS-style notch on every monitor · pushed
- 2026-09-29 · 2b38f25 · v0.1 prototype · pushed
