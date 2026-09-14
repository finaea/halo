# Halo — Hardware Analytics & Live Overlay

A native Windows 11 desktop widget suite that renders a live hardware dashboard directly on the
desktop — CPU, GPU, RAM, drives, fans, power, network, latency, and a real-time FPS/frametime
counter. Built to replace **Rainmeter + Rainformer + HWiNFO + MSI Afterburner + RTSS + the NVIDIA
App** with one self-contained, low-overhead app.

![Halo widget suite](preview.png)

Eleven widget types, all independent windows that can be dragged anywhere
(`src/Halo.Shared/Panels/PanelCatalog.cs:100-328`):

| | |
| --- | --- |
| **CPU / RAM** | package temp, per-core load, clocks, RAM usage, overlay graph |
| **GPU** | temp, load, VRAM, fan, core and memory clocks, overlay graph |
| **FPS counter** | presented & displayed lanes, 1% / 0.1% lows, frametime mean + worst, live graph |
| **Latency / DLSS** | PC latency breakdown, DLSS and frame-gen state |
| **Drives** | per-volume temp / used / total, read & write history graphs |
| **Power · Fans · Network · Top processes (CPU / RAM) · Clock** | draw, RPMs, throughput, per-process CPU and RAM |

Everything is per-widget configurable: which rows show, colours, warn thresholds, graph history,
refresh rate, monitor, z-order, click-through, opacity.

## Requirements

| | Needed for | Without it |
| --- | --- | --- |
| **Windows 10 1809 (build 17763) or newer, 64-bit** | everything | the installer refuses to run (`installer/halo.iss` `MinVersion`) |
| **Admin, once, at install** | the collector runs as a scheduled task at highest privileges | — |
| **PawnIO** (offered by the installer when none is installed yet, ticked by default) | CPU temp / package power / Vcore, fan RPM, drive temps | those rows read `N/A`; everything else works |
| **NVIDIA GPU + driver** | full GPU panel via NVML, DLSS detection, Reflex/PCL latency | AMD and Intel GPUs fall back to what LibreHardwareMonitor exposes; latency and DLSS read `N/A` |
| **Nothing else** | — | no .NET install, no Visual C++ redist, no HWiNFO, no Rainmeter. The download is self-contained. |

Disk: ~57 MB to download, ~173 MB installed (one shared .NET runtime for all three processes).

**Elevation model, in two sentences.** The collector runs elevated because ring-0 sensor reads and
ETW frame capture need it; the widgets and the settings app run as a normal user process at medium
integrity. They talk through a shared-memory section whose DACL grants read to everyone at medium
integrity, so nothing a user clicks on is elevated.

## Install

