# Current Metrics Inventory (Rainformer setup)

**Date:** 2026-07-19 · Compiled from a live screenshot of the desktop. This was the checklist any
replacement stack had to cover before the old stack (HWiNFO + Afterburner + RTSS + NVIDIA App)
could be retired.

**As-built update (evening 2026-07-19):** Halo now covers this inventory. The
"Proposed replacement" column is the original plan, kept for the record; **"Halo source (as
built)"** is what actually ships, tagged with how the value is produced. **"Rate"** is the
**collector-side cadence** — how often the value in shared memory is refreshed — and whether it's
hard-coded or a `config\settings.json` setting. The display side is a separate, independent clock
(see the *Widget layer* note at the bottom): widgets read shared memory at their own tick
(`defaultRateHz` setting, per-widget `rateHz` override in `widgets.json`); what you see is the
last published value at the last tick, so worst-case staleness ≈ one publish interval + one tick
interval. Exception: the two fps panels are event-woken while a game runs (frame batches repaint
the whole panel, text included), so their 5 Hz tick is only the idle/fallback rate. Dashboard cadence was lowered 10 → **5 Hz** on user preference
(both the `defaultRateHz` setting and the previously-10 Hz hard-coded providers); the fps-counter
path keeps its own design rates (presented lane live; displayed lane drains + publishes at 40 Hz).

**Value-semantics tags used below:**
- **latest** — instantaneous sensor/OS read at poll time, no aggregation
- **interval avg** — mean over the gap between two polls (Δcounter ÷ Δt, e.g. CPU %, IO/net rates)
- **rolling Ns** — sliding time window over per-frame/per-sample data, recomputed continuously
- **decaying avg** — running sum/count halved periodically (~rolling over the last few hundred samples)
- **cumulative / running max** — since session start (or pipe reset: `reset-net`, `reset-max`)

Original sources per the [handoff](CONVERSATION-HANDOFF.md): **HWiNFO** (temps/fans/clocks/power),
**MSI Afterburner MAHM** (FPS panel), **UsageMonitor plugin / perf counters** (per-core %, top
processes), **Rainmeter built-ins** (time, disk space, network).

---

## 1. Clock / Uptime panel

| Metric | Example | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|
| Date (D/M/Y) | 19/7/2026 | Rainmeter `Time` | keep (built-in) | widget-side, system clock at render (no metric) · **latest** | widget tick 5 Hz (setting) |
| Day of year | Day: 200 | Rainmeter `Time` | keep | widget-side · **latest** | widget tick |
| Time (HH:MM:SS) | 00:20:39 | Rainmeter `Time` | keep | widget-side · **latest** | widget tick |
| Day of week | SUNDAY | Rainmeter `Time` | keep | widget-side · **latest** | widget tick |
| System uptime | 0d 3h 44m 20 | Rainmeter `Uptime` | keep | Builtin provider `sys.uptime.s` — poll · **latest** | 1 Hz (hard, cap 4) |

## 2. POWER panel

| Metric | Example | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| CPU Vcore (current) | 1.268 | V | HWiNFO | LHM (mobo SIO) | LHM SuperIO (NCT6687D) — poll · **latest** | 1 Hz (hard, cap 2) |
| CPU Vcore (session max) | 1.308 | V | HWiNFO | collector-side max | `.max` metric — **running max** (`reset-max` pipe) | follows base |
| GPU voltage (current) | 0.870 | V | HWiNFO | NVML/NVAPI | LHM GPU part (NVAPI) — poll · **latest** | 1 Hz (hard, cap 2) |
| GPU voltage (session max) | 0.985 | V | HWiNFO | collector-side max | `.max` — **running max** | follows base |
| CPU package power (current) | 62 | W | HWiNFO | LHM (MSR) | LHM CPU (MSR) — poll · **latest** | 5 Hz (setting `defaultRateHz`, cap 20) |
| CPU package power (max) | 125 | W | HWiNFO | collector-side max | `.max` — **running max** | follows base |
| GPU board power (current) | 47 | W | HWiNFO | NVML | NVML — poll · **latest** | 5 Hz (setting, cap 20) |
| GPU board power (max) | 80 | W | HWiNFO | collector-side max | `.max` — **running max** | follows base |

