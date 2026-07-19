# Current Metrics Inventory (Rainformer setup)

**Date:** 2026-07-19 · Compiled from a live screenshot of the desktop. This was the checklist any
replacement stack had to cover before the old stack (HWiNFO + Afterburner + RTSS + NVIDIA App)
could be retired.

**As-built update (evening 2026-07-19):** Halo now covers this inventory. The
"Proposed replacement" column is the original plan, kept for the record; **"Halo source (as
built)"** is what actually ships, tagged with how the value is produced. **"Rate"** is the actual
cadence and whether it's hard-coded or a `config\settings.json` setting. Widget text repaints at
the widget tick (`defaultRateHz` setting, per-widget `rateHz` override in `widgets.json`); frame
graphs are event-driven (see §4).

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
| Date (D/M/Y) | 19/7/2026 | Rainmeter `Time` | keep (built-in) | widget-side, system clock at render (no metric) · **latest** | widget tick 10 Hz (setting) |
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
| CPU package power (current) | 62 | W | HWiNFO | LHM (MSR) | LHM CPU (MSR) — poll · **latest** | 10 Hz (setting `defaultRateHz`, cap 20) |
| CPU package power (max) | 125 | W | HWiNFO | collector-side max | `.max` — **running max** | follows base |
| GPU board power (current) | 47 | W | HWiNFO | NVML | NVML — poll · **latest** | 10 Hz (setting, cap 20) |
| GPU board power (max) | 80 | W | HWiNFO | collector-side max | `.max` — **running max** | follows base |

## 3. DRIVES panel (×7 volumes: C, D, E, F, G, H, I)

Per volume (letters from setting `driveLetters`):

| Metric | Example (C:) | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| Drive letter + label | (C:) Local SSD | — | Rainmeter `FreeDiskSpace` | keep / LHM storage | Builtin — poll (volume info) · **latest** | 1 Hz (hard) |
| Drive temperature | 52°C | °C | HWiNFO (NVMe/SMART) | LHM (SMART/NVMe) | LHM Storage — SMART poll · **latest** | 1/30 Hz (hard, cap 0.2) |
| Used space | 1.1 TB | B | Rainmeter `FreeDiskSpace` | keep | Builtin — poll · **latest** | 1 Hz (hard) |
| Total space | 1.9 TB | B | Rainmeter `FreeDiskSpace` | keep | Builtin — poll · **latest** | 1 Hz (hard) |
| Read activity/rate | 20.6 M | B/s | perf counters / HWiNFO | PDH / LHM | DiskIo — PDH LogicalDisk · **interval avg** (between collects) | 10 Hz (hard, cap 64) |
| Write activity/rate | 1.4 M | B/s | perf counters / HWiNFO | PDH / LHM | DiskIo — PDH · **interval avg** | 10 Hz (hard) |
| Activity graph (per drive) | sparkline | — | same | keep | widget GraphEl — 1 Hz samples of the above | 1 Hz sample (hard per panel) |

Observed temps: C: 52°C · D: 60°C · E: 36°C · F: 39°C · G: 43°C · H: 43°C · I: 33°C

## 4. FPS COUNTER panels (now two panels: PRESENTED and DISPLAYED)

As built this became **two lanes** (commit 980b67a). **Presented panel** = door-1 tap: own ETW
session on DXGI/D3D9 Present-start events (`PresentTap`), push per present, no fate wait —
RTSS-class live. **Displayed panel** = resolved lane: bundled PresentMon 2 service + SDK
(`PresentMonAPI2.dll`), fate-aware (displayed vs dropped, flip times). Vulkan/OpenGL titles have
no runtime present event, so the presented panel falls back to the resolved lane
(`fps.tap.active`, setting `presentedTap`). Frame graphs on both panels repaint on the
`FramesReady` event (~7 ms coalesce, hard) with the widget tick as fallback — not on a poll.