1. Grab `Halo-Setup-<version>.exe` from the [Releases page](https://github.com/finaea/halo/releases).
2. Run it. **One UAC prompt**, no second one.
3. Up to two components, both ticked by default:
   - **PawnIO driver for CPU temps, fans, drive temps** — a signed third-party kernel driver
     ([namazso/PawnIO](https://github.com/namazso/PawnIO), GPL-2.0-or-later). LibreHardwareMonitor
     0.9.6 has no other way to read those sensors. If it says a restart is needed, those rows stay
     `N/A` until the machine is restarted; nothing else is affected. It can be unticked, and turned
     on later from System check. If a PawnIO is already on the machine (FanControl, HWiNFO and
     LibreHardwareMonitor install the same driver), this component isn't shown and the existing
     driver is used as is, whatever its version.
   - **Start with Windows** — registers two scheduled tasks (`\Halo\Collector` at highest
     privileges, `\Halo\Widgets` at normal) so nothing prompts for UAC at logon.
   There's also a **Create a desktop shortcut** checkbox.
4. Widgets appear, and **Settings opens on the System check page**: what the hardware actually
   exposes, which providers are OK / degraded / unavailable and why, and buttons to fix the
   fixable. First run also offers to generate a starting layout for the monitors it finds.

### Starting Halo by hand

Without **Start with Windows**, Halo does not start itself. The **Halo** shortcut — Start menu, and
the desktop if that box was ticked — is the one to click: the overlay comes up, then Windows asks
to let the collector run as administrator. Approving it is what enables CPU temps, fans, drive
temps and the FPS pipeline; declining leaves everything else working, with those rows reading
`N/A`.

Clicking it again while Halo is already up is safe. The widgets are single-instance, and the
collector is started only when the section says none is running
(`src/Halo.Widgets/CollectorLauncher.cs:80-88`). `Halo Widgets` and `Halo Settings` still start one
process each with no prompt, and `Halo.Collector.exe` on its own is still unelevated — `--dump`,
`--migrate-config` and the smoketests never ask for anything.

There is a portable build too. `Halo-<version>-win-x64.zip` from the same release unzips to a
folder that keeps its config and logs in `.\data` next to the exes (that's what the
`portable.marker` file inside does). Run `Halo.Collector.exe` as admin, then `Halo.Widgets.exe`.

### Where things go

| | |
| --- | --- |
| Program | `C:\Program Files\Halo` |
| Config + logs | `%LOCALAPPDATA%\Halo` (or `<app folder>\data` in portable mode) |
| Autostart | scheduled tasks `\Halo\Collector` and `\Halo\Widgets` |
| PawnIO | `C:\Program Files\PawnIO` — **left behind on uninstall**, it's a shared driver |

Full footprint, what rides inside the app folder, and what uninstall removes:
[docs/install-footprint.md](docs/install-footprint.md).

## What works without the optional pieces

**Without PawnIO** — reads `N/A`: `cpu.package.temp.c`, `cpu.package.power.w`, `cpu.vcore.v`, every
`fan.<n>.rpm`, every `drive.<x>.temp.c`. Still fine: all CPU load and clock rows (those come from
the OS, not the driver), RAM, GPU, network, drive space and read/write rates, FPS, latency, top
processes, clock.

**Without an NVIDIA GPU** — AMD and Intel cards are enumerated through LibreHardwareMonitor, so the
temp / load / clock / VRAM rows it exposes still work. NVML-only extras (some power and clock
detail), DLSS state and Reflex PC latency read `N/A`. Multi-GPU is handled either way: read
`gpu.count` and pick the card per widget.

**Without admin** (running the exe by hand instead of through the task) — LibreHardwareMonitor's
CPU / SuperIO / storage parts and both ETW pipelines (PresentMon frames, Reflex markers) are gone,
so temps, fans, drive temps, FPS and latency read `N/A`. System check names the reason per provider
(`unelevated`, `no-driver`, `no-hw`, `no-nvml`, `no-sdk`, `failed`).

## Build from source

```powershell
winget install Microsoft.DotNet.SDK.10     # .NET 10 SDK; global.json pins it
git clone https://github.com/finaea/halo && cd halo
tools\build.ps1                            # -> dist\app (publish + assets + PresentMon + PawnIO)
tools\install-dev.ps1                      # registers the two tasks against dist\app, one UAC prompt
```

`tools\build.ps1 -Installer` also compiles the setup exe — that needs
[Inno Setup 6](https://jrsoftware.org/isdl.php), which installs per-user without admin:
`innosetup-6.x.x.exe /VERYSILENT /CURRENTUSER /NORESTART`. Add `-Zip` for the portable archive,
`-Clean` to wipe `dist\app` first.

The first build restores NuGet into the project-local cache `tools\nuget-cache` (needs internet
once), and downloads `PawnIO_setup.exe` from its official GitHub release, verifying a pinned
SHA-256 before it ships it — the hash and URL are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Day-to-day loop: `tools\redeploy-halo.ps1` (stop widgets → stop collector task → build → start
both, in the only order that works). `tools\uninstall-dev.ps1` removes the tasks.

```
src/Halo.Collector   sensor providers, shared-memory writer, frame pipeline
src/Halo.Widgets     rendering engine, panel definitions, window management
src/Halo.Settings    WPF settings app (WPF-UI), System check, first run
src/Halo.Shared      paths, config models, panel manifest, logging
src/Halo.Metrics     the public client package: layout, reader, writer, control pipe
config/reference     the v2 config layout for reference (the real config lives in %LOCALAPPDATA%)
tools/extracted      faithful JSON transcriptions of the original Rainmeter skins (spec source)
```

How the three processes fit together, and why the boundary sits where it does:
[docs/architecture.md](docs/architecture.md).

## For widget developers

Halo's metrics are a public, documented interface. Any process running as the same user can read
them — no Halo code required, and no admin.

- **[docs/metrics-protocol.md](docs/metrics-protocol.md)** — the contract: section
  `Local\Halo.Metrics.v2`, byte-exact struct layouts, the five reader rules, the control pipe, and
  a ready-to-paste ~60-line Python reader.
- **[docs/current-metrics-inventory.md](docs/current-metrics-inventory.md)** — what every metric
  name means and how fresh it is.
- **`src/Halo.Metrics`** — a dependency-free, AOT-friendly .NET client. `CollectorSession`
  implements all five reader rules already.
- **`Halo.Collector.exe --dump`** for a human-readable snapshot, `--dump --json` for the documented
  machine shape.

Indexed families (`gpu.<i>.*`, `cpu.core.<i>.*`, `fan.<n>.*`, `drive.<x>.*`) are discovered at
runtime — read `gpu.count` / `cpu.logical.count` / `fan.count` and never assume one of anything.
Every metric also publishes the real cadence it changes at, so a consumer knows when polling faster
buys nothing.

## Troubleshooting

**Settings → System check is the first stop for most of these.** It lists every data source with
OK / degraded / unavailable, says *why* in plain words, and has buttons for the two things that are
fixable from there: installing PawnIO and repairing autostart.

**Temperatures, fan speeds or drive temperatures show "N/A".**
Those particular readings need a small helper driver called PawnIO, and it is either not installed
or installed but not loaded yet. Open Settings → System check. If it offers an **Install PawnIO**
button, use it. If it says a restart is needed, restart — the driver cannot load until then.
Everything else keeps working in the meantime.

> Windows will not let ordinary software read CPU package temperature, motherboard fan headers or
> SMART data directly, so LibreHardwareMonitor 0.9.6 reads them through PawnIO's signed kernel
> driver. It is shared: FanControl, HWiNFO and LibreHardwareMonitor install the same one, so a
> machine that already runs any of those has it.

**Everything reads N/A, and no UAC prompt ever appeared.**
That usually means Halo was started from the `Halo Widgets` entry rather than **Halo**. The plain
entry is the overlay on its own — it never starts the collector. Start **Halo** instead, or run
`Halo.Collector.exe` as administrator by hand.

**The numbers froze, or every panel shows a "stale" badge.**
The background process that reads the sensors (the collector) has stopped. If autostart is on, Halo
restarts it by itself within a few seconds. If it isn't, clicking the **Halo** shortcut again
starts one — that is what the badges are waiting for. The log is at
`%LOCALAPPDATA%\Halo\logs\collector-*.log`.

> The widget process treats the collector as stale after 5 s without a heartbeat and asks the
> `\Halo\Collector` scheduled task to start it, backing off 1 s, 5 s, 30 s. With no task registered
> it logs one line and leaves the badges up rather than raising a UAC dialog on an idle desktop,
> and re-probes for the task every 60 s in case autostart gets switched on. A collector that is
> alive but wedged is `/End`ed before the restart, because the task ignores a second instance
> (`src/Halo.Widgets/App.cs:550-606`).

**The widgets didn't come back after a restart.**
Halo starts itself at logon only if the "Start with Windows" option was ticked during install.
Settings → System check says whether the two startup entries are there, and **Repair autostart**
re-creates them. Either way, the **Halo** shortcut starts everything by hand.

> The entries are scheduled tasks, `\Halo\Collector` (highest privileges) and `\Halo\Widgets`
> (normal), not registry Run values — that is what lets the elevated half start at logon with no
> UAC prompt. Full detail in [docs/install-footprint.md](docs/install-footprint.md).

**The widgets vanished, with nothing else obviously wrong.**
This normally means Windows Explorer restarted and took the desktop with it. Halo notices within
about 2 seconds and rebuilds the widgets. If they don't come back, the log at
`%LOCALAPPDATA%\Halo\logs\widgets-*.log` says why.

> The widgets are child windows of the desktop host (`Progman` / `WorkerW`), and Win32 destroys a
> window's children with it. A guard re-validates the host every 2 s and rebuilds onto whatever
> host exists then, even before the shell has finished coming back
> (`src/Halo.Widgets/App.cs:423-456`).

**Widgets are on the wrong monitor, or in the wrong place.**
Just drag one where it belongs — the position saves the moment it is dropped, including which
monitor it landed on. If a monitor is unplugged, the widgets that lived on it are packed onto the
primary one temporarily; plugging it back in puts them home, and the saved layout is never
overwritten in the meantime.

**The FPS counter reads "N/A" while a game is running.**
Only one program on the machine can capture frame timings at a time. If another capture tool
(another copy of Halo, CapFrameX, FrameView, HWiNFO's frame counter) is running, close it. If that
isn't it, System check will say the collector is not running with administrator rights, which frame
capture needs.

> "One at a time" is a Windows ETW limitation on the PresentMon session, not a Halo choice. A second
> collector on the same machine reads N/A for fps and nothing else — expected, not a bug.

**Halo is running on a laptop — does it stop on battery?**
No. It keeps collecting and rendering unplugged.

> Both tasks are registered with `DisallowStartIfOnBatteries` and `StopIfGoingOnBatteries` off
> (`src/Halo.Settings/Services/AutostartManager.cs:220-221`). `schtasks` defaults to stopping on
> battery, which is why Halo registers through the Task Scheduler API instead.

**"Windows protected your PC" appears when running the installer.**
That is SmartScreen reacting to an app it has not seen before, not a virus warning. Halo is not
code-signed — a certificate costs money that v1 doesn't have. Click **More info → Run anyway**.
Anyone who would rather not take that on faith can build from source instead; the hash of the build
is the hash of what runs.

**Windows Defender deleted or quarantined a file.**
It happens to unsigned software: Defender's machine-learning heuristic occasionally flags something
harmless. Check what it took with `Get-MpThreatDetection` in PowerShell, restore the file, and
report it to Microsoft as a false positive. Halo's installer deliberately does **not** add a
Defender exclusion — punching a hole in antivirus is the machine owner's decision, not the
installer's.

> Recorded instance, 2026-07-19: `Halo.Collector.csproj` — a plain MSBuild XML file, not a binary —
> quarantined as `Trojan:Win32/Bearfoos.A!ml`.

**A hand-edited `widgets.json` made the widgets disappear.**
It shouldn't, any more. A config file that is present but does not parse is ignored, the last good
copy is kept, and `%LOCALAPPDATA%\Halo\logs\widgets-*.log` names the file and the reason. Fix the
JSON and the widgets come back on the next save.

> Halo also refuses to overwrite a file it could not read, so a widget dragged in the meantime
> won't stick until the JSON is valid again — overwriting would throw away whatever was being
> hand-edited (`src/Halo.Shared/Config/ConfigStore.cs:139-155`).

**Settings changes don't reach the collector, on a standard-user account.**
This happens when UAC asked for an administrator's password rather than just consent. The collector
is then running as *that* account and reads a different `%LOCALAPPDATA%\Halo` from the one Settings
writes. Use portable mode, or tick "Start with Windows" — the scheduled task always runs as the
logged-in user.

> Full explanation, including why the metrics still arrive while the settings diverge:
> [docs/install-footprint.md](docs/install-footprint.md#known-limitation-the-halo-shortcut-on-a-standard-user-account).

## Privacy

No telemetry. Nothing is uploaded, no analytics, no crash reporting, no update check.

The only outbound network request Halo can make is an external-IP lookup against
`https://api.ipify.org` for the Network widget's public-IP row. It is **off by default** on a fresh
install and is a single switch in Settings → General (`collector.externalIp.enabled`). Everything
else — ETW sessions, the PresentMon service, the shared-memory section, the control pipe — stays on
the machine.

## Credits and licences

The visual design is derived from the **Rainformer 3.1 HWiNFO Edition** Rainmeter skin by
**Pul53dr1v3r**, used under [CC BY-NC 3.0](https://creativecommons.org/licenses/by-nc/3.0/). That
licence is **non-commercial**: use, fork and share it freely, don't sell it. Details and what
exactly is derived: [NOTICE.md](NOTICE.md).

Halo's own code is **MIT** ([LICENSE](LICENSE)).

It stands on [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
(MPL-2.0), [Intel PresentMon](https://github.com/GameTechDev/PresentMon) (MIT),
[PawnIO](https://github.com/namazso/PawnIO) (GPL-2.0-or-later, bundled as a separate program),
[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT),
[WPF-UI](https://github.com/lepoco/wpfui) (MIT) and a few more. Every one of them, with its licence
and what obligation that creates: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
