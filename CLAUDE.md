# Halo — Hardware Analytics & Live Overlay

Native Windows 11 desktop widget suite replacing the Rainmeter + Rainformer + HWiNFO +
Afterburner + RTSS + NVIDIA App stack. Architecture overview: [architecture.md](docs/architecture.md).
Docs live in `docs/`; personal/machine-specific notes in `docs/private/` (git-ignored).

## Build & run

```powershell
dotnet build Halo.sln -c Debug            # all projects (SDK pinned in global.json, targets net10.0)
dotnet test Halo.sln                      # tests\Halo.Tests — xUnit, 39 tests, no hardware needed
# run (order matters only for data availability):
src\Halo.Collector\bin\Debug\net10.0\win-x64\Halo.Collector.exe   # data process (full sensors need admin)
src\Halo.Widgets\bin\Debug\net10.0\win-x64\Halo.Widgets.exe        # widget windows + tray
src\Halo.Settings\bin\Debug\net10.0-windows\win-x64\Halo.Settings.exe
# diagnostics: dump every metric in shared memory (--json for the documented shape)
Halo.Collector.exe --dump [--json]
# one-time upgrade of a pre-v2 config folder:
Halo.Collector.exe --migrate-config <old config dir> [--to <dir>]
# release build: all three exes over ONE shared self-contained runtime, into dist\app
tools\build.ps1 [-Clean] [-Installer] [-Zip] [-NoReadyToRun]   # -Installer needs Inno Setup 6
# dev autostart against dist\app (one UAC prompt): tools\install-dev.ps1 · tools\uninstall-dev.ps1
# stop -> build -> start, in the only order that works: tools\redeploy-halo.ps1
```

**Tests** live in `tests\Halo.Tests` — the pure, I/O-free parts (frame-stat arithmetic, section
layout, provider freshness, config load/write). Anything needing live hardware or the PresentMon
ETW session stays a hand check (`--pm-smoketest`, `--tap-smoketest`). The project is in the
solution, so `dotnet build` and `dotnet test` both pick it up, and deliberately **not** in
`build.ps1`'s publish list — that script names the three shipping exes one by one, so nothing here
can reach `dist\app`. `OutputType=Exe` + `GenerateProgramFile=false` + the hand-written
`Program.cs` are **one unit**: the cross-process config test spawns a second copy of itself and a
default test project produces no apphost. Removing any of the three gives CS5001.

