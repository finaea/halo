# Desktop Widget / Sensor Panel Comparison

**Date:** 2026-07-18 · Research for the [overlay project](CONVERSATION-HANDOFF.md) — evaluating existing "sensor panel" desktop widgets before building our own.

## What we're grading against

The project's requirements (from the handoff + this session's research):

| # | Requirement | Why |
|---|---|---|
| R1 | **PresentMon-grade frame data** — Displayed vs Presented FPS, true 1%/0.1% lows, per-frame frametime (spike visibility) | The frame-gen ambiguity that started this whole project; averages hide stutter |
| R2 | **Full HW sensors without the app stack** — CPU MSR temps, mobo fans/volts, GPU, RAM | Goal is killing HWiNFO + Afterburner + RTSS + NVIDIA App |
| R3 | **Safe kernel driver** — PawnIO-based LHM or equivalent (no WinRing0/RTCore64) | Vulnerable-driver blocklist / Defender |
| R4 | **Efficient rendering** — GPU-accelerated widget surface, not per-tick re-parse | Rainmeter's cost scales linearly with update rate |
| R5 | **Extensible** — plugin/provider model so data sources are swappable | The `ISensorProvider` maintainability tactic |
| R6 | **License viable for an eventual commercial product** | HWiNFO SHM & GPL bases break this |

---

## Main contenders