| Metric | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|
| Framerate (presented) | fps | Afterburner MAHM `Framerate` | PresentMon | Tap — ETW push · **rolling 1 s** (count ÷ actual span) | live, per present (flush = setting `presentMonEtwFlushMs`, 5 ms) |
| Framerate (displayed) | fps | — (didn't exist) | PresentMon Displayed | Resolved lane · **rolling 1 s** (displayed frames only) | numbers 10 Hz (hard); drain 60 Hz (hard, cap 120) |
| Framerate % of refresh | % | skin math /144 | read actual refresh | widget-side derived: rolling FPS ÷ `fps.refresh.hz` (poll · **latest**) | refresh poll 1 Hz (hard) |
| 1% / 0.1% low (per stream) | fps | Afterburner (since-reset window) | true windowed lows | 1000 ÷ mean of worst 1%/0.1% frametimes · **rolling 60 s** (setting `frameLowsWindowS`) | recomputed 2 Hz (hard) |
| Frametime (presented) | ms | Afterburner `Frametime` | PresentMon per-frame | Tap · **rolling 100 ms mean** | live, per present |
| Frametime (displayed) | ms | — | — | Resolved lane, flip-to-flip · **rolling 1 s mean** | 10 Hz (hard) |
| WORST (per stream) | ms | — | worst-per-window | **rolling 1 s max** | presented live / displayed 10 Hz |
| Frametime graph (per stream) | — | MAHM sampled 1/s | PresentMon stream | shared frame ring, one bar per actual frame, lane-filtered (`Provisional` flag) · **raw per-frame, no aggregation** | event-driven (`FramesReady`, ~7 ms coalesce hard) |
| FG multiplier | × | — | — | displayed rate ÷ sim cadence (`BetweenSimulationStart`) · **rolling 1 s ÷ decaying avg** | 10 Hz (hard) |
| Display latency (P2D) | ms | — | — | resolved lane `MsUntilDisplayed` · **decaying avg** | 10 Hz (hard) |
| Click/AllInput-to-Photon | ms | — | PresentMon latency | resolved lane · **decaying avg** | 10 Hz (hard) |
| DLSS badge (SR/FG/RR, model) | — | NVIDIA App | module scan | NGX module scan of target process — poll · **latest** | every 10 s (hard) |

> The original screenshot's 2 FPS 1% low / 618 ms frametime desktop artifacts are structurally
> impossible now: lows are true rolling-window, and the panel goes to a dimmed "NO 3D APP" idle
> state when nothing presents.

## 5. GPU panel (RTX 5070 Ti)

| Metric | Example | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| GPU temperature | 40°C | °C | HWiNFO | NVML | NVML — poll · **latest** | 10 Hz (setting, cap 20) |
| GPU core usage | 18% | % | HWiNFO | NVML | NVML — poll · **latest** (NVML's own sampling window) | 10 Hz (setting) |
| VRAM used / total | 3136 / 16303 MB | MB | HWiNFO | NVML | NVML — poll · **latest** | 10 Hz (setting) |
| VRAM usage % | 19% | % | derived | derived | derived (used/total) · **latest** | 10 Hz |
| GPU fan speed | 1103 rpm | rpm | HWiNFO | NVML | LHM GPU part (NVAPI) — poll · **latest** | 1 Hz (hard, cap 2) |
| GPU fan % | 30% | % | HWiNFO | NVML | NVML/NVAPI — poll · **latest** | 10 Hz / 1 Hz |
| GPU core clock | 1920 MHz | MHz | HWiNFO | NVML | NVML — poll · **latest** | 10 Hz (setting) |
| GPU memory clock | 875 MHz | MHz | HWiNFO | NVML | NVML — poll · **latest** | 10 Hz (setting) |
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
| Download rate (current) | 439.0 B/s | B/s | Rainmeter `NetIn` | keep / PDH | Network — octet counters Δ/dt · **interval avg** (interface pick: setting `networkInterface`) | 10 Hz (hard, cap 64) |
| Upload rate (current) | 192.0 B/s | B/s | Rainmeter `NetOut` | keep / PDH | Network — Δ/dt · **interval avg** | 10 Hz (hard) |
| Download peak | 56.8 MB/s | B/s | skin-side max | collector-side max | `.max` — **running max** (`reset-max` pipe) | follows base |
| Upload peak | 60.3 MB/s | B/s | skin-side max | collector-side max | `.max` — **running max** | follows base |
| Download total (session) | 158.1 GB | B | Rainmeter cumulative | keep | Network — **cumulative** (`reset-net` pipe) | 10 Hz |
| Upload total (session) | 56.5 GB | B | Rainmeter cumulative | keep | Network — **cumulative** | 10 Hz |
| Traffic graph | sparkline | — | same | keep | widget GraphEl — 1 Hz samples of the rates | 1 Hz sample (hard) |

## 8. CPU / RAM panel (i5-14600K, DDR5 6400)

| Metric | Example | Unit | Old source | Proposed replacement | Halo source (as built) | Rate |
|---|---|---|---|---|---|---|
| CPU package temp | 56°C | °C | HWiNFO | LHM (MSR) | LHM CPU (MSR) — poll · **latest** | 10 Hz (setting, cap 20) |
| CPU total usage | 14.8% | % | UsageMonitor | PDH / LHM | CpuKernel — `NtQuerySystemInformation` Δ · **interval avg** (~100 ms) | 10 Hz (setting `defaultRateHz`, cap 64) |
| Per-core usage ×20 | Core 1: 22.5% … | % | UsageMonitor | PDH (keep P/E mapping) | CpuKernel — Δ · **interval avg** (P/E bar mapping kept: 1–12 P, 13–20 E) | 10 Hz (setting) |
| CPU clock | 5287 MHz | MHz | HWiNFO | LHM | LHM CPU — poll · **latest** | 10 Hz (setting) |
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
| **PresentMon resolved lane** | PresentMon 2 service child + `PresentMonAPI2.dll` frame queries | pull (drain) | rolling 1 s (fps/ft), rolling 60 s (lows), decaying avg (latencies) | drain 60 Hz (hard, cap 120); numbers 10 Hz (hard); lows 2 Hz (hard); ETW flush 5 ms (**setting**) | transport/tap/flush/lows-window via settings |
| **PresentTap (door-1)** | own ETW session, DXGI/D3D9 Present_Start via TraceEvent | **push** (per present) | rolling 1 s (fps/lows/worst), rolling 100 ms (frametime), raw per-frame (graph) | live; flush 5 ms (**setting**) | `presentedTap` on/off |
| **PclStats** | NVIDIA Reflex marker ETW session | push (markers) → 10 Hz publish | rolling ~1.5 s avg | 10 Hz (hard, cap 20) | — |
| **NVML** | NVIDIA management lib | poll | latest | 10 Hz (**setting**, cap 20) | `defaultRateHz` |
| **LHM CPU** | MSR via PawnIO | poll | latest | 10 Hz (**setting**, cap 20) | `defaultRateHz` |
| **LHM SuperIO / GPU extras** | NCT6687D / NVAPI | poll | latest | 1 Hz (hard, cap 2) | — |
| **LHM Storage** | SMART/NVMe | poll | latest | 1/30 Hz (hard) | — |
| **CpuKernel** | `NtQuerySystemInformation` + Δ | poll | interval avg | 10 Hz (**setting**, cap 64) | `defaultRateHz` |
| **DiskIo** | PDH LogicalDisk rates | poll | interval avg | 10 Hz (hard, cap 64) | drive list via `driveLetters` |
| **Network** | interface octet counters | poll | interval avg (rates), cumulative (totals), running max (peaks) | 10 Hz (hard, cap 64) | `networkInterface` |
| **Process** | process snapshot + CPU Δ + rankings | poll | interval avg (CPU%), latest (RAM) | 1 Hz (hard, cap 2) | `topProcessCount` |
| **Builtin** | uptime/RAM/IPs/disk space; external IP HTTP | poll | latest | 1 Hz (hard, cap 4); ext-IP 5 min (**setting**) | `externalIpUrl`, `externalIpRefreshMinutes` |
| **Session maxima** | `.max` running-max in MetricSink, `reset-max` pipe | calc | running max | follows base metric | — |

**Widget layer:** text panels tick at 10 Hz (**setting** `defaultRateHz`; per-widget `rateHz` in
`widgets.json`, cap 100); sparklines sample their metric at 1–2 Hz (hard, per panel) — each point
carries the semantics of the metric it samples; frame graphs repaint on the
`Local\Halo.FramesReady.v1` event, coalesced to ~7 ms (hard), widget tick as fallback, and draw
**raw per-frame values** with no sampling.

**Grand total: ~85 individual metric values on screen** (drives ×7 and cores ×20 dominate) — all
covered; HWiNFO / Afterburner / RTSS / NVIDIA App fully retired. The one pre-build unknown
(NCT6687D SuperIO fan coverage) verified working.