**Paths** come from `Halo.Shared.Paths`, never from walking up to `Halo.sln`. Assets, fonts and the
bundled PresentMon SDK are read from the exe's own folder (the build copies them there); config and
logs live in `%LOCALAPPDATA%\Halo`, or `<app root>\data` when a `portable.marker` file sits next to
the exe. `config\reference\` is the documented v2 layout, not what a running Halo reads.

Portable-first policy (docs/install-footprint.md): the NuGet cache is project-local
(`tools\nuget-cache`); so is the PawnIO redist (`installer\redist\`, fetched by `build.ps1` against
a pinned SHA-256, gitignored). Only global footprints: scheduled tasks `\Halo\Collector` (Highest)
and `\Halo\Widgets` (Limited), created by `Halo.Settings.exe --register-autostart` — which the
installer, `install-dev.ps1` and the System check "Repair autostart" button all call, so the task
definition lives in exactly one place (`AutostartManager`). **No HKCU Run value any more**; the verb
deletes a leftover `HaloWidgets` one. An installed copy adds `Program Files\Halo`, three Start-menu
shortcuts (`Halo`, `Halo Settings`, `Halo Widgets`), an optional desktop icon and an ARP entry, and
leaves PawnIO behind on uninstall (shared driver).

**Autostart is optional, and declining it is a supported configuration.** System check separates
`AutostartStatus.Off` (no tasks at all — neutral, button reads "Turn on autostart") from
`NeedsAttention` (`!Healthy && !Off`: half-registered, pointing at a different Halo, or a legacy
HKCU Run value — the actual fault). The `Halo` Start-menu entry and the desktop icon both run
`Halo.Widgets.exe --start-collector`: overlay first, then `CollectorLauncher` starts the collector
through the `runas` verb (one UAC prompt), skipped when the section header already names a live
collector pid. The elevation lives there rather than in the collector's manifest on purpose —
`requireAdministrator` would make it unconditional, including for `--dump`, `--migrate-config` and
the smoketests, and would delete the "degrades gracefully unelevated" property.

## Architecture (three processes)

- **Halo.Collector** (elevated at install; degrades gracefully unelevated): `ISensorProvider`
  implementations polled by `ProviderHost`, one thread each, at the fixed cadences in
  `CollectorRates.cs` — engineering constants, never user settings (`MaxRateHz` is the safety cap).
  Publishes to shared memory `Local\Halo.Metrics.v2` via `MetricsWriter` (lock-free: atomic
  8-byte value slots, seqlock strings, append-only frame ring, provider health table). Session
  maxima = `.max` metrics via `MetricSink`. Control channel: named pipe `Halo.Control.v2`
  (`reset-max`, `reset-net`, `rescan`, `reload-config`, `ping`); `rescan` re-runs `Initialize` on
  every provider whose `RescanReinitialises` is true (the ones that enumerate hardware) and drops
  repeats inside 10 s. `LhmProvider.Part.Cpu` opts out on purpose — see the LHM gotcha below.
- **Halo.Widgets**: one WS_EX_NOREDIRECTIONBITMAP HWND per widget, DirectComposition +
  D2D on a shared D3D11 device (`Dx`). Panels are element trees (`Render\Elements.cs`)
  in a Rainmeter-like flow layout (`Render\Panel.cs`), built per type in `PanelDefs\`.
  Dirty-driven redraw. Drag/snap(8px)/z-modes/click-through/opacity/context menu in
  `WidgetWindow`; desktop parenting (WorkerW/Progman) + tray + watchdog in `App`.
- **Halo.Settings**: WPF config editor writing the data folder's `config\*.json`; both other
  processes hot-reload via `ConfigStore` file watcher.
- **Halo.Metrics**: the public client package — section layout, reader, writer, `CollectorSession`
  and the control-pipe client. Dependency-free and AOT-friendly so anyone can read Halo's metrics
  (`docs\metrics-protocol.md`). Halo.Shared depends on it, never the other way round.
  Its test seam is `InternalsVisibleTo("Halo.Tests")` plus **internal** constructor overloads
  taking a section name — the tests must point a `MetricsWriter` at a private section, because
  constructing one *clears* the section and a test using the real name would wipe a running
  collector. **The public surface is deliberately unchanged**: this is a published third-party
  contract, and widening it to serve a test would be a permanent decision made for the wrong
  reason. Shadowing `SharedMemoryLayout` is not an alternative — `SectionName` is a `const`, so it
  is inlined into `Halo.Metrics.dll` at its own compile time and a consumer still gets the real one.

## Conventions

- Layout coordinates are **logical units** (panel = 206 wide); theme scale (1.7) is applied
  once in the renderer. 8-pt text rows use `FixedH = 11` (Rainformer's effective line height).
- Colors only via `Theme` tokens (extracted from the Rainformer skin's `@Resources\Variables.inc` —
  values only, no GPL code). The skin is **not vendored here**: a reference copy comes from a
  Rainmeter install of Rainformer 3.1 HWiNFO Edition, under
  `%USERPROFILE%\Documents\Rainmeter\Skins\`. Staged warn colors: `CpuRamPanelImpl.WarnColor`.
- Metric names: `Halo.Metrics\MetricNames.cs`; `.max` suffix = session maximum. Indexed families
  (`gpu.<i>.*`, `cpu.core.<i>.*`, `fan.<n>.*`, `drive.<x>.*`) are discovered at runtime — read
  `gpu.count` / `cpu.logical.count` / `fan.count`, never assume one of anything. The GPU index
  space is owned by `GpuIndexSpace` (NVML by PCI bus id first, then LHM's AMD/Intel cards) and is
  append-only: an index, once published, is that card's for the life of the process.
- Every metric registers its **real** cadence as `nominalRateHz`, not the provider's ceiling —
  a sub-cadence metric says so (presentmon's lows are 2 Hz inside a 40 Hz poll). `Static`
  semantics means nominal 0 Hz and a single write at discovery; `MetricSink.Register` enforces
  the 0. Never re-write a Static metric every poll.
- **Freshness contract: a value is fresh or absent, never stale-but-plausible.** Value timestamp
  0 = N/A. `ProviderHost` marks a failed provider's whole metric set N/A, and a watchdog thread
  does the same for any provider that has gone `max(10 s, 10 × its period)` without completing a
  poll. Widgets render an absent reading through `PanelContext.Na` (`Render\Elements.cs:145-151`),
  which replaces the **whole formatted string including its unit** — units are concatenated outside
  the number formatter, so the decision has to live one level up or a dead sensor reads `N/A °C`.
  Graph series use `NaSample` → `NaN`, which holds the previous bar rather than drawing a cliff to
  zero. The test for which a metric gets is **"is this a reading, or a fact about the machine?"** —
  readings go N/A, counts and booleans keep their zero. Fan RPM is the case to remember: a stopped
  fan genuinely reports 0, so only an *unreadable* sensor is N/A (`LhmProvider.cs:325-326`).
- **Frame rates come from summed intervals, not a count over a span.** Each frame carries its own
  present-to-present interval, so k frames carry k intervals and mean-frametime → rate is exact;
  under two samples there is no interval at all and the answer is 0/N/A, not the old `Math.Max(0.1,
  span)` floor's hard 10 fps (`FrameStats.Rate`). `fps.app.pid` does double duty: PCL Stats filters
  its markers on it, and the FPS panel uses it as the frame-graph `ResetKey`, so alt-tabbing to a
  non-presenting app clears the graph instead of freezing it. Click and all-input latency are a
  rolling **20 s window** (`PresentMonProvider.InputLatencyWindowS`), not a sample-count
  accumulator, and they register `RollingWindow` semantics with that `windowMs` so a reader sees it.
- **Config writes are cross-process, not just cross-thread.** Both the Settings app and the widget
  process write `widgets.json`/`settings.json`. A named interprocess mutex
  (`Local\Halo.Config.<key>.<file>`, 5 s timeout) spans the whole read → mutate → `File.Move`, and
  temp files are per-process-unique. The in-process `Lock` alone was never enough — two
  read-modify-write cycles interleaved and one process's change vanished. A config file that is
  present but does not parse is **not** treated as absent: last good copy kept, reason logged,
  writes to it refused rather than clobbering a hand edit (`ConfigStore.cs:139-155`).
- What each panel shows, which options it takes and which theme tokens it paints with is declared
  once in `Halo.Shared\Panels\PanelCatalog.cs`; the renderer and the Settings app both read it.
  Per-widget overrides live in `widgets.json` (`metrics`, `options`, `appearance`).
- Panel visual specs live in `tools\extracted\*.json` (faithful transcriptions of the
  original Rainmeter skins) — treat them as the source of truth for 1:1 parity.
- Frame data is two lanes. **Resolved lane** (plan D7): bundled PresentMon 2 service
  (copied to `<app root>\presentmon\`) spawned as a console-mode child — no SCM registration — and
  P/Invoked `PresentMonAPI2.dll`; ETW flush via `settings.json > collector.presentMonEtwFlushMs` (10 ms),
  40 Hz provider poll with stats published every poll (lows cached at 2 Hz); feeds the
  DISPLAYED panel + all fate-dependent metrics. The fps pipeline is idle-aware: 10 s
  without frames → service flush 100 ms + tap providers muted; frames re-arm it (~150 ms). FRAMETIME means on both panels are rolling
  100 ms; WORST is the 1 s max. There is no fallback transport — `collector.presentMonTransport`
  still takes `auto|sdk` and both mean the SDK (the console capture app is gone; its binary was
  never on disk). **Tap lane** (`PresentTap`, `collector.presentedTap`):
  own ETW session on the DXGI/D3D9 present-start events — no fate wait — feeding the
  PRESENTED panel live (1 s FPS, 100 ms frametime mean, `FrameFlags.Provisional` ring
  entries); Vulkan/OpenGL titles fall back to the resolved lane (`fps.tap.active`).
  Widgets repaint frame graphs on the `Local\Halo.FramesReady.v2` event (16 ms coalesce —
  matched to the 60 Hz widget monitor) with their tick as fallback. Smoketests: `--pm-smoketest [pid]`, `--tap-smoketest <pid>`.

## Gotchas

- Defender ML false-positive (2026-07-19): quarantined `Halo.Collector.csproj` as
  `Trojan:Win32/Bearfoos.A!ml`. If a file vanishes, check `Get-MpThreatDetection`, restore from
  git. **No Halo script offers a Defender exclusion** — the old `install-halo.ps1
  -AddDefenderExclusion` is gone along with that script, and nothing replaced it. Punching a hole
  in antivirus stays a manual, deliberate `Add-MpPreference -ExclusionPath` by whoever owns the
  machine.
- LHM CPU/SuperIO/Storage parts and PresentMon/PCL Stats (ETW) need elevation; unelevated they
  mark metrics N/A (or never register them at all) and the widgets render "N/A"/idle states.
  The provider table's `lastError` says which — `unelevated`, `no-driver`, `no-hw`, `no-nvml`,
  `no-sdk`, `failed`.
- Only one process can own the PresentMon ETW session, so a second collector's fps metrics read
  N/A while the production one runs. Expected, not a bug.
- **LHM's `OpCode`/`Mutexes` plumbing is process-global and not reference counted — and Halo has
  four `Computer` instances.** LHM 0.9.6's `Computer.Open()`/`Close()` call `OpCode.Open()`/
  `Close()` unconditionally; `OpCode` is an `internal static` class whose `Open()` VirtualAllocs
  **one** PAGE_EXECUTE_READWRITE page holding the rdtsc/cpuid stubs and points its static
  delegates at it, and whose `Close()` nulls those delegates and `MEM_RELEASE`s the page. Only
  `Computer._open` is per-instance. So **any** part's `Close()` breaks every other live part:
  `NullReferenceException` out of `GenericCpu.Update` / `GenericCpu.EstimateTimeStampCounterFrequency`
  (calling a null delegate), and `0xC0000005` when the page is freed while another thread is
  executing in it — uncatchable, process gone. `LhmProvider` obeys **two** rules, and it needs
  both: (1) serialise every LHM call behind its one static `ReaderWriterLockSlim` — write =
  `Open`/`Close`, read = `hw.Update()`, **never call LHM outside that gate**; and (2) **never
  leave a `Close()` unpaired** — every `Close` must be followed by an `Open` before the write lock
  is released, so no part ever exits the gate with LHM's globals shut. Rule 1 alone is not enough
  and looks like it is: measured 2026-09-14, with the gate in place but `Initialize`'s
  "no hardware found" path still closing, an unelevated `lhm-storage` retry broke `lhm-cpu`'s poll
  129 ms later. That is why an unavailable part keeps its empty `Computer` open until its next
  retry re-opens it. Diagnosed 2026-09-14 — the earlier note here blamed "re-`Open()` in quick
  succession" and `CpuId.Get`, both symptoms of this, and wrongly concluded the host's
  poll-failure retry path was safe. `Part.Cpu` still opts out of `rescan`, now only because it has
  nothing to re-enumerate.
- PawnIO (`C:\Program Files\PawnIO`) is a **shared** driver — FanControl, LHM and HWiNFO use the
  same one. Halo's installer offers it (`Halo.Settings.exe --install-pawnio`, exit 3010 = reboot
  needed) and never removes it on uninstall.
- `assets\fonts` has exactly one font left: `ElegantIcons.ttf` (GPL-2.0/MIT dual, MIT taken),
  loaded by the `*.ttf` glob in `Dx.LoadFonts` and used by name for the Drives/Network arrows.
  `SegMDL2.ttf` (Microsoft proprietary) and `MaterialIcons.ttf` (unreferenced) were deleted for the
  public release — see `THIRD-PARTY-NOTICES.md`.
- Licensing lives in three files at the root: `LICENSE` (MIT, Halo's code), `NOTICE.md` (the
  Rainformer design is CC BY-NC 3.0 — **non-commercial**), `THIRD-PARTY-NOTICES.md` (a row per
  shipped component, plus the GPL-2.0 text for PawnIO). Anything new that ships needs a row.
