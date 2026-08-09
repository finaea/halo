# Halo — Global Installs, Dependencies & New-PC Setup

**Policy:** everything for Halo lives inside this project folder (`c:\Users\final\Desktop\WIP\CodeProj\HALO\`).
**Relocating that folder:** follow [moving-the-project.md](private/moving-the-project.md) — the §3 table
below is the complete set of absolute paths, and `tools\install-halo.ps1` rewrites all of them.
Deleting the folder + running the uninstall script removes Halo completely. Anything that *must*
touch the system outside this folder is listed here — nothing global gets added without a row in
this file.

**Baseline audited:** 2026-07-19 · last updated 2026-07-20.

---

## 1. Dependencies (everything Halo needs to run)

### Bundled in the repo / build output — nothing to install

| Dependency | Where | Used for |
|---|---|---|
| .NET 9 runtime | published **self-contained** into `bin\Halo.*\` | no system .NET needed at run time |
| PresentMon 2 **service + SDK** (`PresentMonService.exe`, `PresentMonAPI2.dll`, Intel) | `tools\presentmon\sdk\` | frame capture, resolved lane (DISPLAYED panel, lows). Run as a plain console-mode **child process** of the collector — never registered as a Windows service |
| PresentMon 2 **console app** (`PresentMon-2.5.1-x64.exe`) | `tools\presentmon\` | fallback transport (`settings.PresentMonTransport`) |
| LibreHardwareMonitorLib 0.9.6 (NuGet) | project-local NuGet cache → build output | CPU/SuperIO/storage/GPU-extra sensors (needs the collector elevated for ring0 access) |
| Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5 (NuGet) | build output | own ETW sessions: present tap (`HaloTap`), NVIDIA Reflex/PCL latency markers |
| Vortice.* 3.8.3 (NuGet) | build output | Direct3D11 / Direct2D / DirectComposition / DXGI bindings for the widget renderer |
| 3 icon fonts (`ElegantIcons`, `MaterialIcons`, `SegMDL2`) | `assets\fonts\` | widget glyphs (copied from the Rainformer skin — personal use) |
| `halo.ico` | `assets\` | exe / window / tray icon |

### Expected on the machine

| Dependency | Needed for | If missing |
|---|---|---|
| Windows 11 x64 (Win10 21H2+ works) | everything | — |
| Administrator elevation for the **collector** | LHM ring0 sensors (CPU temp, fans, storage), ETW frame capture, Reflex markers | collector degrades gracefully: those metrics render "N/A", clock/disk-space/network still work |
| NVIDIA GPU + driver (for NVML) | GPU panel, DLSS/frame-gen detection, Reflex latency panel | metrics show N/A; everything else unaffected |
| **Trebuchet MS** font (ships with Windows) | widget text | any font can be set in Settings → General |
| .NET **SDK** 9 | **building from source only** | not needed if you copy a published `bin\` |
| Internet access, once, at first build | NuGet restore into `tools\nuget-cache` | not needed at run time (external-IP lookup is optional and off-path) |

### Explicitly NOT dependencies

- **PawnIO** (`C:\Program Files\PawnIO`) — belongs to **FanControl** on this machine; LHM coexists
  with it. ⚠️ Never uninstall it when removing Halo, and never install it for Halo on a new PC.
- HWiNFO, MSI Afterburner, RTSS, NVIDIA App overlay, Rainmeter — the stack Halo **replaces**;
  none are read or required (decommissioned on this machine 2026-07-20).

## 2. Pre-existing on this system (NOT ours, never remove)

| Item | Location | Owner | Notes |
|---|---|---|---|
| .NET SDK 9.0.301 + runtimes | `C:\Program Files\dotnet` | User (pre-existing) | used only to build; published output is self-contained |
| PawnIO driver | `C:\Program Files\PawnIO` | **FanControl** | see above |
| FanControl | `C:\Program Files (x86)\FanControl` | User | untouched; Halo is monitor-only, coexists via LHM's global ISA mutex |

## 3. Added by Halo — global registrations (binaries stay in the project folder)

| Item | Global footprint | Points to | Cleanup | Status |
|---|---|---|---|---|
| Collector autostart | Scheduled task `\Halo\Collector` (highest privileges, at logon) | `bin\Halo.Collector\Halo.Collector.exe` (Debug fallback), resolved by `tools\install-halo.ps1` | `schtasks /Delete /TN "\Halo\Collector" /F` (done by `tools\uninstall-halo.ps1`) | ☑ installed |
| Widgets autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `HaloWidgets` | `bin\Halo.Widgets\Halo.Widgets.exe` (Debug fallback), resolved by `tools\install-halo.ps1` | `reg delete HKCU\...\Run /v HaloWidgets /f` (done by `tools\uninstall-halo.ps1`) | ☑ installed |
| Windows Defender exclusion *(optional, off by default)* | exclusion path for the project folder | `<project>` | `Remove-MpPreference -ExclusionPath <project>` (done by uninstall) | opt-in via `install-halo.ps1 -AddDefenderExclusion` |

*PresentMon has **no** global footprint: both the service and the console app run as child
processes launched and killed by the collector (console mode — never registered with SCM).*

## 4. Kept project-local by design (would normally be global)

| Item | Normal location | Halo location |
|---|---|---|
| NuGet package cache | `%USERPROFILE%\.nuget\packages` | `tools\nuget-cache` (via `nuget.config` `globalPackagesFolder`) |
| App config + widget layouts | `%AppData%` | `config\` |
| Logs (7-day retention, 64 MB/session cap) | `%LocalAppData%` | `logs\` |
| PresentMon service + SDK + console binaries | `Program Files\Intel\PresentMon` | `tools\presentmon\` |
| Build outputs | — | `bin\`, `obj\` (git-ignored) |

## 5. Setting up Halo on a new PC

Everything except `bin\`, `logs\` and the NuGet cache is in git — including the PresentMon
binaries, fonts, icon, and `config\` (widget layout + settings travel with the repo).

**Option A — copy the working folder (no SDK needed):**
1. Copy the whole project folder (including `bin\`) to the new machine.
2. Run `tools\install-halo.ps1` (self-elevates): registers the collector scheduled task +
   widgets Run key and starts both. Done.

**Option B — from git (build machine):**
1. Install the .NET SDK 9.x (`winget install Microsoft.DotNet.SDK.9`).
2. Clone the repo; run `tools\publish-halo.ps1` (first run restores NuGet into
   `tools\nuget-cache` — needs internet once) → self-contained builds land in `bin\`.
3. Run `tools\install-halo.ps1`.

**After either option:**
- **Widget positions:** stored per monitor device id — on different monitor hardware, drag each
  widget where you want it once (drops save instantly, including which monitor).
- **Fans:** channel names / max RPM are motherboard-specific — re-map in Settings → General →
  Fans (channels are the SuperIO "System Fan #N" numbers).
- **Drives / network adapter:** re-tick in Settings → General.
- **Defender:** if a project file vanishes, check `Get-MpThreatDetection` — a 2026-07-19 ML
  false-positive (`Trojan:Win32/Bearfoos.A!ml`) once quarantined a csproj; restore from git and
  consider `install-halo.ps1 -AddDefenderExclusion`.
- **No NVIDIA GPU:** GPU/latency/DLSS panels show N/A — disable those widgets in Settings.

## 6. Full removal

1. Run `tools\uninstall-halo.ps1` — stops all processes (including PresentMon children),
   deletes the registrations in §3, verifies nothing global remains. Self-elevates.
2. Delete the folder.
3. Leave `C:\Program Files\dotnet`, PawnIO, FanControl untouched (pre-existing, §2).

*Runtime-only footprints that clean themselves: ETW sessions (`HaloPMSvc`, `HaloTap`, PCL —
stopped by the collector; stale ones from a hard kill are cleaned at next collector start),
`Local\Halo.Metrics.v1` shared memory + `Local\Halo.FramesReady.v1` event (vanish with the
processes), LHM ISA-bus mutex (process-lifetime).*
