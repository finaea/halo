# Halo — Hardware Analytics & Live Overlay

Native Windows 11 desktop widget suite replacing the Rainmeter + Rainformer + HWiNFO +
Afterburner + RTSS + NVIDIA App stack. Architecture overview: [architecture.md](docs/architecture.md).
Docs live in `docs/`; personal/machine-specific notes in `docs/private/` (git-ignored).

**Writing style for anything a user reads** (README, `docs/`, `NOTICE.md`, release notes in
`.github/workflows/release.yml`): third person, never "you"/"your"; plain everyday words, with
technical names (PawnIO, ETW, PresentMon, UAC) kept so the mechanism stays visible; softly
phrased rather than blunt; British spelling as elsewhere (colour, licence). The model is the
MacTime and MonitorScreenSaver READMEs. Code comments are exempt and stay technical.

## Build & run

```powershell
dotnet build Halo.sln -c Debug            # all projects (SDK pinned in global.json, targets net10.0)
dotnet test Halo.sln                      # tests\Halo.Tests — xUnit, no hardware needed
# run (order matters only for data availability):
src\Halo.Collector\bin\Debug\net10.0\win-x64\Halo.Collector.exe   # data process (full sensors need admin)
src\Halo.Widgets\bin\Debug\net10.0\win-x64\Halo.Widgets.exe        # widget windows + tray
src\Halo.Settings\bin\Debug\net10.0-windows\win-x64\Halo.Settings.exe
# diagnostics: dump every metric in shared memory (--json for the documented shape)
Halo.Collector.exe --dump [--json]
# DLSS state of a game over NVAPI, decoded (read-only, safe next to a running collector):
Halo.Collector.exe --ngx-smoketest [pid]
# one-time upgrade of a pre-v2 config folder:
Halo.Collector.exe --migrate-config <old config dir> [--to <dir>]
# release build: all three exes over ONE shared self-contained runtime, into dist\app
tools\build.ps1 [-Clean] [-Installer] [-Zip] [-NoReadyToRun] [-Output <dir>]   # -Installer needs Inno Setup 6
# one panel off-screen (WARP, fixed fixture) — how a visual change gets looked at:
Halo.Widgets.exe --render <out.png> --type cpu-ram --skin azur-archive --preset port-day --fixture idle|gaming|hot|na|partial --warp
# dev autostart against dist\app (one UAC prompt): tools\install-dev.ps1 · tools\uninstall-dev.ps1
# dev-only halo tuner: per-student halo pose, live; Save writes game-art\halos\<id>.json
python tools\halo-tuner\halo_tuner.py [--refs <portraits folder>]
# stop -> build -> start, in the only order that works: tools\redeploy-halo.ps1
```

**Tests** live in `tests\Halo.Tests` — the pure, I/O-free parts (frame-stat arithmetic, section
layout, provider freshness, config load/write) plus the logger (flush durability, envelope framing,
level filtering, rotation, debug shedding, session classification) and the skins (Rainformer
pixel goldens, preset contrast, theme resolution, config v3 migration, motion). Anything needing live hardware
or the PresentMon ETW session stays a hand check (`--pm-smoketest`, `--tap-smoketest`). The project
is in the solution, so `dotnet build` and `dotnet test` both pick it up, and deliberately **not** in
`build.ps1`'s publish list — that script names the three shipping exes one by one, so nothing here
can reach `dist\app`. `OutputType=Exe` + `GenerateProgramFile=false` + the hand-written
`Program.cs` are **one unit**: the cross-process config test spawns a second copy of itself and a
default test project produces no apphost. Removing any of the three gives CS5001.
The test project targets `net10.0-windows` with `UseWPF` and references Halo.Settings, so the
settings write-queue tests (`SettingsWriteFailureTests`) can drive its view-models; its output is
`tests\Halo.Tests\bin\...\net10.0-windows\win-x64` (where `portable.marker` and `golden-failures`
land). WPF bodies run on one STA thread that owns the single `Application`, and `UseWPF` drops
`System.IO` from the implicit usings, so the csproj adds it back.

