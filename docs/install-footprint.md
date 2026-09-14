# Halo — global footprint and what ships inside the app

Two questions, answered separately:

1. **What does installing Halo put on the machine?** Section 1. Short list, and nothing is added
   without a row in it.
2. **What rides inside the app folder instead of being installed system-wide?** Section 2. The
   headline is that Halo carries its own .NET runtime — nothing installs .NET and nothing looks
   for one.

Two ways to run Halo, two footprints:

- **Installed** (`Halo-Setup-<version>.exe`) — program files under `Program Files\Halo`, data under
  `%LOCALAPPDATA%\Halo`, scheduled tasks, shortcuts, an Add/Remove Programs entry.
- **Portable** (`Halo-<version>-win-x64.zip`, or a `dist\app` built from source with a
  `portable.marker` next to the exes) — everything, config and logs included, stays in the folder.
  No registry, no tasks, nothing to uninstall. Autostart is the only thing given up.

---

## 1. Global footprint

Everything the installer adds outside the app folder.

| Item | Where | Created by | Removed by |
| --- | --- | --- | --- |
| Program files | `C:\Program Files\Halo` | Inno Setup `[Files]` | uninstall |
| Start-menu shortcuts | `Halo` (runs `Halo.Widgets.exe --start-collector` — the overlay plus a UAC prompt for the collector), `Halo Settings`, `Halo Widgets` | `[Icons]` | uninstall |
| Desktop shortcut *(optional, the "Create a desktop shortcut" checkbox)* | `Halo` on the all-users desktop, same target as the Start-menu `Halo` | `[Icons]` + `[Tasks] desktopicon` | uninstall |
| ARP / uninstall entry | `HKLM\...\Uninstall\{D05EF3BB-...}_is1` | Inno Setup | uninstall |
| Collector autostart | scheduled task `\Halo\Collector` — RunLevel **Highest**, at logon of the interactive user | `Halo.Settings.exe --register-autostart` | `--unregister-autostart`, run by `[UninstallRun]` |
| Widgets autostart | scheduled task `\Halo\Widgets` — RunLevel **Limited** (medium integrity), at logon | same | same |
| Config + logs | `%LOCALAPPDATA%\Halo\config\*.json`, `%LOCALAPPDATA%\Halo\logs\*.log` | the apps, at first run | uninstall **asks**; keeping them lets a re-install pick up where the last one left off |
| PawnIO driver *(optional component, ticked by default, not offered when any PawnIO is already installed)* | `C:\Program Files\PawnIO` + its own ARP entry and kernel service | `Halo.Settings.exe --install-pawnio` → `redist\PawnIO_setup.exe` | **not removed** — see below |

There is **no** `HKCU\...\Run` value. `--register-autostart` deletes a leftover `HaloWidgets` one
from the pre-installer days if it finds it
(`src/Halo.Settings/Services/AutostartManager.cs:290-304`).

### The two scheduled tasks

Both are created through the Task Scheduler COM API, not `schtasks`, with
`DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false`, `ExecutionTimeLimit=PT0S`,
`StartWhenAvailable=true`, `RestartCount=3` and priority 5
(`src/Halo.Settings/Services/AutostartManager.cs:220-226`). `schtasks /Create` defaults to
stopping a task when a laptop goes on battery, which is exactly the wrong behaviour for a monitor,
and is why none of this goes through it.

The task definition exists in exactly one place. The installer, `tools\install-dev.ps1` and the
System check page's **Repair autostart** button all shell out to the same
`Halo.Settings.exe --register-autostart` verb.

Uninstall only removes a task whose action points into the folder being uninstalled. A
`\Halo\Collector` registered by a build from source somewhere else is left alone.

### Why PawnIO is left behind

PawnIO is a **shared** kernel driver. FanControl, LibreHardwareMonitor and HWiNFO installs all use
the same one, and Halo has no way to know whether it was there first. Uninstalling Halo therefore
leaves it installed and says so on the way out. It can be removed from Settings → Apps if nothing
else needs it.

