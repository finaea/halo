# Halo — what installing adds, and what ships inside the app

This doc answers two questions separately:

1. **What does installing Halo add to the machine?** Section 1. The list is short, and nothing is
   added without a row in it.
2. **What is carried inside the app folder instead of being installed system-wide?** Section 2.
   The main point is that Halo brings its own .NET runtime: nothing installs .NET, and nothing looks
   for an existing one.

There are two ways to run Halo, with two different footprints:

- **Installed** (`Halo-Setup-<version>.exe`) — Program files under `Program Files\Halo`, data under
  `%LOCALAPPDATA%\Halo`, scheduled tasks, shortcuts and an entry in Installed apps.
- **Portable** (`Halo-<version>-win-x64.zip`, or a `dist\app` built from source with a
  `portable.marker` next to the programs) — Everything, including settings and logs, stays in the
  folder. There is no registry entry and nothing to uninstall. Autostart is only added if
  **Turn on autostart…** is used in System check, in which case the two scheduled tasks below point
  at the portable folder.

---

## 1. Global footprint

Everything the installer adds outside the app folder:

| Item | Where | Created by | Removed by |
| --- | --- | --- | --- |
| Program files | `C:\Program Files\Halo` | Inno Setup `[Files]` | Uninstall |
| Start-menu shortcuts | `Halo` (runs `Halo.Widgets.exe --start-collector` — the widgets plus a UAC prompt for the collector), `Halo Settings`, `Halo Widgets` | `[Icons]` | Uninstall |
| Desktop shortcut *(optional, the "Create a desktop shortcut" checkbox)* | `Halo` on the all-users desktop, with the same target as the Start-menu `Halo` | `[Icons]` + `[Tasks] desktopicon` | Uninstall |
| Installed apps entry | `HKLM\...\Uninstall\{D05EF3BB-...}_is1` | Inno Setup | Uninstall |
| Collector autostart | Scheduled task `\Halo\Collector` — RunLevel **Highest**, at sign-in of the interactive user | `Halo.Settings.exe --register-autostart` | `--unregister-autostart`, run by `[UninstallRun]` |
| Widgets autostart | Scheduled task `\Halo\Widgets` — RunLevel **Limited** (medium integrity), at sign-in | Same | Same |
| Settings and logs | `%LOCALAPPDATA%\Halo\config\*.json`, `%LOCALAPPDATA%\Halo\logs\` | The apps, on first run | Uninstall **asks**; keeping them lets a later install continue where this one stopped |
| Install log and install record | `%LOCALAPPDATA%\Halo\logs\install-<version>-<time>.log`, `%LOCALAPPDATA%\Halo\install-state.json` | The installer, at the end of setup | Same as settings and logs |
| PawnIO driver *(optional component, selected by default, not offered when any PawnIO is already installed)* | `C:\Program Files\PawnIO`, plus its own Installed apps entry and kernel service | `Halo.Settings.exe --install-pawnio` → `redist\PawnIO_setup.exe` | **Not removed** — see below |

There is **no** `HKCU\...\Run` value. `--register-autostart` deletes a leftover `HaloWidgets` value
from earlier builds if it finds one (`src/Halo.Settings/Services/AutostartManager.cs`).

`install-state.json` records what the installer decided — whether autostart was registered or
declined, the PawnIO outcome, and whether a restart is pending. Without it, a failed autostart
registration and a deliberately unticked "Start with Windows" would look the same afterwards, since
both leave no scheduled tasks.

### The two scheduled tasks

Both are created through the Task Scheduler COM API rather than `schtasks`, with
`DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false`, `ExecutionTimeLimit=PT0S`,
`StartWhenAvailable=true`, `RestartCount=3` (one minute apart) and priority 5
(`AutostartManager.cs`). `schtasks /Create` stops a task by default when a laptop switches to
battery, which is exactly the wrong behaviour for a monitor, and is why none of this goes through
it.

The task definition exists in exactly one place. The installer, `tools\install-dev.ps1` and the
System check **Turn on autostart…** / **Repair autostart…** button all call the same
`Halo.Settings.exe --register-autostart` verb.

Uninstall only removes a task whose action points into the folder being uninstalled. A
`\Halo\Collector` task registered by a build from source somewhere else is left alone.

### Why PawnIO is left behind

PawnIO is a **shared** kernel driver. FanControl, LibreHardwareMonitor and HWiNFO all use the same
one, and Halo cannot know whether it was there first. Uninstalling Halo therefore leaves it
installed, and says so on the way out. It can be removed from **Settings → Apps** if nothing else
needs it.

The same sharing is why the installer does not offer the component when any version of PawnIO is
already present: `PawnIO_setup.exe` refuses to install over an existing copy and exits with 183
(`ERROR_ALREADY_EXISTS`).

### Known limitation: the Halo shortcut on a standard-user account

The `Halo` shortcut runs `Halo.Widgets.exe --start-collector`, which starts the collector through
the `runas` verb.

- **Signed in as an administrator** — Windows asks for confirmation, the collector runs as the same
  user, and both processes use the same `%LOCALAPPDATA%\Halo`. Everything is consistent.
- **Signed in as a standard user** — UAC asks for a *different* account's credentials. The
  collector then runs as that administrator, and because `%LOCALAPPDATA%` is per user, settings and
  logs quietly split into two folders.

The metrics still arrive either way. `Local\Halo.Metrics.v2` belongs to the sign-in *session*, not
the user, and is created with `GENERIC_READ` for Everyone (`src/Halo.Metrics/NativeSection.cs`), so
the widgets show live numbers. It is the settings and logs that differ: the collector ends up
reading a `settings.json` that the Settings app never writes.

Two existing options avoid it:

- **Portable mode** — A `portable.marker` next to the programs puts the data in
  `<app folder>\data`, which is the same folder whoever runs it.
- **Autostart** — The scheduled tasks do not have this problem. They are registered with
  `TaskLogonInteractiveToken` for the user at the console, found at registration time with
  `WTSGetActiveConsoleSessionId` inside the `--register-autostart` verb, so the elevated collector is
  always the *same* user as the widgets. If they still end up on the wrong account — with several
  people signed in at once, for example — System check → **Repair autostart…** registers them again
  for whoever runs it.

This is documented rather than worked around. The direct UAC launch is what makes "open the
shortcut, approve the prompt" work with no scheduled task at all, and an administrator account —
the common case — is not affected.

### Only while running, and cleaned up automatically

| Item | Lifetime |
| --- | --- |
| `Local\Halo.Metrics.v2` shared section, `Local\Halo.FramesReady.v2` event, `\\.\pipe\Halo.Control.v2` | End with the collector process |
| ETW sessions `HaloPMSvc`, `HaloTap` and the PCL Stats session | Stopped by the collector; one left behind by a forced stop is cleaned up at the next start |
| `PresentMonService.exe` | A plain console-mode **child process** of the collector — never registered with the Windows service manager, so there is no service entry to leave behind |
| LibreHardwareMonitor's global ISA-bus mutex | Ends with the process |

---

## 2. What ships inside the app folder

### Halo brings its own .NET runtime

**Nothing installs .NET system-wide, and nothing looks for an existing copy.** `tools\build.ps1`
publishes all three programs `--self-contained` into one folder, so the runtime travels with the
app.

The folder itself shows this:

- Each `<name>.runtimeconfig.json` lists **`includedFrameworks`**, not `framework`. A
  framework-dependent app declares `framework` — a requirement for a runtime the machine must
  already have, and a launch failure if it does not. `includedFrameworks` means the opposite: the
  runtime ships inside the folder. `Halo.Settings.runtimeconfig.json` lists two,
  `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App`, because it is the WPF app.
- The .NET host and runtime are ordinary files next to the programs: `hostfxr.dll`,
  `hostpolicy.dll`, `coreclr.dll`, `clrjit.dll`, `System.Private.CoreLib.dll` and the rest of the
  framework assemblies.

That is where the size comes from. A published `dist\app` is roughly **300 files and about 175 MB**,
of which Halo's own three programs and their assemblies are about **2.6 MB** (measured with a
10.0.12 runtime; `tools\build.ps1` prints the exact count and size at the end of every build).
Everything else is the runtime, WPF, and the third-party libraries listed in
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). Publishing uses ReadyToRun by default, which
`tools\build.ps1` measures at about 14 MB of that total in exchange for faster start-up;
`-NoReadyToRun` turns it off.

All three programs are published into the *same* folder so they share one copy of the runtime
instead of carrying three. Each keeps its own `.deps.json` and `.runtimeconfig.json`, so nothing
conflicts, and the runtime files themselves are identical because `global.json` pins the SDK.

A `dotnet build` during development is framework-dependent (`Directory.Build.props` sets
`SelfContained=false`), so it does need a .NET 10 runtime on the machine — but nothing that ships
is built that way.

### The rest of the payload

Everything Halo reads while running is found relative to the folder the program sits in, through
`Halo.Shared.Paths`, and never by searching upwards for the repository. An installed copy under
Program Files and a development build under `src\…\bin\Debug` find their files the same way.

| Inside the app folder | What | Read by |
| --- | --- | --- |
| `presentmon\PresentMonAPI2.dll`, `PresentMonService.exe` | The bundled Intel PresentMon 2 SDK (MIT) | Frame capture — `Paths.PresentMonDir` |
| `assets\fonts\*.ttf` | `ElegantIcons.ttf`, the one font Halo ships | The renderer's private font collection — `Paths.FontsDir` |
| `assets\halo.ico` | Window and notification-area icon | All three processes |
| `redist\PawnIO_setup.exe` | The PawnIO installer | `Halo.Settings.exe --install-pawnio`, so System check can offer it later — `Paths.RedistDir` |
| `LICENSE`, `NOTICE.md`, `THIRD-PARTY-NOTICES.md` | Licences and notices | People; Settings → About opens the third-party notices |

### Portable mode

Settings and logs normally go to `%LOCALAPPDATA%\Halo`. With a file named `portable.marker` next to
the programs, they go to `<app folder>\data` instead, which keeps the whole thing self-contained,
for example on a USB stick (`src/Halo.Shared/Paths.cs`). The portable zip includes that marker. A
`dist\app` built from source deliberately does not, so the development loop keeps using
`%LOCALAPPDATA%` like an installed copy.

If neither location can be created or written to, everything falls back to `%TEMP%\Halo` and the
reason is logged — otherwise settings that quietly stop saving would be impossible to explain.

### Kept inside the project on purpose

For anyone building from source. Each of these would normally go somewhere global:

| Item | Usual location | Halo location |
| --- | --- | --- |
| NuGet package cache | `%USERPROFILE%\.nuget\packages` | `tools\nuget-cache` (via `nuget.config` `globalPackagesFolder`) |
| PresentMon SDK | `Program Files\Intel\PresentMon` | `tools\presentmon\sdk\`, committed to git (MIT) and copied into the build output |
| PawnIO installer | — | `installer\redist\PawnIO_setup.exe`, downloaded by `tools\build.ps1` and checked against a pinned SHA-256; not in git |
| Build outputs | — | `dist\`, `bin\`, `obj\` (none in git) |

`tools\build.ps1` needs no administrator rights and touches nothing outside the repository except
the NuGet restore and, on its first run, the PawnIO download.

---

## 3. Full removal

1. **Settings → Apps → Halo → Uninstall** (or `unins000.exe` in the program folder). It stops both
   tasks, ends any `Halo.*` or `PresentMon*` process whose program file is inside the install
   folder, removes the tasks, deletes `Program Files\Halo`, and asks whether to delete
   `%LOCALAPPDATA%\Halo`. A silent uninstall keeps the data, because deleting someone's layouts
   with nobody there to answer would be the wrong default.
2. PawnIO stays. It can be removed from **Settings → Apps** if nothing else uses it.

Silent uninstall:

```powershell
& "C:\Program Files\Halo\unins000.exe" /VERYSILENT /NORESTART
```

For a build from source, `tools\uninstall-dev.ps1` does the same for a `dist\app` layout. It stops
everything in the right order — the widgets first, because that process runs the watchdog that
would restart the collector — and then calls `--unregister-autostart`. It never touches
`%LOCALAPPDATA%\Halo` or PawnIO.

---

See also [architecture.md](architecture.md) for what the three processes are and how they talk,
and the [README](../README.md) for the install walkthrough.