Two things about the log tests specifically. **`tests\Halo.Tests\portable.marker` is load-bearing**
(a `Content` item in the csproj): `SessionLog` has no path seam and `Paths.DataDir` has no setter,
so without it `SessionLog.Begin` would reclassify, rewrite and prune the **real** installation's
session records on the dev machine every time the suite ran. And the log classes share one
xUnit collection with `DisableParallelization` — `Log` is a single path/queue/pump/counter set and
`ConfigStore` logs too, so parallel classes corrupt each other's counters. Teardown pushes a
sentinel through and waits for it on disk, because `ResetForTests` cannot reach a batch the pump has
already taken and that batch's count would otherwise land on the next test's zeroed counters.

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
  (`reset-max`, `reset-net`, `rescan`, `reload-config`, `ping`, `quit` — the only stop that runs
  the shutdown path; the tray's Exit sends it); `rescan` re-runs `Initialize` on
  every provider whose `RescanReinitialises` is true (the ones that enumerate hardware) and drops
  repeats inside 10 s. `LhmProvider.Part.Cpu` opts out on purpose — see the LHM gotcha below.
- **Halo.Widgets**: one WS_EX_NOREDIRECTIONBITMAP HWND per widget, DirectComposition +
  D2D on a shared D3D11 device (`Dx`). Panels are element trees (`Render\Elements.cs`)
  in a Rainmeter-like flow layout (`Render\Panel.cs`), built per type by the skin behind
  `ISkin` (`Skins\Rainformer\`, hand-built; `Skins\AzurArchive\`, a block layout over the skin-neutral `PanelModels\`). Dirty-driven redraw. Drag/snap(8px)/z-modes/click-through/opacity/context menu in
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
- Colors only via `Theme` tokens (Rainformer's base palette was extracted from the Rainformer skin's
  `@Resources\Variables.inc` — values only, no GPL code). The skin is **not vendored here**: a
  reference copy comes from a Rainmeter install of Rainformer 3.1 HWiNFO Edition, under
  `%USERPROFILE%\Documents\Rainmeter\Skins\`. Staged warn colors: `PanelData.WarnColor`
  (`PanelModels\`), shared by every skin.
- **Skins: a skin owns how a panel looks, never what it measures.** Metrics, options, warn
  thresholds and N/A rules stay in `PanelCatalog` + `PanelModels\`; a skin owns tokens, presets,
  chrome, layout, fonts, image slots and its own options. Two halves: `SkinInfo` in
  `Halo.Shared\Skins\SkinCatalog.cs` (metadata, read by Settings too — first preset is the default,
  every skin ships a `HighContrast` one) and `ISkin` in `Halo.Widgets\Skins\` (`Build(type, ctx)` →
  element tree + `ICardChrome`), mapped by `SkinRegistry`; an unknown id draws Rainformer and stays
  in the file. The 31 core tokens are a cross-skin contract (same ids, same meaning — they are in
  users' configs); skins add extras. Rainformer stays hand-built and pinned by the goldens in
  `tests\Halo.Tests\goldens\rainformer\` (regenerate only goldens that intentionally change, from
  `bin\...\golden-failures\`, never a blanket `HALO_UPDATE_GOLDENS=1`); block-layout skins build from
  `PanelModels\`. Graphs are always a top-level `GraphEl` (frame wakes and graph settings key on the
  type) — a skin restyles them through `GraphVisual`, never wraps them.
- **Config v3 appearance is per skin.** `appearance.skin` + `appearance.skins.<id>.{preset, colors,
  options}` in `settings.json`, the same shape (every field nullable = inherit) under each widget's
  `appearance` in `widgets.json`. `Theme.Resolve` is the one resolution order: base ← preset ←
  global colours (only if the widget picks no preset of its own) ← widget colours ← `metric:<key>`.
  `colors` is always the **active** preset's tweaks; `presetColors` parks every other preset's
  (a widget's "Inherit" has its own slot) and only Settings reads it — one swap,
  `SkinSettings.SwitchPreset`, used by both pages. HC under Windows HC re-derives, never restores.
  Placement is per skin the same way: a widget's live `monitor/x/y/enabled` are its **effective**
  skin's, `placements` parks the other skins' (`WidgetInstance.SwitchSkin`, global via
  `WidgetsConfig.SwitchGlobalSkin`); only Settings swaps, widgets.json flushed before settings.json,
  each switch queued under its own path so two cannot coalesce. A skin with nothing parked keeps
  the live placement.
  **Profiles** (`ProfileStore`, `config\profiles\<id>.json`, which no watcher sees) hold lock all,
  snap, all of `appearance` and all of widgets.json — **never** `collector.*` or `diagnostics`.
  Auto-save: the active profile (`settings.json > activeProfile`) *is* the live files, and its file
  is only refreshed when another is selected. A switch (`ProfileService`) swaps both files as one
  queued mutation each, widgets first, capturing the outgoing state inside the same transaction;
  it does not go through `SwitchGlobalSkin` (the incoming placement already matches its skin).
  v2 files upgrade in memory (`SchemaV3.Upgrade`) to `rainformer-light` + every differing colour as
  a tweak. Option keys a skin does not declare are kept in the file and ignored (`textSizePt` is one:
  it never reached the renderer). Hot reload compares **resolved** themes (`Theme.RebuildReason`):
  skin, font family or a `Structural` option rebuilds, everything else applies through `CopyFrom`.
- **Contrast gate.** `PresetContrastTests` checks every non-exempt preset against its skin's
  `ContrastPairs`: text 4.5:1 with the card composited over black, #808080 and white wallpaper,
  alert text 4.5:1 and graphics/warn ramp 3:1 on grey. `rainformer-light` is `ContrastExempt` — it
  is today's palette byte for byte, the parity baseline. One pair can also exempt named presets
  (`ContrastPair.ExemptPresets`): Azur's FPS app tag keeps white on Port Day's and Shittim's bright
  blue by Jack's choice (2.62:1). Opacity < 1 voids the guarantee; Settings says so next to the
  opacity slider rather than clamping.
- **Game art is isolated, and never required.** Azur Archive's character art lives only under
  `assets\skins\azur-archive\game-art\` with a `CREDITS.md` row per file; art-bearing renders
  (gallery previews) are written **inside** that folder, art-free ones to `previews\`. Every skin must
  render with `game-art\` deleted (`SkinAssets`: user-art → bundled → nothing, never a placeholder).
  Per-student halos are data there too (`game-art\halos\<id>.json`, `HaloShape`); missing or
  unreadable → the generic ring. The `halo` skin option hides them; the clock card never draws one.
  README/docs screenshots are art-free only — a screenshot with a character in it would escape the
  folder. Removal steps: `docs\game-art-removal.md`.
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
  fan genuinely reports 0, so only an *unreadable* sensor is N/A (`LhmProvider.cs:377-378`).
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
  writes to it refused rather than clobbering a hand edit (`ConfigStore.Load` → `NoteUnreadable`).
  In Settings, a write that touches **both** files (profile switch, global skin switch, Reset
  appearance) flushes them one at a time with `LiveConfigService.FlushFileAsync`, which returns
  whether that file committed; a failed half is `CancelPending`'d and a committed half undone. A
  mutation having *run* proves nothing — it runs on the in-memory document before the save that
  can still throw (the store reloads from disk when it does).
- **Logging is evidence, so it is allowed to cost something.** `Halo.Shared\Log.cs` writes
  **one file per process instance** (`logs\<proc>-<yyyyMMdd-HHmmss>-<pid>.log`) — a shared per-day
  file could not survive more than one writer, and five collectors once interleaved into one file
  with no way to tell their lines apart. Records are
  `<ISO 8601 + offset> <LVL> <sessionId> <component> <message>`, and a multi-line stack trace
  repeats the whole envelope with a `+` marker so no physical line is ever bare. The component
  column pads but never truncates, so **there is one guaranteed space after it** — a name longer
  than 12 characters used to run into the `+`.
  `Log.Durable(level, msg)` writes **synchronously**, through the same stream and lock as the pump;
  use it for anything whose value is surviving the next instruction (crash handlers, breadcrumbs
  around native calls that can end the process). **Durability is not severity** — a routine
  breadcrumb is `Debug`, not an error. Only `Warn`/`Error` wait for the queue to drain first:
  `LhmProvider` crumbs every `hw.Update()` at 5 Hz inside LHM's read gate, and making those wait
  would stall polling until the freshness watchdog marked the provider N/A.
  **The accounting rule, because breaking it makes `Log.Flush` lie:** `_accepted` counts records
  the logger took responsibility for, and only persistence or `_droppedWriteFailed` discharges one.
  Records shed or refused *before* acceptance never enter either side. `Flush` and `DrainBefore`
  share one `Outstanding` definition on purpose; they each had their own copy once, and `Flush`
  reported success with 6015 records still in the queue.
  Level comes from `settings.json > diagnostics.logLevel`, and **`HALO_LOG_LEVEL` overrides it** —
  the escape hatch for a config file that will not parse, which is exactly when you need verbose
  logging. `Log.LevelPinnedByEnv` exists so the Settings toggle can admit when it is not in charge.
- **`SessionLog` answers "why is this process gone", and its identity is pid AND OS process start
  time.** Pids are reused, so pid alone would let a fresh instance declare a **live sibling**
  crashed. A process that cannot be queried is `Unknown`, never `Unclean` — "I could not look" is
  not evidence of a crash. Whoever kills another process calls
  `SessionLog.RecordExternalStop(pid, reason)` first (the widgets watchdog before `schtasks /End`),
  because the victim gets no chance to record its own shutdown and a restart must not read as a
  fault. `SessionLog.SetPhase` is **not** level-gated: the last phase written is what a native
  crash or a hard kill leaves behind.
- **Motion never animates readings.** `Motion.Resolve` caps `appearance.motion` (off/subtle/full)
  by Windows' Animation effects setting, re-read on `WM_SETTINGCHANGE`. State changes key a
  `Transition`; its 60 Hz `AnimFrame`s redraw without `Panel.Update` or metric reads, with
  `NowQpc` frozen. Key a transition on state, never on a reading. Motion off schedules no animation
  frames. Up to two `AmbientLoop`s per widget (`AmbientLoop.MaxPerWidget`; the network card's two
  seats) run only at full motion and pause while a game presents. Each has its own cached DComp
  visual; App moves them all at `appearance.motionFps` (15/24/30/60, default 30, read live) and
  commits once. Keep the loops app-driven: a DComp animation makes DWM recompose at the monitor's
  refresh rate. Rainformer has no entrances or transitions.
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
- **DLSS rows come from NVAPI first, the DLL scan second** (`NgxProvider`). An NVIDIA App
  override loads DLSS from a `.bin` under `ProgramData\NVIDIA\NGX\models`, so a module scan sees
  nothing (Cyberpunk 2077 ran RR and FG while the old scan said "not loaded"). Three rules: NVAPI
  is **reference-counted and shared with the LHM GPU part** in this process, so exactly one
  `NvAPI_Initialize`/`NvAPI_Unload` pair per `NvApi` instance; "active" keys on the `CREATED`
  flag, never `EVALUATE`, which flickers with dynamic frame generation; and the runtime
  `performanceMode` (3 = Ultra Performance) is **not** numbered like the DRS override setting
  (3 = in-game). Preset and mode exist only while an override applies them — nothing public
  reports a game's own choice.
- PawnIO (`C:\Program Files\PawnIO`) is a **shared** driver — FanControl, LHM and HWiNFO use the
  same one. Halo's installer offers it (`Halo.Settings.exe --install-pawnio`, exit 3010 = reboot
  needed) and never removes it on uninstall.
- `assets\fonts` has exactly one font left: `ElegantIcons.ttf` (GPL-2.0/MIT dual, MIT taken),
  loaded by the `*.ttf` glob in `Dx.LoadFonts` and used by name for the Drives/Network arrows.
  `SegMDL2.ttf` (Microsoft proprietary) and `MaterialIcons.ttf` (unreferenced) were deleted for the
  public release — see `THIRD-PARTY-NOTICES.md`.
- Licensing lives in three files at the root: `LICENSE` (PolyForm Noncommercial 1.0.0, Halo's
  code — the same licence as MacTime and MonitorScreenSaver), `NOTICE.md` (the Rainformer design
  is CC BY-NC 3.0 — also **non-commercial**), `THIRD-PARTY-NOTICES.md` (a row per shipped
  component, plus the GPL-2.0 text for PawnIO). Anything new that ships needs a row. `LICENSE` is
  the verbatim PolyForm text plus its `Required Notice:` line — keep it that way; what it does
  and does not cover is explained in the README and `NOTICE.md`, not appended to it.