The same sharing is why the installer does not offer the component when any PawnIO is already
present, at any version: `PawnIO_setup.exe` refuses to install over an existing copy and exits
183 (`ERROR_ALREADY_EXISTS`).

### Known limitation: the Halo shortcut on a standard-user account

The `Halo` shortcut runs `Halo.Widgets.exe --start-collector`, which starts the collector via the
`runas` verb.

- **Logged in as an administrator** — Windows asks for consent, the collector runs as that same
  user, and both processes resolve the same `%LOCALAPPDATA%\Halo`. Everything is consistent.
- **Logged in as a standard user** — UAC asks for a *different* account's credentials. The
  collector then runs as that administrator, and `%LOCALAPPDATA%` resolves per user, so config and
  logs silently split in two.

The metrics still arrive either way: `Local\Halo.Metrics.v2` is scoped to the logon *session*, not
the user, and is created granting `GENERIC_READ` to Everyone
(`src/Halo.Metrics/NativeSection.cs:38`), so the panels show live numbers. It is settings and logs
that diverge — the collector ends up reading a `settings.json` the Settings app never writes.

Two ways around it, both already supported:

- **Portable mode** — a `portable.marker` next to the exes puts data in `<app folder>\data`, which
  is the same folder whoever runs it.
- **Autostart** — the scheduled tasks do not have this problem. They are registered with
  `TaskLogonInteractiveToken` for the interactive console user, resolved at install time by
  `WTSGetActiveConsoleSessionId` inside the `--register-autostart` verb
  (`AutostartManager.cs:306-315`), so the elevated collector is always the *same* user as the
  widgets. If they still land on the wrong account — multiple simultaneous sessions, say — System
  check → **Repair autostart** re-registers them for whoever is running it.

Documented rather than engineered around: the direct-UAC launch is what makes "click the shortcut,
approve the prompt" work with no scheduled task at all, and an administrator account — the common
case — is unaffected.

### Runtime-only, cleans itself up

| Thing | Lifetime |
| --- | --- |
| `Local\Halo.Metrics.v2` shared section, `Local\Halo.FramesReady.v2` event, `\\.\pipe\Halo.Control.v2` | die with the collector process |
| ETW sessions `HaloPMSvc`, `HaloTap`, the PCL Stats session | stopped by the collector; a stale one left by a hard kill is cleaned up at the next start |
| `PresentMonService.exe` | a plain console-mode **child process** of the collector — never registered with the Windows service manager, so there is no service entry to leave behind |
| LibreHardwareMonitor's global ISA-bus mutex | process lifetime |

---

## 2. What ships inside the app folder

### Halo brings its own .NET runtime

**Nothing installs .NET system-wide, and nothing looks for one.** `tools\build.ps1` publishes all
three executables `--self-contained` into one folder (`tools/build.ps1:155-161`), so the runtime
travels with the app.

The evidence is in the folder itself:

- Each `<name>.runtimeconfig.json` lists **`includedFrameworks`**, not `framework`. A
  framework-dependent app declares `framework` — a requirement for a runtime the machine must
  already have, and a hard launch failure if it does not. `includedFrameworks` declares the
  opposite: the runtime ships inside the folder. `Halo.Settings.runtimeconfig.json` lists two,
  `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App`, because it is the WPF one.
- The .NET host and runtime are ordinary files next to the exes: `hostfxr.dll`, `hostpolicy.dll`,
  `coreclr.dll`, `clrjit.dll`, `System.Private.CoreLib.dll` and the rest of the framework
  assemblies.

That is what the size is. A published `dist\app` is roughly **300 files and ~175 MB**, of which
Halo's own three executables and their assemblies are about **2.6 MB** — measured on a 10.0.12
runtime, and `tools\build.ps1` prints the exact count and size at the end of every build.
Everything else is the runtime, WPF, and the third-party libraries listed in
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). Publishing is ReadyToRun by default, which
`tools\build.ps1` puts at ~14 MB of that in exchange for faster cold start; `-NoReadyToRun` turns
it off.

