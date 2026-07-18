# Current Metrics Inventory (Rainformer setup)

**Date:** 2026-07-19 · Compiled from a live screenshot of the desktop. This is the checklist any replacement stack (InfoPanel + LHM + PresentMon plugin) must cover before the old stack (HWiNFO + Afterburner + RTSS + NVIDIA App) can be retired.

Current sources per the [handoff](CONVERSATION-HANDOFF.md): **HWiNFO** (temps/fans/clocks/power), **MSI Afterburner MAHM** (FPS panel), **UsageMonitor plugin / perf counters** (per-core %, top processes), **Rainmeter built-ins** (time, disk space, network).

---

## 1. Clock / Uptime panel

| Metric | Example | Current source | Replacement |
|---|---|---|---|
| Date (D/M/Y) | 19/7/2026 | Rainmeter `Time` | keep (Rainmeter/InfoPanel built-in) |
| Day of year | Day: 200 | Rainmeter `Time` | keep |
| Time (HH:MM:SS) | 00:20:39 | Rainmeter `Time` | keep |
| Day of week | SUNDAY | Rainmeter `Time` | keep |
| System uptime | 0d 3h 44m 20 | Rainmeter `Uptime` | keep |

## 2. POWER panel

| Metric | Example | Unit | Current source | Replacement |
|---|---|---|---|---|
| CPU Vcore (current) | 1.268 | V | HWiNFO | LHM (mobo SIO) |
| CPU Vcore (session max) | 1.308 | V | HWiNFO | collector-side max |
| GPU voltage (current) | 0.870 | V | HWiNFO | NVML/NVAPI |
| GPU voltage (session max) | 0.985 | V | HWiNFO | collector-side max |
| CPU package power (current) | 62 | W | HWiNFO | LHM (MSR) |
| CPU package power (max) | 125 | W | HWiNFO | collector-side max |
| GPU board power (current) | 47 | W | HWiNFO | NVML |
| GPU board power (max) | 80 | W | HWiNFO | collector-side max |

> Note: "Max" columns are session-window maxima — the replacement collector must track running max, not just instantaneous values.

## 3. DRIVES panel (×7 volumes: C, D, E, F, G, H, I)

Per volume:

| Metric | Example (C:) | Unit | Current source | Replacement |
|---|---|---|---|---|
| Drive letter + label | (C:) Local SSD | — | Rainmeter `FreeDiskSpace` | keep / LHM storage |
| Drive temperature | 52°C | °C | HWiNFO (NVMe/SMART) | LHM (SMART/NVMe) |
| Used space | 1.1 TB | B | Rainmeter `FreeDiskSpace` | keep |
| Total space | 1.9 TB | B | Rainmeter `FreeDiskSpace` | keep |
| Read activity/rate | 20.6 M | B/s | perf counters / HWiNFO | PDH / LHM |
| Write activity/rate | 1.4 M | B/s | perf counters / HWiNFO | PDH / LHM |
| Activity graph (per drive) | sparkline | — | same | keep |

Observed temps: C: 52°C · D: 60°C · E: 36°C · F: 39°C · G: 43°C · H: 43°C · I: 33°C

## 4. FPS COUNTER panel

| Metric | Example | Unit | Current source | Replacement |
|---|---|---|---|---|
| Framerate | 51 FPS | fps | Afterburner MAHM `Framerate` | **PresentMon (Displayed FPS)** |
| Framerate % of refresh | 35% | % (of 144) | skin math /144 | keep (or read actual refresh) |
| 1% low | 2 FPS | fps | Afterburner `Framerate 1% Low` (window = since AB reset) | **PresentMon, true windowed 1% low** |
| Frametime | 618.4 ms | ms | Afterburner `Frametime` | **PresentMon (per-frame; expose worst-per-window)** |
| Frametime graph | sparkline | — | MAHM sampled 1/s | PresentMon stream |

> ⚠️ Screenshot values (2 FPS 1% low, 618 ms frametime at desktop) illustrate the exact known problem: Afterburner stats windows/desktop-idle artifacts. This panel is the whole reason for the PresentMon migration — add **Displayed vs Presented/base FPS** and **0.1% low** when rebuilt.

## 5. GPU panel (RTX 5070 Ti)

| Metric | Example | Unit | Current source | Replacement |
|---|---|---|---|---|
| GPU temperature | 40°C | °C | HWiNFO | NVML |
| GPU core usage | 18% | % | HWiNFO | NVML |
| VRAM used / total | 3136 / 16303 MB | MB | HWiNFO | NVML |
| VRAM usage % | 19% | % | derived | derived |
| GPU fan speed | 1103 rpm | rpm | HWiNFO | NVML |
| GPU fan % | 30% | % | HWiNFO | NVML |
| GPU core clock | 1920 MHz | MHz | HWiNFO | NVML |
| GPU memory clock | 875 MHz | MHz | HWiNFO | NVML |
| Usage/temp graphs | sparklines | — | same | keep |

