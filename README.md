# Windows Ultimate

A transparent Windows 11 optimization suite (C# / .NET 10 / WPF). It scans the PC, explains what is
actually slowing it down, and applies only documented, reversible changes — after the user has reviewed them.

The goal: someone installs it, runs a scan, understands exactly what slows their PC down, and improves it
without risking breaking Windows.

## Principles (enforced in code, not just stated)

- **Nothing changes without consent.** Every change goes through one path (`TweakApplier` → consent dialog →
  engine). Moderate-risk and higher changes need an explicit acknowledgement; destructive operations
  (app removal, network reset, "undo all") need a second confirmation.
- **Everything is reversible or labelled as not reversible.** Before a change, the engine writes the original
  value to a write-ahead journal (`%LOCALAPPDATA%\WindowsUltimate\journal`), then applies, then verifies.
  A failure rolls back that tweak. Undo works per change, per session, or for everything — also after a crash
  or restart. Operations that cannot be undone (removing an app, emptying caches, resetting the network) are
  recorded in History as *Not reversible*, with how to recover (e.g. the Microsoft Store link).
- **Security is never weakened.** No tweak touches Defender, Firewall, UAC, SmartScreen, VBS/HVCI or Windows
  Update (a unit test checks the catalog for security-critical keys). The privacy page never edits the hosts
  file or firewall. Telemetry changes are refused on Insider builds, where they would break flighting.
- **No remote code.** No scripts are downloaded or executed. The only download is the app's own update
  (see below), and only after the user accepts it. Drivers and firmware are never downloaded, installed or
  flashed — the app links to official vendor pages and Windows Update.
- **Personal files are never auto-deleted.** Cleanup only targets caches, temp files and logs, shows the size
  and file count first, skips files in use, and keeps a history. Large files and duplicates found by the
  analyzer can only be sent to the Recycle Bin, one at a time, and the last copy of a duplicate is protected.
- **Runs as a standard user.** Analysis needs no admin rights. System-wide changes are disabled until the user
  restarts the app elevated.

## No invented FPS tweaks

Every tweak in `TweakCatalog.cs` has: what it changes (exact registry value / plan / task), why it helps,
the expected impact, disadvantages, restart requirement, how to undo it, a risk level and a source.
Recommendations depend on detected hardware (e.g. hardware GPU scheduling is only recommended on GPUs where
it matters; High performance is not recommended on laptops, X3D or hybrid Intel CPUs).

Popular "optimizer" tweaks that are placebo or harmful on Windows 11 (timer resolution, HPET, Nagle,
Win32PrioritySeparation, disabling SysMain, RAM cleaners, core parking, IRQ priority, disabling Spectre
mitigations/VBS/Defender…) are **not** applied — the Responsiveness page explains why for each one.

## Features

| Page | What it does |
|---|---|
| Dashboard | Hardware summary, live CPU/GPU/RAM, health score with its methodology and per-finding penalties, Quick Scan / Full Analysis (event logs, drivers, disk health), one-click *safe* recommendations (still reviewed). |
| Live monitor | CPU, GPU, RAM, disk, network, temperatures, top processes. Sampling runs only while visible; interval configurable. |
| Before / After | Synthetic benchmark (idle CPU, CPU single/multi, memory bandwidth, disk, thread wake latency) with noise thresholds, history and comparison. |
| Gaming | Analysis (driver age, refresh rate vs. max, Game Mode, HAGS, background recording, dual-channel, XMP), overlay detection, profiles (Smart / Competitive / Balanced / Max stability / Normal desktop) with "restore previous". |
| Responsiveness | Measured causes of lag, symptom → cause guide, evidence-based settings, myths explained. |
| Privacy | Reversible privacy settings + app-permission review linking to Windows Settings. |
| Debloat | AppX packages classified Safe / Optional / Advanced / Do not remove; frameworks and system packages are locked; preview + double confirmation. |
| Startup & processes | Startup entries (Task Manager's own enable/disable mechanism), third-party scheduled tasks, processes (only third-party processes can be ended, with confirmation). |
| Storage & cleanup | Drives, cleanup with preview, largest folders, large files, duplicate finder (content hash), installed programs, history. |
| Network | Router vs. internet latency/jitter/loss with diagnosis, Wi-Fi signal/band advice, DNS comparison (honest: DNS does not change game ping), flush DNS, network reset (double confirmation). |
| Repair | DISM / SFC / chkdsk `/scan` with explanation and live output, Windows Update services, pending updates, disk reliability counters, event-log problems. |
| Hardware & drivers | Components, driver versions/ages, problem devices with Device Manager codes, official driver sources. |
| History & undo | Every session and change with old → new values; undo change/session/all; export (zip); restore points as an extra safety net. |
| Settings | Language (follows Windows by default; English / French), restore-point default, data folder, principles and score methodology. |

## Design system

`Theme/Tokens.xaml` defines the materials (L1 atmosphere → L4 popovers), light (top-edge highlights, low-opacity
radial lights), shadows, typography and motion (180–300 ms ease-out). `Theme/Controls.xaml` builds every control from
those tokens and `Controls/Fx.cs` adds hover light/lift, page/toast entrances and loading skeletons. On Windows 11 22H2+
with transparency effects on, the window uses the native acrylic backdrop (DWM); otherwise it falls back to an opaque
background (*Settings → Window translucency*). Nothing animates continuously: live charts step once per sample (no slide-in, which made WPF recompose the window
many times a second), dials skip changes too small to see, and live sampling pauses while the window is minimized.

## Automatic updates

Every push to `master`/`main` runs `.github/workflows/release.yml`: it tests, publishes a self-contained
build, compiles the Inno Setup installer and creates a GitHub Release `v1.0.<run number>` with the
installer and its `.sha256` checksum. Release notes are generated from the commit messages.

When the app starts (Release builds only, and only if *Settings → Check for updates at startup* is on),
it asks the GitHub API for the latest release of this repository. If it is newer, the user sees the version,
size and notes and chooses **Update now** or not. On accept, the installer is downloaded from github.com,
its SHA-256 is checked against the published checksum (a mismatch deletes it), then it runs silently: the app
closes, is updated in place and reopens. Settings, the change journal and history are kept.
*Settings → Check now* does the same on demand. The repository must be public for this to work.

## Honest limitations

- **CPU temperature** is the ACPI thermal zone reported by the firmware; on many desktops it is not the core
  temperature or is unavailable. GPU temperature comes from NVML (NVIDIA only).
- **Startup impact** is estimated from the memory of the running process, not from boot tracing.
- **Restore points**: Windows allows one every 24 h by default; the app says so instead of failing silently.
  They are an extra layer — the journal is the primary undo mechanism.
- Synthetic benchmarks don't measure game FPS; they confirm a change didn't make things worse.

## Build & run

Requirements: Windows 11, .NET 10 SDK.

```bash
dotnet build WindowsUltimate.sln
```

```bash
dotnet test WindowsUltimate.sln
```

```bash
dotnet run --project src/WindowsUltimate.App
```

Installer (Inno Setup 6):

```bash
dotnet publish src/WindowsUltimate.App -c Release -r win-x64 --self-contained true -o publish/win-x64
```

```bash
iscc installer/WindowsUltimate.iss
```

Uninstalling keeps `%LOCALAPPDATA%\WindowsUltimate` (journal, logs, settings). Run **History → Undo all**
before uninstalling if you want every change reverted.

## Layout

```
src/WindowsUltimate.Core     engine, journal, catalog, diagnostics, monitoring, services (no UI)
src/WindowsUltimate.App      WPF shell, design system, view models, views, EN/FR strings
tests/WindowsUltimate.Tests  engine/journal/rollback tests (sandboxed in HKCU), catalog safety tests
installer/                   Inno Setup script
```

Key pieces: `Optimization/OptimizationEngine.cs` (apply → verify → journal → rollback),
`Optimization/Actions.cs` (registry, power plan, scheduled task actions with capture/restore),
`Optimization/TweakCatalog.cs` (all tweaks and their documentation), `Diagnostics/SystemAnalyzer.cs`
(scan and score), `Services/TweakApplier.cs` (the only UI path that changes the system).
