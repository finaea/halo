# Halo — Native Desktop Widget Implementation Plan

**HALO = Hardware Analytics & Live Overlay** (named for the halos on the wallpaper)

**Date:** 2026-07-19 · Supersedes the "InfoPanel first" interim plan — we skip straight to the final phase: a native, efficient, self-sufficient replacement for the Rainmeter + Rainformer + HWiNFO + Afterburner + RTSS + NVIDIA App stack.

**Companion docs:** [current-metrics-inventory.md](current-metrics-inventory.md) (the ~85 metrics to replicate) · [global-installs.md](global-installs.md) (dependencies + new-PC setup). *(Pre-build research docs — widget comparison, conversation handoff, `memory/` notes — were retired 2026-07-20; see git history if needed.)*

---

## 1. Goal & non-goals

**Goal:** A Windows 11 desktop widget suite, visually 1:1 with the current Rainformer setup (the 10 panels in the reference screenshot), self-sourcing all data (no HWiNFO / Afterburner / RTSS / NVIDIA App), pollable up to 100 Hz with per-metric auto-capping, dramatically more efficient than Rainmeter at high refresh, with Rainmeter-grade window management (drag / snap / multi-monitor / lock) and a Rainformer-style settings UI.

**Non-goals (this phase):**
- In-game overlay rendering (the collector is designed so one can attach later)
- The 10 non-screenshot Rainformer skins (Weather, Calendar, Volume, Battery, Astronomy, PSU, Temps, TopGPU, RecycleBin, PowerPlan, Launcher) — stretch, after parity
- The anime wallpaper (that's Wallpaper Engine / static wallpaper, not our app)
- Skinning ecosystem for third parties (config-driven theming yes; arbitrary user skins no)

## 2. Decisions made (and challenges to your spec)

| # | Decision | Rationale / where I pushed back |
|---|---|---|
| D1 | **C# (.NET 8 LTS) for both processes** — confirmed with you | LHM reuse (C#, MPL-2.0) outweighs C++; efficiency at this workload is architecture, not language. Renderer hot path written allocation-free; NativeAOT for the widget process. If measured perf ever disappoints, the renderer is isolated enough to port to C++ without touching the collector. |
| D2 | **Two processes: Collector (elevated) + Widgets (user, AOT)** + on-demand Settings window | Privilege separation (PawnIO/MSR needs admin; widgets shouldn't run elevated), crash isolation (req 8), and the collector doubles as the future in-game-overlay feed. |
| D3 | **100 Hz is a graph feature, not a number feature** | Text updating >10 Hz is unreadable flicker. Default 10 Hz everywhere; per-widget override up to 100 Hz *for graphs*; numbers stay ≤10 Hz. Frame data isn't polled at all — PresentMon pushes per-frame events (better than 100 Hz). |
| D4 | **Hardware sensors physically can't do 100 Hz** — your req 1 auto-cap is mandatory, not optional | See rate table §5. EC/SuperIO reads are ~ms-scale port I/O behind mutexes; SMART is ~10s of ms; NVML calls ~0.2–1 ms each. The scheduler clamps per sensor class and surfaces the effective rate in the UI. |
| D5 | **GPL code reuse allowed — user confirmed personal-use-only.** InfoPanel (GPL-3.0) code may be copied/repurposed directly (LHM host, plugin patterns, USB-panel bits); LHM = linked library (MPL-2.0); PresentMon = SDK client (MIT). | User decision 2026-07-19: this app is personal, never distributed. Consequence recorded: the codebase becomes GPL-encumbered — if a commercial ambition ever returns, GPL-derived modules must be rewritten (they're isolated behind `ISensorProvider`/renderer seams to keep that possible). |
| D6 | **Two FPS widgets via instantiable widget types** | Req 10 (separate Presented and Displayed windows) falls out free: any widget type can be instantiated N times with independent config, like Rainmeter skins. |
| D7 | **PresentMon consumed as a client of the Intel PresentMon service** | Never own a raw ETW session — avoids the documented conflicts with HWiNFO/CapFrameX/FrameView if they ever run. Service binaries bundled with our installer (MIT allows redistribution). |
| D8 | **Config = JSON, hot-reloaded** — not INI | Rainformer's settings *surface* is replicated (§10), its INI format is not. A one-time converter seeds our theme from `Styles.inc` / `SettingsGeneral` variables. |

## 3. Requirements traceability

| Your req | Where addressed |
|---|---|
| 1. 100 Hz capable, 10 Hz default, per-metric auto-cap | §5 rate table, §6 scheduler |
| 2. Efficient language | D1, §8 budgets |
| 3. Win11 + this PC, flexible for AMD/updates | §4 provider abstraction, §13 portability |
| 4. 1:1 Rainformer visuals + similar settings UI | §9, §10 |
| 5. Reuse components, own the foundation | D5, §12 reuse map |
| 6. Desktop widget: drag/snap/multi-monitor | §9.3 |
| 7. No RTSS/HWiNFO/etc. | §4 (all first-party providers) |
| 8. Start with login | §11 |
| 9. Survive monitor sleep etc. | §11 resilience matrix |
| 10. FPS + Power as true first-class sections | §7 |
| 11. Two FPS windows (Presented / Displayed) | D6, §7 |
| 12. Same ~85 metrics | [inventory](current-metrics-inventory.md), §9.1 |
| 13. Rainformer-equivalent settings + graph series toggles | §10 |

## 4. Architecture

```
┌──────────────────────────────── Halo.Collector.exe (elevated, .NET 8) ────────┐
│  ProviderHost + scheduler (per-provider cadence, auto-cap)                     │
│  ┌────────────┬──────────┬───────────────┬──────────────┬─────────────┐        │
│  │ PresentMon │  NVML    │ LHM (PawnIO)  │  PDH/Win32   │  Builtin    │        │
│  │ SDK client │ (GPU)    │ CPU/mobo/SMART│ cores/procs/ │ IP, uptime  │        │
│  │ per-frame  │          │               │ net/disk     │             │        │
│  └────────────┴──────────┴───────────────┴──────────────┴─────────────┘        │
│  StatsEngine: session max/peaks, windowed 1%/0.1% lows, worst-frametime,       │
│               rolling ring buffers (graphs), staleness stamps                  │
│  ▼ writes                                                                      │
│  Shared memory  «Halo.Metrics.v1»  (seqlock, double-buffered values            │
│                 + per-frame ring + metric registry)                            │
└─────────────────────────────────────────────────────────────────────────────────┘
        ▲ reads (lock-free)                          ▲ reads
┌───────┴──────────────────────────────┐   ┌─────────┴──────────────┐
│ Halo.Widgets.exe                     │   │ (future) in-game       │
│ (per-user, NativeAOT)                │   │ overlay renderer       │
│ Win32 + Direct2D + DirectComposition │   └────────────────────────┘
│ one HWND per widget; drag/snap/lock  │
│ tray icon; watchdog for collector    │
└──────────┬───────────────────────────┘
           │ launches on demand
┌──────────┴───────────────────────────┐
│ Halo.Settings.exe (WPF, JIT)         │  ← writes JSON config; both processes hot-reload
└──────────────────────────────────────┘
```

- **`ISensorProvider`** (the maintainability contract from the handoff): `Initialize()`, `Enumerate() → SensorDescriptor[]`, `Poll(ids) → values`, `MaxRateHz(sensorClass)`, `Dispose()`. Vendor swaps (NVML→ADLX/IGCL for a future AMD/Intel GPU, LHM version bumps, Windows API changes) are one-module changes.
- **Shared memory layout:** header (magic, version, seqlock counter) · metric registry (stable string-hash IDs, type, unit, effective rate, staleness QPC) · double-buffered value block · **frame ring** (8192 × {QPC, frametime µs, flags: presented/displayed/dropped/generated, PID}) so graph consumers get every frame, not samples.
- **Why shared memory (not pipes):** widgets read lock-free at their own cadence; N consumers free; it *is* the MAHM/RTSS pattern we spent the sessions reverse-engineering — proven for exactly this.

## 5. Providers & rate ceilings (req 1's auto-cap, made concrete)

| Provider | Metrics (from inventory) | Cost/read | **Hard cap** | Default |
|---|---|---|---|---|
| PresentMon (SDK client) | FPS presented/displayed, frametime, lows, GPU-Busy | event-driven push | per-frame (no poll) | per-frame → aggregated per widget tick |
| PresentMon latency | **Click-to-Photon, All-Input-to-Photon (full PC latency, no game integration)** | event-driven push | per-input-event | non-zero-avg per widget tick |
| NGX module inspector | DLSS SR/FG/RR loaded? + exact DLSS version (module enum of game PID — read-only, no injection) | ~ms module snapshot | **0.5 Hz** | on game start + 10 s |
| *(optional slot)* FrameView SDK | true Reflex PCL for Reflex games | — | — | not built unless wanted |
| PDH: CPU total + 20 cores | usage % | µs, but counters update per ~15.6 ms kernel tick | **64 Hz** (beyond = duplicate reads) | 10 Hz |
| PDH/IpHlpApi: net, disk I/O, RAM | rates, usage | µs | 64 Hz | 10 Hz |
| Process snapshot (Top CPU/RAM, count) | top-5 lists ×2, count | ~ms (371 procs) | **2 Hz** | 1 Hz |
| NVML | GPU temp/use/VRAM/fan/clock/power/volt | 0.2–1 ms per call | **20 Hz** | 10 Hz |
| LHM: CPU MSR (temp, power, Vcore, clock) | package temp, W, V, MHz | fast ioctl | **20 Hz** | 10 Hz |
| LHM: SuperIO/EC (case+CPU fans, mobo V) | fan RPM/%, Vcore | ~ms port I/O + mutex | **2 Hz** | 1 Hz |
| LHM: drive SMART/NVMe temps | 7 drive temps | 10s of ms per drive | **0.2 Hz** | 1/30 s |
| Builtin: time/uptime | clock panel | free | widget-local | widget tick |
| Builtin: IPs, ext. IP | network panel | web fetch (ext.) | on network-change event | + 5 min refresh |

Scheduler: each provider runs on its own cadence on the collector's thread pool; a widget asking for 100 Hz gets 100 Hz *reads of the freshest value*, stamped with staleness — the UI shows "effective rate" per metric (your req 1 behavior, visible instead of silent).

## 6. Stats engine (fixing the "badly implemented custom" sections — req 9)

All derived stats live in the **collector** (not skin math), documented and windowed explicitly:

- **Session max/min** (Power panel's Max Vcore/W, Network peaks): running extrema since collector start, with per-panel reset action.
- **Frame stats** (per game, keyed by foreground PID): true **1% / 0.1% lows** computed over a *defined* rolling window (default 60 s, configurable — unlike Afterburner's "since reset" ambiguity), **worst frametime per widget tick** (so 10 Hz display still exposes every spike), avg FPS presented & displayed, frame-gen ratio (displayed:presented).
- **Rolling ring buffers** per graphed metric (configurable depth, default 10 min @ effective rate) — graphs render from these, so a 10 Hz widget can still draw a per-frame frametime graph segment.
- Everything stamped with window definition + staleness → the settings UI can *show* what a number means (goodbye undocumented custom measures).

## 7. First-class FPS & Power sections

**FPS widget type** (instantiable — you'll create two):
- Instance A "FPS COUNTER (PRESENTED)": presented FPS, % of refresh (reads actual monitor refresh, not hardcoded 144), 1% low, worst frametime, frametime graph — all from the presented stream.
- Instance B "FPS COUNTER (DISPLAYED)": same layout from the displayed stream + frame-gen ratio badge (so ZZZ frame-gen reads honestly).
- **Latency & upscaler telemetry (user req 2026-07-19):** PC latency line (PresentMon Click-to-Photon avg + All-Input-to-Photon, rolling window, non-zero averaging) and a DLSS badge row — SR/FG/RR presence + DLL version from the NGX module inspector, frame-gen multiplier (displayed:presented, e.g. "FG 2.0×"). **Accepted residual gaps:** exact DLSS preset/mode (needs NGX hooking — out of scope) and marker-based Reflex PCL (optional FrameView SDK slot; Click-to-Photon answers the same question vendor-neutrally).
- Target-app logic: foreground fullscreen/borderless 3D app via PresentMon PID tracking; "no 3D app" state renders a dimmed idle panel (fixes the 618 ms desktop artifact in your screenshot).

**Power widget type:** Vcore (LHM SIO) + GPU V (NVML) + CPU package W (LHM MSR) + GPU board W (NVML), each current + session-max with documented semantics and a reset button — replacing the undocumented Rainmeter measures.

## 8. Rendering engine & performance budgets

- One **HWND per widget**: `WS_EX_NOREDIRECTIONBITMAP` + DirectComposition visual + D2D device context on a **shared D3D11 device**; per-monitor-v2 DPI aware.
- **Dirty-driven drawing:** a widget redraws only when (a) its tick fired AND (b) a displayed value actually changed, or (c) window system events demand it. Idle desktop = 0% CPU.
- **Cached text:** `IDWriteTextLayout` cached per string; numeric fields update via layout swap only when the rounded display value changes. No per-tick re-parse of anything (the Rainmeter sin).
- **Graphs:** polyline from ring buffer, decimated to widget pixel width; geometry realized only on resize.
- **Budgets (measured, enforced in CI-style perf test):**

| State | CPU (of one core) | GPU | RAM |
|---|---|---|---|
| Idle (nothing changed) | ~0% | ~0% | — |
| All 11 widgets @ 10 Hz | **< 0.5%** | < 1% | Widgets ≤ 60 MB (AOT) |
| Graphs @ 100 Hz | < 3% | < 3% | Collector ≤ 80 MB |
| Collector polling (defaults) | < 1% | — | — |

Startup < 500 ms to first paint. If any budget fails by >2× after tuning → escalate that module to C++ (D1 exit clause).

## 9. Widgets & window management

### 9.1 The 11 windows (10 panel types, FPS ×2)
Clock/Uptime · Power · Drives (×7 volumes) · FPS-Presented · FPS-Displayed · GPU (RTX 5070 Ti) · Fans · Network · CPU-RAM (20 cores, P/E grouping preserved: bars 1–12 P, 13–20 E) · Top CPU · Top RAM — every metric per the [inventory](current-metrics-inventory.md).

### 9.2 Visual parity ("1:1")
- Extract exact colors, fonts, sizes, spacing, bar/graph styles from `RainformerHWiv2\@Resources\Styles.inc`, `SettingsGeneral.ini` variables, and each panel's `.ini` — build-time converter emits our default theme JSON. Values are facts; no GPL code copied.
- Fonts: skin uses icon fonts (`MaterialIcons.ttf` Apache-2.0 ✓, `SegMDL2` = ship-with-Windows Segoe MDL2 Assets — reference system font, don't bundle; `ElegantIcons` — check license before bundling, personal use fine) + whatever text face Rainformer sets (extract; likely a common free font).
- Acceptance test: screenshot overlay diff vs Rainmeter at same position ≤ minor antialiasing deltas.

### 9.3 Window behaviors (Rainmeter parity — req 6)
- **Drag** anywhere on widget when unlocked; **lock/unlock** global + per-widget (tray + context menu).
- **Snap:** screen edges + other StatsMonitor widgets, 8 px threshold (Rainmeter SnapEdges behavior).
- **Multi-monitor:** positions stored monitor-relative (monitor stable ID + offset); monitor removed → widget migrates to nearest with position remembered for the monitor's return; DPI change → re-layout, not bitmap-stretch.
- **Z-order modes** per widget: On Desktop (default — bottom-of-z, above wallpaper, survives Win+D via Progman/WorkerW parenting), Normal, Always-on-top.
- **Click-through** option, **keep-on-screen** option, per-widget **opacity** — matching the Rainmeter context-menu feature set.
- Context menu per widget: lock, z-mode, opacity, refresh, open its settings page.

## 10. Settings UI (Rainformer-equivalent + our additions — reqs 12, 13)

Replicates the `Settings\SettingsGeneral + SettingsSkins1-6` surface, reorganized:
- **General:** theme colors (color-picker like ColorPickerPlus), fonts/scale, global update rate, start-with-login, lock all.
- **Per-widget pages** (like SettingsSkins tabs): show/hide widget, position (or drag), per-widget rate override (10→100 Hz), panel-specific options (drive letters list, network adapter pick, refresh-rate source, FPS window length for lows, session-max reset).
- **New — graph composer (your req 13):** per graph, toggle each series (e.g. frametime line only), colors per series, window length, min/max autoscale vs fixed, per-frame vs sampled source.
- **New — metrics transparency page:** every metric with source provider, effective rate, staleness, window definition — the anti-"undocumented custom measure" page.
- Settings writes JSON → both processes hot-reload (file watcher); no restart for any visual/rate change.

## 11. Lifecycle & resilience (reqs 7, 8)

**Start with login:** Widgets.exe via `HKCU\...\Run`; Collector via **Task Scheduler task** (highest privileges, at-logon, no UAC prompt) — the standard pattern for elevated autostart. Installer creates both; settings toggle manages them.

**Watchdog:** Widgets monitors collector heartbeat (shared-mem timestamp). Stale > 5 s → widgets show per-panel "stale" badge, attempt collector restart (backoff 1/5/30 s). Collector crash never crashes widgets; widgets crash never touches collector.

**Resilience matrix (each = a chaos test in §14):**

| Event | Handling |
|---|---|
| Monitor sleep / display off | DXGI occlusion + `WM_DISPLAYCHANGE` → suspend rendering (0% CPU), resume on wake |
| Monitor unplug / re-plug | migrate to nearest monitor; restore saved position when monitor returns |
| DPI / resolution change | `WM_DPICHANGED` → re-layout from theme units |
| D3D device lost / driver reset / driver update | recreate device + resources, backoff retry; never terminate |
| Sleep/hibernate resume | collector re-inits NVML + PresentMon session; providers re-enumerate |
| Session lock / RDP | pause render; PresentMon data marked stale |
| PresentMon service missing/stopped | FPS widgets show "service unavailable", auto-retry; rest of app unaffected |
| PawnIO/LHM init failure (Defender, driver blocked) | LHM sensors → "N/A", one-time toast with fix link; app runs on |
| Single sensor disappears (e.g. drive removed) | per-metric N/A + registry update; widgets rebind gracefully |
| GPU swap NVIDIA→AMD (req 3) | NVML provider fails to init → ADLX provider slot (interface ready, implementation later); everything else untouched |
| Windows feature update | only kernel-touching dep is PawnIO (maintained upstream); PresentMon service versioned; providers isolate API breaks |

## 12. Reuse map & licenses

| Component | License | How we use it |
|---|---|---|
| LibreHardwareMonitorLib (+ PawnIO driver, installed separately like LHM does) | MPL-2.0 / PawnIO upstream | **Linked library** — CPU MSR, SuperIO fans, SMART. File-level copyleft only; safe even commercially |
| PresentMon service + SDK (`PresentMonAPI2.dll`) | MIT | **Bundled + P/Invoked**; we are a service client |
| NVML (`nvml.dll` ships with driver) | NVIDIA SDK EULA (redistributable headers) | P/Invoke |
| PDH / IpHlpApi / DXGI / D2D / DWrite / DComp | Windows | P/Invoke (CsWin32-generated) |
| InfoPanel | GPL-3.0 | **Code copy/repurpose allowed** (personal-use decision, D5) — LHM hosting, plugin patterns; keep GPL-derived code behind module seams |
| Rainmeter / Rainformer | GPL-2.0 / skin author's terms | No code; theme *values* extracted from our own installed config; audit skin assets before any distribution |
| Vortice.Windows or TerraFX (D3D/D2D bindings) | MIT | Renderer bindings (AOT-compatible) |

## 13. What flexibility for future hardware looks like (req 3)

- AMD CPU: LHM already covers Ryzen MSR/SMU — zero code change.
- AMD/Intel GPU: `ISensorProvider` slot for ADLX / IGCL; FPS path (PresentMon) is vendor-neutral already.
- New Windows: only PawnIO (upstream-maintained) and PresentMon service (Intel-maintained) touch fragile surfaces; both are exactly the "absorb the churn for you" choices from the handoff research.

## 14. Build order & milestones

Skipping the interim phases ≠ skipping build order — this is the dependency sequence:

| M | Deliverable | Key risk retired |
|---|---|---|
| **M0 — Spikes (go/no-go)** | (a) ~~fans/SIO~~ **RESOLVED 2026-07-19 via FanControl config**: board SIO = Nuvoton **NCT6687D**, fully supported by LHM-on-PawnIO (8 fan channels incl. CPU/Pump/System 1–6 — FanControl v240 reads them today). Residual: verify **7 drive temps** via LHM Storage module (FanControl had storage disabled by choice); (b) PresentMon SDK attach + read displayed/presented on ZZZ with frame-gen; (c) DComp transparent widget POC with budget measurement | The remaining unknowns |
| **M1 — Foundation** | Shared-mem schema + seqlock, ProviderHost + scheduler + auto-cap, StatsEngine, JSON config + hot-reload, logging | Architecture |
| **M2 — Providers** | PDH, NVML, LHM, PresentMon, Builtin — all inventory metrics flowing, verified against old stack side-by-side | Data correctness |
| **M3 — Render + windowing** | Widget host, D2D pipeline, drag/snap/z-order/multi-monitor/DPI, tray | UX foundation |
| **M4 — The 11 widgets** | All panels, theme extracted from Rainformer, screenshot-diff parity pass | Req 4/11 |
| **M5 — Settings UI** | WPF settings app, graph composer, metrics transparency page | Req 12/13 |
| **M6 — Hardening** | Full resilience matrix as chaos tests (monitor off/unplug, driver reset, sleep, kill collector, stop PresentMon service…), login autostart, watchdog | Req 8 |
| **M7 — Cutover** | A/B week vs Rainmeter stack; perf budget verification; installer (bundles PresentMon service, fetches PawnIO); retire old stack | Done |

**M0 is this week's work and decides everything** — especially (a): if LHM can't read this board's SuperIO fans, the fallback ladder is: contribute the chip to LHM (it's exactly what the project accepts) → or keep HWiNFO *only* for fans while everything else migrates (degraded but acceptable personal-use compromise).

## 15. Dependency & install policy (portable-first)

**User requirement (2026-07-19):** deleting this project folder must clear Halo out entirely.

- Everything project-local: source, NuGet cache (redirected via `nuget.config`), self-contained published binaries, PresentMon service+SDK binaries, config, logs — see layout in [global-installs.md](global-installs.md).
- Only three global *registrations* (all pointing at files inside the project folder): the PresentMon service SCM entry, the `\Halo\Collector` scheduled task, and the `HaloWidgets` HKCU Run value. Each is manifested in [global-installs.md](global-installs.md) the moment it's created, with its cleanup command.
- `tools\uninstall-halo.ps1` (M1 deliverable) removes all global registrations; then folder deletion = complete removal.
- Pre-existing system components we use but never touch: .NET SDK 9 (`C:\Program Files\dotnet`), **PawnIO (`C:\Program Files\PawnIO` — owned by FanControl, never uninstall with Halo)**.
- Rule going forward: **no tool, package, or registration is added outside this folder without a row in the manifest.**

## 16. Resolved decisions (2026-07-19)

1. **GPU scope:** single GPU — RTX 5070 Ti only. `ISensorProvider` keeps the second-GPU/ADLX slot open but nothing is built for it.
2. **App name:** **Halo** (HALO = Hardware Analytics & Live Overlay). Binaries `Halo.Collector.exe` / `Halo.Widgets.exe` / `Halo.Settings.exe`; shared memory `Halo.Metrics.v1`; scheduled task `Halo Collector`.
3. **Frame-lows window:** rolling **60 s** (per-widget adjustable). No since-game-start mode.
4. **Clock "Day: N":** confirmed as day-of-year (`Clock.ini` `Format=%j`) — intentional, kept.
5. **External IP:** web fetch (ipify or similar), 5 min + network-change refresh — approved.
6. **GPL reuse:** allowed (personal-use only) — see D5.
7. **Fans ground truth:** board SIO = **NCT6687D** (from FanControl `v3.json`: `/lpc/nct6687d/fan/0-7` — CPU Fan, Pump Fan, System Fan #1–6, running on LHM+PawnIO v240 today). Halo's collector coexists with FanControl via LHM's global ISA-bus mutex; Halo is monitor-only — FanControl keeps fan *control*. Screenshot's BACK/FRONT names = user relabels of System Fan channels; Halo settings will allow per-channel nicknames the same way.
