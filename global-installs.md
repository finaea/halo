# Halo — Global Installs Manifest

**Policy:** everything for Halo lives inside this project folder (`c:\Users\final\Desktop\WIP\StatsMonitor\`). Deleting the folder + running the cleanup script below removes Halo completely. Anything that *must* touch the system outside this folder is listed here — nothing global gets added without a row in this file.

**Baseline audited:** 2026-07-19.

---

## Pre-existing (found on system before Halo — NOT ours, never remove)

| Item | Location | Owner | Notes |
|---|---|---|---|
| .NET SDK 9.0.301 + runtimes | `C:\Program Files\dotnet` | User (pre-existing) | Halo builds with it; installs nothing. Widget/collector published **self-contained** into the project folder so no runtime dependency at run time |
| PawnIO driver | `C:\Program Files\PawnIO` | **FanControl** (installed it; depends on it) | Halo's LHM uses it too. ⚠️ **Do not uninstall when removing Halo** — FanControl breaks |
| FanControl | `C:\Program Files (x86)\FanControl` | User | Untouched; Halo is monitor-only and coexists via LHM's global ISA mutex |

## Added by Halo — global registrations (binaries stay in project folder)

Filled in as implementation proceeds; every row added here the moment it's created.

| Item | Global footprint | Points to | Cleanup command | Status |
|---|---|---|---|---|
| Intel PresentMon Service | Service registration `PresentMonService` (SCM entry only) | `<project>\tools\presentmon\PresentMonService.exe` | `sc.exe stop PresentMonService & sc.exe delete PresentMonService` | ☐ planned |
| Collector autostart | Scheduled task `\Halo\Collector` (highest privileges, at logon) | `<project>\bin\Halo.Collector.exe` | `schtasks /Delete /TN "\Halo\Collector" /F` | ☐ planned |
| Widgets autostart | Registry value `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `HaloWidgets` | `<project>\bin\Halo.Widgets.exe` | `reg delete HKCU\...\Run /v HaloWidgets /f` | ☐ planned |

## Kept project-local by design (would normally be global)

| Item | Normal location | Halo location |
|---|---|---|
| NuGet package cache | `%USERPROFILE%\.nuget\packages` | `<project>\tools\nuget-cache` (via `nuget.config` `globalPackagesFolder`) |
| App config + widget layouts | `%AppData%` | `<project>\config\` (portable mode) |
| Logs | `%LocalAppData%` | `<project>\logs\` |
| PresentMon service + SDK binaries | `Program Files\Intel\PresentMon` | `<project>\tools\presentmon\` |
| LibreHardwareMonitorLib, Vortice, etc. | NuGet global cache | project-local cache above |
| Build outputs | — | `<project>\bin\`, `<project>\obj\` (git-ignored) |

## Full removal procedure

1. Run `<project>\tools\uninstall-halo.ps1` (created in M1) — stops processes, deletes the three global registrations above, verifies nothing global remains.
2. Delete this folder.
3. Leave `C:\Program Files\dotnet`, `C:\Program Files\PawnIO`, FanControl untouched (pre-existing, see above).

*Runtime-only footprints that clean themselves: ETW session (only while PresentMon service runs), `Halo.Metrics.v1` shared memory (vanishes when processes exit), LHM ISA-bus mutex (namespace object, process-lifetime).*