## 3. DRIVES panel (×7 volumes: C, D, E, F, G, H, I)

Per volume (letters from setting `driveLetters`):

| Metric | Example (C:) | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| Drive letter + label | (C:) Local SSD | — | Rainmeter `FreeDiskSpace` | keep / LHM storage | Builtin — poll (volume info) · **latest** | 1 Hz (hard) |
| Drive temperature | 52°C | °C | HWiNFO (NVMe/SMART) | LHM (SMART/NVMe) | LHM Storage — SMART poll · **latest** | 1/30 Hz (hard, cap 0.2) |
| Used space | 1.1 TB | B | Rainmeter `FreeDiskSpace` | keep | Builtin — poll · **latest** | 1 Hz (hard) |
| Total space | 1.9 TB | B | Rainmeter `FreeDiskSpace` | keep | Builtin — poll · **latest** | 1 Hz (hard) |
| Read activity/rate | 20.6 M | B/s | perf counters / HWiNFO | PDH / LHM | DiskIo — PDH LogicalDisk · **interval avg** (~200 ms window) | 5 Hz (hard, cap 64) |
| Write activity/rate | 1.4 M | B/s | perf counters / HWiNFO | PDH / LHM | DiskIo — PDH · **interval avg** | 5 Hz (hard) |
| Activity graph (per drive) | sparkline | — | same | keep | widget GraphEl — 1 Hz samples of the above | 1 Hz sample (hard per panel) |

Observed temps: C: 52°C · D: 60°C · E: 36°C · F: 39°C · G: 43°C · H: 43°C · I: 33°C

## 4. FPS COUNTER panels (now two panels: PRESENTED and DISPLAYED)

As built this became **two lanes** (commit 980b67a). **Presented panel** = door-1 tap: own ETW
session on DXGI/D3D9 Present-start events (`PresentTap`), push per present, no fate wait —
RTSS-class live. **Displayed panel** = resolved lane: bundled PresentMon 2 service + SDK
(`PresentMonAPI2.dll`), fate-aware (displayed vs dropped, flip times). Vulkan/OpenGL titles have
no runtime present event, so the presented panel falls back to the resolved lane
(`fps.tap.active`, setting `presentedTap`). Frame graphs on both panels repaint on the
`FramesReady` event (16 ms coalesce = 60 Hz widget monitor, hard) with the widget tick as fallback — not on a poll.

