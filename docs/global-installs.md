# Halo — what it puts on your machine

Everything Halo touches outside its own program folder, what removes it, and what it deliberately
leaves behind. Nothing global gets added without a row in this file.

Two ways to run Halo, two footprints:

- **Installed** (`Halo-Setup-<version>.exe`) — program files under `Program Files\Halo`, data under
  `%LOCALAPPDATA%\Halo`, two scheduled tasks, an ARP (Add/Remove Programs) entry.
- **Portable** (`Halo-<version>-win-x64.zip`, or a `dist\app` you built yourself with a
  `portable.marker` next to the exes) — everything including config and logs stays in the folder.
  No registry, no tasks, nothing to uninstall. Autostart is the only thing you give up.

Last updated 2026-09-13 for the v2 layout.

---

## 1. Added by the installer

| Item | Where | Created by | Removed by |
| --- | --- | --- | --- |
| Program files | `C:\Program Files\Halo` (three exes over one shared .NET runtime, `assets\`, `presentmon\`, `redist\`, licences) | Inno Setup `[Files]` | uninstall |
| Start menu shortcuts | `Halo Settings`, `Halo Widgets` | `[Icons]` | uninstall |
| ARP / uninstall entry | `HKLM\...\Uninstall\{D05EF3BB-...}_is1` | Inno Setup | uninstall |
| Collector autostart | scheduled task `\Halo\Collector` — RunLevel **Highest**, at logon of the interactive user | `Halo.Settings.exe --register-autostart` | `--unregister-autostart`, run by `[UninstallRun]` |
| Widgets autostart | scheduled task `\Halo\Widgets` — RunLevel **Limited** (medium integrity), at logon | same | same |
| Config + logs | `%LOCALAPPDATA%\Halo\config\*.json`, `%LOCALAPPDATA%\Halo\logs\*.log` | the apps, at first run | uninstall **asks**; keep them and a re-install picks up where you left off |
| PawnIO driver *(optional component, ticked by default)* | `C:\Program Files\PawnIO` + its own ARP entry and kernel service | `Halo.Settings.exe --install-pawnio` → `redist\PawnIO_setup.exe -install -silent` | **not removed** — see below |

Both tasks are created through the Task Scheduler API, not `schtasks`, with
`DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false`, `ExecutionTimeLimit=PT0S`,
`StartWhenAvailable=true`, `RestartCount=3` and priority 5
(`src/Halo.Settings/Services/AutostartManager.cs:182-188`). `schtasks /Create` defaults to stopping
on battery, which is why none of this goes through it.

There is **no** `HKCU\...\Run` value any more. `--register-autostart` deletes a leftover
`HaloWidgets` one from the pre-installer days if it finds it
(`AutostartManager.cs:254-268`).

### Why PawnIO is left behind

PawnIO is a **shared** kernel driver. FanControl, LibreHardwareMonitor and HWiNFO installs all use
the same one, and Halo has no way to know whether it was there first. Uninstalling Halo therefore
leaves it installed and says so. Remove it yourself from Settings → Apps if nothing else needs it.

## 2. Runtime-only, cleans itself up

| Thing | Lifetime |
| --- | --- |
| `Local\Halo.Metrics.v2` shared section, `Local\Halo.FramesReady.v2` event, `\\.\pipe\Halo.Control.v2` | die with the collector process |
| ETW sessions `HaloPMSvc`, `HaloTap`, the PCL Stats session | stopped by the collector; a stale one from a hard kill is cleaned at the next start |
| `PresentMonService.exe` | a plain console-mode **child process** of the collector — never registered with the Windows service manager, so it has no service entry to leave behind |
| LibreHardwareMonitor's global ISA-bus mutex | process lifetime |

## 3. Kept project-local on purpose (would normally be global)

For anyone building from source:

| Item | Normal location | Halo location |
| --- | --- | --- |
| NuGet package cache | `%USERPROFILE%\.nuget\packages` | `tools\nuget-cache` (via `nuget.config` `globalPackagesFolder`) |
| PresentMon SDK | `Program Files\Intel\PresentMon` | `tools\presentmon\sdk\`, committed to git (MIT), copied into the build output |
| PawnIO installer payload | — | `installer\redist\PawnIO_setup.exe`, downloaded by `tools\build.ps1` against a pinned SHA-256, gitignored |
| Build outputs | — | `dist\`, `bin\`, `obj\` (all gitignored) |

## 4. Full removal

1. **Settings → Apps → Halo → Uninstall** (or `unins000.exe` in the program folder). It stops both
   tasks, kills any `Halo.*` / `PresentMon*` process whose image path is inside the install folder,
   removes the tasks, deletes `Program Files\Halo`, and asks whether to delete
   `%LOCALAPPDATA%\Halo`.
2. PawnIO stays. Remove it from Settings → Apps if you want it gone and nothing else uses it.

Silent uninstall, if you need it:

```powershell
& "C:\Program Files\Halo\unins000.exe" /VERYSILENT /NORESTART
```

Built from source instead? `tools\uninstall-dev.ps1` does the same for a `dist\app` layout: stops
everything in the right order (widgets first — it owns the watchdog that restarts the collector),
then calls `--unregister-autostart`. It never touches `%LOCALAPPDATA%\Halo` or PawnIO.

## 5. Known limitation: which user the tasks belong to

The installer runs elevated, so Windows would normally hand it the elevating account. The tasks are
registered for the **interactive console user** instead, resolved at install time by
`WTSGetActiveConsoleSessionId` inside the `--register-autostart` verb
(`AutostartManager.cs:270-279`), which is right in the case that actually bites: a standard user
installing with an admin's credentials. If it still lands on the wrong account — multiple
simultaneous sessions, say — Settings → System check → **Repair autostart** re-registers them for
whoever is running it.