All three exes are published into the *same* folder so they share one copy of the runtime rather
than carrying three. Each keeps its own `.deps.json` and `.runtimeconfig.json`, so nothing
collides, and the runtime files themselves are byte-identical because `global.json` pins the SDK.

A `dotnet build` during development is framework-dependent (`Directory.Build.props` sets
`SelfContained=false`), so it does need a .NET 10 runtime on the machine — but nothing that ships
is built that way.

### The rest of the payload

Everything Halo reads at run time is resolved from the folder the exe sits in, through
`Halo.Shared.Paths` — never by walking up a tree looking for the repo. An installed copy under
Program Files and a dev build under `src\…\bin\Debug` resolve identically.

| Inside the app folder | What | Read by |
| --- | --- | --- |
| `presentmon\PresentMonAPI2.dll`, `PresentMonService.exe` | bundled Intel PresentMon 2 SDK (MIT) | the frame pipeline — `Paths.PresentMonDir` |
| `assets\fonts\*.ttf` | `ElegantIcons.ttf`, the one font Halo ships | the renderer's private font collection — `Paths.FontsDir` |
| `assets\halo.ico` | window and tray icon | all three processes |
| `redist\PawnIO_setup.exe` | the PawnIO installer payload | `Halo.Settings.exe --install-pawnio`, so System check can offer it later — `Paths.RedistDir` |
| `LICENSE`, `NOTICE.md`, `THIRD-PARTY-NOTICES.md` | licences | humans |

### Portable mode

Config and logs normally go to `%LOCALAPPDATA%\Halo`. Put a file named `portable.marker` next to
the exes and they go to `<app folder>\data` instead, which makes the whole thing self-contained on
a USB stick (`src/Halo.Shared/Paths.cs`). The portable zip ships with that marker inside; a
`dist\app` built from source deliberately does not get one, so the dev loop keeps using
`%LOCALAPPDATA%` like an installed copy does.

If neither location can be created or written to, everything falls back to `%TEMP%\Halo` and the
reason is logged — settings that silently stop persisting are otherwise impossible to diagnose.

### Kept project-local on purpose

For anyone building from source. Each of these would normally land somewhere global:

| Item | Normal location | Halo location |
| --- | --- | --- |
| NuGet package cache | `%USERPROFILE%\.nuget\packages` | `tools\nuget-cache` (via `nuget.config` `globalPackagesFolder`) |
| PresentMon SDK | `Program Files\Intel\PresentMon` | `tools\presentmon\sdk\`, committed to git (MIT), copied into the build output |
| PawnIO installer payload | — | `installer\redist\PawnIO_setup.exe`, downloaded by `tools\build.ps1` against a pinned SHA-256, gitignored |
| Build outputs | — | `dist\`, `bin\`, `obj\` (all gitignored) |

`tools\build.ps1` needs no admin and touches nothing outside the repo except the NuGet restore and,
on its first run, the PawnIO download.

---

## 3. Full removal

1. **Settings → Apps → Halo → Uninstall** (or `unins000.exe` in the program folder). It stops both
   tasks, kills any `Halo.*` / `PresentMon*` process whose image path is inside the install folder,
   removes the tasks, deletes `Program Files\Halo`, and asks whether to delete
   `%LOCALAPPDATA%\Halo`. A silent uninstall keeps the data rather than deleting layouts with
   nobody there to answer.
2. PawnIO stays. It can be removed from Settings → Apps if nothing else uses it.

Silent uninstall:

```powershell
& "C:\Program Files\Halo\unins000.exe" /VERYSILENT /NORESTART
```

For a build from source, `tools\uninstall-dev.ps1` does the same for a `dist\app` layout: it stops
everything in the right order (widgets first — that process owns the watchdog that restarts the
collector), then calls `--unregister-autostart`. It never touches `%LOCALAPPDATA%\Halo` or PawnIO.

---

See also: [architecture.md](architecture.md) for what the three processes are and how they talk,
and [../README.md](../README.md) for the install walkthrough.