| Metric | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|
| Framerate (presented) | fps | Afterburner MAHM `Framerate` | PresentMon | Tap — ETW push · **rolling 1 s** (count ÷ actual span) | live, per present (flush = setting `presentMonEtwFlushMs`, 5 ms) |
| Framerate (displayed) | fps | — (didn't exist) | PresentMon Displayed | Resolved lane · **rolling 1 s** (displayed frames only) | published per drain — 40 Hz (hard, cap 120) |
| Framerate % of refresh | % | skin math /144 | read actual refresh | widget-side derived: rolling FPS ÷ `fps.refresh.hz` (poll · **latest**) | refresh poll 1 Hz (hard) |
| 1% / 0.1% low (per stream) | fps | Afterburner (since-reset window) | true windowed lows | 1000 ÷ mean of worst 1%/0.1% frametimes · **rolling 60 s** (setting `frameLowsWindowS`) | recomputed 2 Hz (hard) |
| Frametime (presented) | ms | Afterburner `Frametime` | PresentMon per-frame | Tap · **rolling 100 ms mean** | live, per present |
| Frametime (displayed) | ms | — | — | Resolved lane, flip-to-flip · **rolling 100 ms mean** | 40 Hz (per drain) |
| WORST (per stream) | ms | — | worst-per-window | **rolling 1 s max** | presented live / displayed 40 Hz |
| Frametime graph (per stream) | — | MAHM sampled 1/s | PresentMon stream | shared frame ring, one bar per actual frame, lane-filtered (`Provisional` flag) · **raw per-frame, no aggregation** | event-driven (`FramesReady`, 16 ms coalesce hard) |
| DLSS badge (version · SR/FG/RR · FG ×) | — | NVIDIA App | module scan | NGX module scan — poll · **latest**; FG × from `fps.fgratio` (displayed ÷ sim-pacing **calc**) | scan every 10 s; ratio 40 Hz (per drain) |

> Latency components (P2D / click / input) and the detailed DLSS / MODEL / FRAME GEN rows were
> moved to the dedicated LATENCY / DLSS panel (§4b) on 2026-07-19 — the fps panels keep only the
> compact badge above. The collector publishes those metrics either way; §4b documents them.

> The original screenshot's 2 FPS 1% low / 618 ms frametime desktop artifacts are structurally
> impossible now: lows are true rolling-window, and the panel goes to a dimmed "NO 3D APP" idle
> state when nothing presents.

## 4b. LATENCY / DLSS panel (new — not in the Rainformer setup)

Added 2026-07-19 (commit 71b19a9). No old-stack equivalent existed short of the NVIDIA App
overlay; the point of this panel is replacing that overlay's latency/DLSS readouts without the
App. Idle-dims like the FPS panels.

| Metric | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|
| PC LAT (headline, warn-colored) | ms | NVIDIA App overlay only | plan §7: marker-based PCL | widget-side sum **calc** = QUEUE + REND + DISP (overlay-equivalent, starts at input-enters-game) | components below; repaint at widget tick |
| QUEUE (input post → consume) | ms | — | — | PclStats — NVIDIA Reflex ETW markers (self-ping) · **rolling ~1.5 s avg** | 5 Hz publish (hard, cap 20) |
| REND (consume → present) | ms | — | — | PclStats markers · **rolling ~1.5 s avg** | 5 Hz (hard) |
| DISP (present → photon, P2D) | ms | — | — | resolved lane `MsUntilDisplayed` · **decaying avg** | 40 Hz (rides the drain publish) |
| CLICK (click-to-photon) | ms | — | PresentMon latency | resolved lane `ClickToPhoton` · **decaying avg** | 40 Hz (rides the drain publish) |
| INPUT (all-input-to-photon) | ms | — | PresentMon latency | resolved lane · **decaying avg** | 40 Hz (rides the drain publish) |
| DLSS version + features (SR/FG/RR) | — | NVIDIA App | NGX module scan | NGX DLL scan of target process — poll · **latest** | every 10 s (hard) |
| MODEL (Transformer/CNN · override vs game DLL) | — | NVIDIA App | DLL version/path heuristic | module scan (DLL ≥310 = Transformer; DriverStore path = override) · **latest** | every 10 s (hard) |
| FRAME GEN multiplier | × | — | — | widget-side **calc**: `fps.displayed` ÷ `render.rate.hz` (PCL sim-marker cadence · rolling ≥0.5 s); falls back to PresentMon sim-pacing ratio | components 5–10 Hz |
| PCL sparkline | — | — | — | widget GraphEl samples the PC LAT sum (autoscaled) | 2 Hz sample (hard) |

## 5. GPU panel (RTX 5070 Ti)

| Metric | Example | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| GPU temperature | 40°C | °C | HWiNFO | NVML | NVML — poll · **latest** | 5 Hz (setting, cap 20) |
| GPU core usage | 18% | % | HWiNFO | NVML | NVML — poll · **latest** (NVML's own sampling window) | 10 Hz (setting) |
| VRAM used / total | 3136 / 16303 MB | MB | HWiNFO | NVML | NVML — poll · **latest** | 5 Hz (setting) |
| VRAM usage % | 19% | % | derived | derived | derived (used/total) · **latest** | 5 Hz |
| GPU fan speed | 1103 rpm | rpm | HWiNFO | NVML | LHM GPU part (NVAPI) — poll · **latest** | 1 Hz (hard, cap 2) |
| GPU fan % | 30% | % | HWiNFO | NVML | NVML/NVAPI — poll · **latest** | 5 Hz / 1 Hz |
| GPU core clock | 1920 MHz | MHz | HWiNFO | NVML | NVML — poll · **latest** | 5 Hz (setting) |
| GPU memory clock | 875 MHz | MHz | HWiNFO | NVML | NVML — poll · **latest** | 5 Hz (setting) |
| Usage/temp graphs | sparklines | — | same | keep | widget GraphEl — 1 Hz samples of the above | 1 Hz sample (hard) |

> Dual-GPU system — inventory shows only the 5070 Ti panel; second GPU still has no panel.

## 6. FANS panel (case)

| Metric | Example | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| Back fan RPM | 1200 rpm | rpm | HWiNFO (mobo SIO/EC) | LHM ⚠️ verify board | LHM SuperIO NCT6687D — poll · **latest** (✅ board supported) | 1 Hz (hard, cap 2) |
| Back fan % | 60% | % | HWiNFO | LHM | derived: RPM ÷ setting `fanMaxRpm` (names via `fanNames`) · **latest** | 1 Hz |
| Front fan RPM | 1109 rpm | rpm | HWiNFO | LHM ⚠️ | LHM SuperIO — poll · **latest** | 1 Hz (hard) |
| Front fan % | 55% | % | HWiNFO | LHM | derived vs `fanMaxRpm` · **latest** | 1 Hz |

## 7. NETWORK panel

| Metric | Example | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| External IP | N/A | — | WebParser | keep (HTTP fetch) | Builtin — HTTP fetch (`externalIpUrl`) · **latest** | every 5 min (setting `externalIpRefreshMinutes`) + on network change |
| Internal IP | 192.168.1.45 | — | Rainmeter `SysInfo` | keep | Builtin — poll · **latest** | 1 Hz (hard) |
| Download rate (current) | 439.0 B/s | B/s | Rainmeter `NetIn` | keep / PDH | Network — octet counters Δ/dt · **interval avg** (interface pick: setting `networkInterface`) | 5 Hz (hard, cap 64) |
| Upload rate (current) | 192.0 B/s | B/s | Rainmeter `NetOut` | keep / PDH | Network — Δ/dt · **interval avg** | 5 Hz (hard) |
| Download peak | 56.8 MB/s | B/s | skin-side max | collector-side max | `.max` — **running max** (`reset-max` pipe) | follows base |
| Upload peak | 60.3 MB/s | B/s | skin-side max | collector-side max | `.max` — **running max** | follows base |
| Download total (session) | 158.1 GB | B | Rainmeter cumulative | keep | Network — **cumulative** (`reset-net` pipe) | 5 Hz |
| Upload total (session) | 56.5 GB | B | Rainmeter cumulative | keep | Network — **cumulative** | 5 Hz |
| Traffic graph | sparkline | — | same | keep | widget GraphEl — 1 Hz samples of the rates | 1 Hz sample (hard) |

## 8. CPU / RAM panel (i5-14600K, DDR5 6400)

| Metric | Example | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| CPU package temp | 56°C | °C | HWiNFO | LHM (MSR) | LHM CPU (MSR) — poll · **latest** | 5 Hz (setting, cap 20) |
| CPU total usage | 14.8% | % | UsageMonitor | PDH / LHM | CpuKernel — `NtQuerySystemInformation` Δ · **interval avg** (~200 ms) | 5 Hz (setting `defaultRateHz`, cap 64) |
| Per-core usage ×20 | Core 1: 22.5% … | % | UsageMonitor | PDH (keep P/E mapping) | CpuKernel — Δ · **interval avg** (P/E bar mapping kept: 1–12 P, 13–20 E) | 5 Hz (setting) |
| CPU clock | 5287 MHz | MHz | HWiNFO | LHM | LHM CPU — poll · **latest** | 5 Hz (setting) |
| CPU fan RPM | 1109 rpm | rpm | HWiNFO | LHM ⚠️ | LHM SuperIO — poll · **latest** | 1 Hz (hard) |
| RAM used / total | 19.1 / 31.7 GB | GB | Rainmeter `PhysicalMemory` | keep / PDH | Builtin — poll (`GlobalMemoryStatusEx`) · **latest** | 1 Hz (hard, cap 4) |
| RAM usage % | 60% | % | derived | derived | derived · **latest** | 1 Hz |
| RAM usage graph | sparkline | — | same | keep | widget GraphEl — 1 Hz samples | 1 Hz sample (hard) |

## 9. TOP CPU panel

| Metric | Example | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|
| Process count | 371 | UsageMonitor | PDH | Process provider — snapshot poll · **latest** | 1 Hz (hard, cap 2) |
| Top-5 by CPU % (name, RAM, CPU%) | OfficeClickT… 4.8%; dwm 1.9%; … | UsageMonitor | PDH / ToolHelp32 | Process — snapshot + per-process CPU Δ · **interval avg** (~1 s); RAM · **latest**; two rankings (per-instance + name-aggregated); count = setting `topProcessCount` | 1 Hz (hard) |

## 10. TOP RAM panel

| Metric | Example | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|
| Top-5 by RAM (name, CPU%, working set) | Memory Co… 2.2 GB; msedge 1.4 GB; … | UsageMonitor | PDH / ToolHelp32 | Process — same snapshot; RAM ranking · working set **latest**, CPU% **interval avg** | 1 Hz (hard) |

---

## Rollup — as built (collector providers and their cadences)

| Provider | Mechanism | Poll / push | Value semantics | Rate | Configurable? |
|---|---|---|---|---|---|
| **PresentMon resolved lane** | PresentMon 2 service child + `PresentMonAPI2.dll` frame queries | pull (drain) | rolling 1 s (fps/worst), rolling 100 ms (ft mean), rolling 60 s (lows), decaying avg (latencies) | drain + publish 40 Hz (hard, cap 120); lows 2 Hz (hard); ETW flush 5 ms (**setting**) | transport/tap/flush/lows-window via settings |
| **PresentTap (door-1)** | own ETW session, DXGI/D3D9 Present_Start via TraceEvent | **push** (per present) | rolling 1 s (fps/lows/worst), rolling 100 ms (frametime), raw per-frame (graph) | live; flush 5 ms (**setting**) | `presentedTap` on/off |
| **PclStats** | NVIDIA Reflex marker ETW session | push (markers) → 10 Hz publish | rolling ~1.5 s avg | 5 Hz (hard, cap 20) | — |
| **NVML** | NVIDIA management lib | poll | latest | 5 Hz (**setting**, cap 20) | `defaultRateHz` |
| **LHM CPU** | MSR via PawnIO | poll | latest | 5 Hz (**setting**, cap 20) | `defaultRateHz` |
| **LHM SuperIO / GPU extras** | NCT6687D / NVAPI | poll | latest | 1 Hz (hard, cap 2) | — |
| **LHM Storage** | SMART/NVMe | poll | latest | 1/30 Hz (hard) | — |
| **CpuKernel** | `NtQuerySystemInformation` + Δ | poll | interval avg | 5 Hz (**setting**, cap 64) | `defaultRateHz` |
| **DiskIo** | PDH LogicalDisk rates | poll | interval avg | 5 Hz (hard, cap 64) | drive list via `driveLetters` |
| **Network** | interface octet counters | poll | interval avg (rates), cumulative (totals), running max (peaks) | 5 Hz (hard, cap 64) | `networkInterface` |
| **Process** | process snapshot + CPU Δ + rankings | poll | interval avg (CPU%), latest (RAM) | 1 Hz (hard, cap 2) | `topProcessCount` |
| **Builtin** | uptime/RAM/IPs/disk space; external IP HTTP | poll | latest | 1 Hz (hard, cap 4); ext-IP 5 min (**setting**) | `externalIpUrl`, `externalIpRefreshMinutes` |
| **Session maxima** | `.max` running-max in MetricSink, `reset-max` pipe | calc | running max | follows base metric | — |

> ⚠️ Known cost hotspot: one LHM CPU (MSR) poll currently measures ~240 ms — longer than even the
> 5 Hz period (200 ms) — so that provider thread runs polls back-to-back (≈1 core busy, ~5% of the
> 20-thread CPU). Rate settings can't relieve it below its ~4 Hz effective floor; the fix is
> LHM-side (why does one update sweep take 240 ms?) — open optimization item.

**Widget layer:** text panels tick at 5 Hz (**setting** `defaultRateHz`; per-widget `rateHz` in
`widgets.json`, cap 100); sparklines sample their metric at 1–2 Hz (hard, per panel) — each point
carries the semantics of the metric it samples; frame graphs repaint on the
`Local\Halo.FramesReady.v1` event, coalesced to 16 ms (60 Hz widget monitor, hard), widget tick as fallback, and draw
**raw per-frame values** with no sampling.

**Grand total: ~85 individual metric values on screen** (drives ×7 and cores ×20 dominate) — all
covered; HWiNFO / Afterburner / RTSS / NVIDIA App fully retired. The one pre-build unknown
(NCT6687D SuperIO fan coverage) verified working.