| | Rainmeter *(current)* | **InfoPanel** ⭐ | CLU VIEW | AIDA64 SensorPanel | FPS Monitor | MoBro | SensorPanelUtility |
|---|---|---|---|---|---|---|---|
| **License** | GPL-2.0, free | GPL-3.0, free | Free, **closed** | **Paid**, closed | **Paid** (Steam), closed | Free, closed (plugins MIT) | GPL-2.0 |
| **Maturity** | Very mature | Active — 228★, v1.3.1 Dec 2025 | Active | Very mature | Stagnant | Active | **Dead** (5 commits, 0 releases) |
| **R1 frame data** | ✗ needs external feed (Afterburner/HWiNFO SHM) | ⚠️ community PresentMon plugin exists ([IPFPS](https://github.com/F3NN3X/InfoPanel.IPFPS)) but weak: fullscreen-only, 5-frame smoothing, **no** displayed/presented split, **no** 1% lows | ⚠️ claims FPS/frametime "where supported" — unverified black box | ⚠️ FPS via **RTSS** only (presented-count, needs RTSS running) | ⚠️ own hook (injection), no PresentMon decomposition | ✗ none found | ✗ (RTSS planned, never shipped) |
| **R2 sensors, no stack** | ✗ needs HWiNFO/AB running | ✓ **built-in LHM** (or HWiNFO SHM optional) | ✓ built-in | ✓ own engine | ✓ own engine | ✓ LHM/HWiNFO via plugins | ✗ |
| **R3 driver** | n/a (inherits from feeder) | LHM → PawnIO path ✓ | unknown (closed) | own driver | own driver (flag risk) | LHM plugin → PawnIO path ✓ | n/a |
| **R4 rendering** | ✗ per-tick re-eval; D2D w/ optional HW accel; fine @1 Hz, poor @10+ Hz | ✓ **SkiaSharp + DirectX-accelerated**, built for high refresh | ✓ native, purpose-built (14 widgets/186 styles) | ✓ mature panel renderer | ✓ | ⚠️ web-tech themes (browser rendering) | ? |
| **R5 extensible** | ✓ plugins (C++/C#) | ✓ **plugin SDK** (data + visualizations; Spotify/FPS/YT exist) | ✗ closed | ✗ closed | ✗ closed | ✓ plugin SDK (MIT examples) | ✗ |
| **R6 commercial base** | ✗ GPL | ✗ GPL-3.0 (fine personal; can't ship closed product on it) | ✗ closed source | ✗ | ✗ | ✗ closed core | ✗ GPL + dead |
| **Bonus** | huge skin ecosystem (Rainformer already built) | drives USB LCDs (BeadaPanel/Turing); Linux port exists | Panel Mode for 2nd screens | remote LCD/phone panels | desktop **and** in-game overlay | Raspberry Pi / phone / network displays | — |

⭐ **InfoPanel is the standout** — the only open, extensible, actively-maintained option with built-in LHM *and* a proven (if weak) PresentMon path.

---

## Dismissed / honorable mentions

| Tool | Why dismissed |
|---|---|
| **Sidebar Diagnostics** | Sidebar-only layout, no frame data, low activity |
| **Witals** (MS Store) | Win11 Widget Board only — CPU/GPU/RAM/net, no temps depth, no FPS |
| **System Monitor II** | Legacy sidebar-gadget style, CPU/RAM only |
| **NZXT CAM** | Heavyweight suite, account-pushy, closed, telemetry concerns; overlay is in-game oriented |
| **Wallpaper Engine** | Exposes CPU/GPU/RAM stats to wallpapers, GPU-rendered — fun, but no FPS/frametime, no plugin data model, paid, wallpaper ≠ widget layer |
| **Aquasuite** | Excellent panels but tied to Aqua Computer hardware/licensing |
| **Grafana-based** (e.g. MarkhamLee/HardwareMonitoring) | Dashboard-in-browser, ~seconds latency, wrong form factor for an always-on desktop widget |
| **RTSS Desktop Overlay Host** | Renders the RTSS OSD on the desktop — but that keeps the exact Afterburner/RTSS stack we're trying to retire |
| **CapFrameX** | Best-in-class frame *analysis*, but capture-session oriented; no persistent desktop widget; no live SHM feed for others |

## Feeder-layer tools (not widgets, but relevant)

| Tool | Role | Notes |
|---|---|---|
| **HWiNFO 7.63+** | AIO sensor+PresentMon feed via SHM | Zero-code personal path; ✗ Pro license + 12 h SHM cap for commercial/always-on; poll-window stats only (≥1000 ms, no per-frame) |
| **LibreHardwareService** (epinter) | LHM → shared memory service, MPL-2.0 | Commercial-friendly collector that already exists; + `rainmeter-lhws` plugin; frame data via RTSS not PresentMon |
| **Intel PresentMon service + SDK** | The frame-data source of truth (MIT) | Attach as a *client* — never spawn a second ETW session (conflicts with HWiNFO/CapFrameX/FrameView) |

---

## Verdict

**Personal use, this week (zero code):**
> **InfoPanel + built-in LHM + IPFPS plugin.** Kills the entire HWiNFO/Afterburner/RTSS/NVIDIA-App stack today. Accept IPFPS's weak metrics for now.

**The one thing worth building (small):**
> A **better InfoPanel PresentMon plugin** — attach to the Intel PresentMon service as an SDK client; expose **Displayed FPS, presented/base FPS, true 1% / 0.1% lows, worst-frametime-per-window**. Replaces IPFPS; the collector core is reusable later. This is now the whole of "Phase 1."

**Commercial endgame (only if it materializes):**
> Own renderer, using InfoPanel as the architecture blueprint (SkiaSharp surface + LHM/PawnIO + plugin data model). Nothing on this list is a legally usable base: GPL (InfoPanel, Rainmeter), closed (CLU VIEW, AIDA64, FPS Monitor, MoBro core).

**Keep Rainmeter?** Only if attached to the Rainformer skins' look — feed them via LibreHardwareService + `rainmeter-lhws` (MPL-2.0) or HWiNFO SHM (personal only). Otherwise InfoPanel supersedes it for this use case.

---

## Sources

- [InfoPanel](https://github.com/habibrehmansg/infopanel) · [InfoPanel.IPFPS plugin](https://github.com/F3NN3X/InfoPanel.IPFPS) · [infopanel.net](https://infopanel.net/)
- [CLU VIEW](https://mr-clu.com/cluview) · [sensor-panel roundup](https://mr-clu.com/articles/best-free-sensor-panel-software)
- [AIDA64 SensorPanel](https://www.aida64.com/aida64-sensorpanel) · [AIDA64 FPS via RTSS](https://forums.aida64.com/topic/9089-show-fps-rtss-aida/)
- [FPS Monitor (Steam)](https://store.steampowered.com/app/966610/FPS_Monitor__hardware_ingame__desktop_overlays/)
- [MoBro](https://www.mod-bros.com/en/faq/mobro/desktop-app/hardware-monitor) · [MoBro LHM plugin](https://github.com/ModBros/mobro-plugin-librehardwaremonitor)
- [SensorPanelUtility](https://github.com/danarrib/SensorPanelUtility)
- [LibreHardwareService](https://github.com/epinter/LibreHardwareService) · [rainmeter-lhws](https://github.com/epinter/rainmeter-lhws)
- [PresentMon](https://github.com/GameTechDev/PresentMon) · [PresentMon Service](https://github.com/GameTechDev/PresentMon/blob/main/README-Service.md)
- [HWiNFO PresentMon integration](https://videocardz.com/newz/hwinfo-7-63-integrates-presentmon-adds-framerate-frametime-and-gpu-busy-metrics)
- [Rainmeter 4.3 D2D](https://www.rainmeter.net/release-4-3/) · [Rainmeter perf thread](https://forum.rainmeter.net/viewtopic.php?t=43185)
- [Witals](https://apps.microsoft.com/detail/9nlvhrg92j4l) · [System Monitor II](https://www.techspot.com/downloads/5721-system-monitor.html)