> Dual-GPU system — inventory shows only the 5070 Ti panel; confirm whether the second GPU needs a panel.

## 6. FANS panel (case)

| Metric | Example | Unit | Current source | Replacement |
|---|---|---|---|---|
| Back fan RPM | 1200 rpm | rpm | HWiNFO (mobo SIO/EC) | LHM ⚠️ *verify this board's SIO is supported* |
| Back fan % | 60% | % | HWiNFO | LHM |
| Front fan RPM | 1109 rpm | rpm | HWiNFO | LHM ⚠️ |
| Front fan % | 55% | % | HWiNFO | LHM |

## 7. NETWORK panel

| Metric | Example | Unit | Current source | Replacement |
|---|---|---|---|---|
| External IP | N/A | — | WebParser (web lookup) | keep (any HTTP fetch) |
| Internal IP | 192.168.1.45 | — | Rainmeter `SysInfo` | keep |
| Download rate (current) | 439.0 B/s | B/s | Rainmeter `NetIn` | keep / PDH |
| Upload rate (current) | 192.0 B/s | B/s | Rainmeter `NetOut` | keep / PDH |
| Download peak | 56.8 MB/s | B/s | skin-side max | collector-side max |
| Upload peak | 60.3 MB/s | B/s | skin-side max | collector-side max |
| Download total (session) | 158.1 GB | B | Rainmeter cumulative | keep |
| Upload total (session) | 56.5 GB | B | Rainmeter cumulative | keep |
| Traffic graph | sparkline | — | same | keep |

## 8. CPU / RAM panel (i5-14600K, DDR5 6400)

| Metric | Example | Unit | Current source | Replacement |
|---|---|---|---|---|
| CPU package temp | 56°C | °C | HWiNFO | LHM (MSR) |
| CPU total usage | 14.8% | % | UsageMonitor (perf counters) | PDH / LHM |
| Per-core usage ×20 | Core 1: 22.5% … Core 20: 8.6% | % | UsageMonitor `\Processor(0..19)` | PDH ⚠️ *keep P/E mapping: bars 1–12 = P-cores (logical 0–11), 13–20 = E-cores (logical 12–19)* |
| CPU clock | 5287 MHz | MHz | HWiNFO | LHM |
| CPU fan RPM | 1109 rpm | rpm | HWiNFO | LHM ⚠️ |
| RAM used / total | 19.1 / 31.7 GB | GB | Rainmeter `PhysicalMemory` | keep / PDH |
| RAM usage % | 60% | % | derived | derived |
| RAM usage graph | sparkline | — | same | keep |

## 9. TOP CPU panel

| Metric | Example | Current source | Replacement |
|---|---|---|---|
| Process count | 371 | UsageMonitor / perf counters | PDH |
| Top-5 processes by CPU % (name, RAM, CPU%) | OfficeClickT… 80.9 MB 4.8%; dwm 1.9%; svchost 1.1%; SignalRgb 0.9%; msedge 0.8% | UsageMonitor (`Process` category) | PDH / ToolHelp32 snapshot |

## 10. TOP RAM panel

| Metric | Example | Current source | Replacement |
|---|---|---|---|
| Top-5 processes by RAM (name, CPU%, working set) | Memory Co… 2.2 GB; msedge 1.4 GB; javaw 1.1 GB; Code 1.0 GB; Discord 573 MB | UsageMonitor | PDH / ToolHelp32 |

---

## Rollup — what the replacement collector must provide

| Provider | Metrics covered | Count (approx) |
|---|---|---|
| **PresentMon** (SDK client) | FPS, %refresh, 1% low, frametime (+ new: Displayed vs Presented, 0.1% low, worst-frametime) | 4 shown → 7 target |
| **NVML/NVAPI** | GPU temp, usage, VRAM, fans, clocks, voltage, power | ~10 |
| **LHM (PawnIO)** | CPU temp/clock/power/Vcore, mobo+CPU fans, drive temps | ~14 ⚠️ fan/SIO coverage must be verified on this board |
| **PDH / Win32** | total+per-core CPU ×20, RAM, disk I/O, process count, top-CPU/top-RAM lists, network rates | ~40 |
| **Built-ins (keep)** | time/date/uptime, disk space, IPs, session totals | ~15 |
| **Collector-side stats** | session max (volts/power/net peaks), windowed aggregates | cross-cutting |

**Grand total: ~85 individual metric values on screen** (drives ×7 and cores ×20 dominate). Nothing here requires HWiNFO *except* what LHM's board support may or may not cover (case/CPU fans, some drive temps) — that's the single compatibility check to run first in InfoPanel/LHM.
