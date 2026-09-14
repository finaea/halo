# Halo — Hardware Analytics & Live Overlay

A native Windows desktop widget suite that renders a live hardware dashboard straight onto the
wallpaper — CPU, GPU, RAM, drives, fans, power, network, latency, and a real-time FPS/frametime
counter. Built to replace **Rainmeter + Rainformer + HWiNFO + MSI Afterburner + RTSS + the NVIDIA
App** with one low-overhead app.

![Halo widget suite](preview.png)

Eleven widget types, all independent windows you drag where you like
(`src/Halo.Shared/Panels/PanelCatalog.cs:102-312`):

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
integrity, so nothing you interact with is elevated.

## Install

1. Grab `Halo-Setup-<version>.exe` from the [Releases page](https://github.com/finaea/halo/releases).
2. Run it. **One UAC prompt**, no second one.
3. Up to two components, both ticked by default:
   - **PawnIO driver for CPU temps, fans, drive temps** — a signed third-party kernel driver
     ([namazso/PawnIO](https://github.com/namazso/PawnIO), GPL-2.0-or-later). LibreHardwareMonitor
     0.9.6 has no other way to read those sensors. If it says a restart is needed, those rows stay
     `N/A` until you reboot; nothing else is affected. Untick it if you'd rather not, you can turn
     it on later from System check. If a PawnIO is already on the machine (FanControl, HWiNFO and
     LibreHardwareMonitor install the same driver), this component isn't shown and the existing
     driver is used as is, whatever its version.
   - **Start with Windows** — registers two scheduled tasks (`\Halo\Collector` at highest
     privileges, `\Halo\Widgets` at normal) so nothing prompts for UAC at logon.
   There's also a **Create a desktop shortcut** checkbox.
4. Widgets appear, and **Settings opens on the System check page**: what your hardware actually
   exposes, which providers are OK / degraded / unavailable and why, and buttons to fix the
   fixable. First run also offers to generate a starting layout for your monitors.

### Starting Halo yourself

Halo doesn't start itself unless you ticked **Start with Windows**. The **Halo** shortcut (Start
menu, and your desktop if you asked for it) is the one to click: the overlay comes up, then Windows
asks to let the collector run as administrator. Say yes — that's what reads CPU temps, fans, drive
temps and the FPS pipeline. Say no and everything else still works, those rows just read `N/A`.

Clicking it again while Halo is already running is safe: the widgets are single-instance, and the
collector is only started if there isn't one. `Halo Widgets` and `Halo Settings` still start one
process each, with no prompt, and `Halo.Collector.exe` on its own is still unelevated — `--dump`,
`--migrate-config` and the smoketests never ask for anything.

Prefer portable? `Halo-<version>-win-x64.zip` from the same release unzips to a folder that keeps
its config and logs in `.\data` next to the exes (that's what the `portable.marker` file inside
does). Run `Halo.Collector.exe` as admin, then `Halo.Widgets.exe`.

### Where things go

| | |
| --- | --- |
| Program | `C:\Program Files\Halo` |
| Config + logs | `%LOCALAPPDATA%\Halo` (or `<app folder>\data` in portable mode) |
| Autostart | scheduled tasks `\Halo\Collector` and `\Halo\Widgets` |
| PawnIO | `C:\Program Files\PawnIO` — **left behind on uninstall**, it's a shared driver |

Full footprint, including what uninstall removes: [docs/global-installs.md](docs/global-installs.md).

## What works without the optional pieces

**Without PawnIO** — reads `N/A`: `cpu.package.temp.c`, `cpu.package.power.w`, `cpu.vcore.v`, every
`fan.<n>.rpm`, every `drive.<x>.temp.c`. Still fine: all CPU load and clock rows (those come from
the OS, not the driver), RAM, GPU, network, drive space and read/write rates, FPS, latency, top
processes, clock.

**Without an NVIDIA GPU** — AMD and Intel cards are enumerated through LibreHardwareMonitor, so you
get the temp / load / clock / VRAM rows it exposes. NVML-only extras (some power and clock detail),
DLSS state and Reflex PC latency read `N/A`. Multi-GPU is handled either way: read `gpu.count` and
pick the card per widget.

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
config/reference     the v2 config layout for reference (your real config lives in %LOCALAPPDATA%)
tools/extracted      faithful JSON transcriptions of the original Rainmeter skins (spec source)
```

## For widget developers

Halo's metrics are a public, documented interface. Any process running as the same user can read
them — you do not need Halo's code, and you do not need admin.

- **[docs/metrics-protocol.md](docs/metrics-protocol.md)** — the contract: section
  `Local\Halo.Metrics.v2`, byte-exact struct layouts, the five reader rules, the control pipe, and
  a ~60-line Python reader you can paste.
- **[docs/current-metrics-inventory.md](docs/current-metrics-inventory.md)** — what every metric
  name means and how fresh it is.
- **`src/Halo.Metrics`** — a dependency-free, AOT-friendly .NET client. `CollectorSession`
  implements all five reader rules for you.
- **`Halo.Collector.exe --dump`** for a human-readable snapshot, `--dump --json` for the documented
  machine shape.

Indexed families (`gpu.<i>.*`, `cpu.core.<i>.*`, `fan.<n>.*`, `drive.<x>.*`) are discovered at
runtime — read `gpu.count` / `cpu.logical.count` / `fan.count` and never assume one of anything.
Every metric also publishes the real cadence it changes at, so you know when polling faster buys
you nothing.

## Troubleshooting

**"Windows protected your PC" / SmartScreen.** Halo is not code-signed yet — a certificate costs
money and v1 doesn't have one. Click **More info → Run anyway**. If that bothers you, build from
source instead; the hash of what you build is the hash of what you run.

**A file vanished, or Defender flagged something.** Happened once, 2026-07-19: Defender's ML
heuristic quarantined `Halo.Collector.csproj` as `Trojan:Win32/Bearfoos.A!ml` — a false positive on
plain MSBuild XML. Unsigned binaries make a repeat more likely. Check `Get-MpThreatDetection`,
restore the file, and report it to Microsoft as a false positive. Halo's installer deliberately does
**not** add a Defender exclusion — that's your call, not ours.

**Widgets disappeared after Explorer restarted.** Explorer taking the desktop host window down
takes the widgets with it. Halo notices within ~2 s and rebuilds them
(`src/Halo.Widgets/App.cs:376-393`). If they don't come back, check
`%LOCALAPPDATA%\Halo\logs\widgets-*.log`.

**Laptop on battery: did Halo stop?** No. Both tasks are registered with
`DisallowStartIfOnBatteries` and `StopIfGoingOnBatteries` off
(`src/Halo.Settings/Services/AutostartManager.cs:182-183`) — `schtasks`' defaults would have
stopped them, which is why Halo registers through the Task Scheduler API instead.

**Temps and fans read N/A.** PawnIO is either not installed or not loaded (it needs a restart after
install). Settings → System check tells you which, and has an Install PawnIO button.

**FPS reads N/A while a game is running.** Only one process can own the PresentMon ETW session, so a
second collector — or another capture tool holding it — gets nothing. Also check the collector is
actually elevated (System check says).

**Widgets in the wrong place / wrong monitor.** Positions are stored per monitor device id. Drag one
once and the drop saves instantly, including which monitor it landed on.

**Everything reads N/A and there was no UAC prompt.** You probably started `Halo Widgets` rather
than **Halo** — the plain entry is the overlay on its own. Start `Halo` instead, or start
`Halo.Collector.exe` as administrator by hand.

**I hand-edited `widgets.json` and my widgets vanished.** They shouldn't any more: a config file
that's present but doesn't parse is ignored and the last good copy is kept, with a line in
`%LOCALAPPDATA%\Halo\logs\widgets-*.log` saying which file and why. Halo also won't overwrite it
while it's broken, so a widget you drag in the meantime won't stick until the JSON is valid again.

**On a standard-user account, Settings changes don't reach the collector.** UAC asked for an
admin's password, so the collector is running as *that* user and resolves a different
`%LOCALAPPDATA%\Halo`. Use portable mode, or tick "Start with Windows" — the scheduled task always
runs as you. Details in [docs/global-installs.md](docs/global-installs.md#6-known-limitation-the-halo-shortcut-on-a-standard-user-account).

## Privacy

No telemetry. Nothing is uploaded, no analytics, no crash reporting, no update check.

The only outbound network request Halo can make is an external-IP lookup against
`https://api.ipify.org` for the Network widget's public-IP row. It is **off by default** on a fresh
install and is a single switch in Settings → General (`collector.externalIp.enabled`). Everything
else — ETW sessions, the PresentMon service, the shared-memory section, the control pipe — is local
to your machine.

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
