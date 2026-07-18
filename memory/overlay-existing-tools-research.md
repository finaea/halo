---
name: overlay-existing-tools-research
description: Buy-vs-build research — existing AIO monitors that already do the Phase 1 collector, so most of it should not be built from scratch
metadata:
  type: project
---

Research (2026-07-18) on whether to build the [[overlay-app-project]] Phase 1 collector or reuse existing tools. Conclusion: the collector already exists in mature form; original Phase 1 was ~90% reinvention.

**Key findings:**
- **PawnIO dissolves the "kernel driver is the ONLY hard part" concern.** Signed driver running signed Pawn bytecode behind IOCTLs (secure WinRing0 replacement, NOT on vulnerable-driver blocklist). LibreHardwareMonitor itself swapped WinRing0→PawnIO (PR #1857); FanControl v238, OpenRGB, CapFrameX 1.7.8 all migrated. So: consume a PawnIO-based LHM build; no re-signing/embedding needed. This supersedes the handoff plan to embed LHM's re-signed driver.
- **epinter/LibreHardwareService + epinter/rainmeter-lhws = the Phase 1 collector, already built, MPL-2.0 (commercial-OK).** LocalSystem service runs LHM, writes ALL sensors to shared memory (JSON/MessagePack index). rainmeter-lhws is a ready Rainmeter plugin reading it (`SensorType=Temperature/Load/Framerate/frametime`). Gaps: small project (~6 stars, last release Apr 2024); frame data via RTSS, NOT PresentMon-native.
- **HWiNFO 7.63+ (2024) integrates PresentMon natively** — Framerate (Presented AND Displayed), Frametime, GPU Busy — exposed via shared memory to Rainmeter. Personal-use path officially supported (docs.rainmeter.net/tips/hwinfo). Collapses the whole HWiNFO+Afterburner+RTSS+NVIDIA-App stack to just HWiNFO. Catch (already known): Pro license + 12h cap for commercial/always-on.
- **CapFrameX** = full reference implementation of the exact stack (PresentMon+LHM+PawnIO+RTSS overlay) but capture/analysis-oriented; no clean LIVE shared-memory feed for Rainmeter (its port-1337 server is an MCP AI-query endpoint, not a sensor feed). Use as blueprint, not data source.

**Decision guidance:**
- Personal, now → just run HWiNFO 7.63+, point skins at it. Zero code, fixes frametime ambiguity.
- Commercial-safe + own-data (stated long-term goal) → start from LibreHardwareService + rainmeter-lhws. The ONLY real gap vs original design is PresentMon-native frame data (it uses RTSS). So the sole thing worth building is a small **PresentMon → shared-memory shim** — a fraction of original Phase 1 scope.

**Phase-2-as-desktop-widget research (user pivoted from injected overlay to widget):** the category is "sensor panel" software (AIDA64 SensorPanel lineage).
- **InfoPanel (habibrehmansg/infopanel)** = near-exact match: GPL-3.0, C#/WPF, SkiaSharp + DirectX-accelerated widget rendering (high refresh — solves Rainmeter's per-tick re-parse cost), built-in LHM (no HWiNFO needed) or HWiNFO SHM, plugin architecture (plugins publish sensor data + visualizations), drives USB LCDs too. 228★, active (v1.3.1 Dec 2025).
- **InfoPanel.IPFPS plugin** (F3NN3X) proves PresentMon-in-InfoPanel works but is weak: fullscreen-only, 5-frame smoothing, NO displayed-vs-presented / 1% lows / max-frametime, admin required, 3★.
- Others: CLU VIEW (free but closed), AIDA64 SensorPanel (paid, RTSS-based), FPS Monitor on Steam (paid, stagnant), SensorPanelUtility (dead, 5 commits).
- **Plan shift:** instead of building a renderer, build a *better InfoPanel PresentMon plugin* (PresentMon SDK client → Displayed FPS, base FPS, true 1% lows, worst-frametime). Tiny vs whole renderer; collector core reusable later.
- **GPL-3.0 caveat:** InfoPanel fine for personal use + as architecture blueprint; a closed commercial product cannot build on it — own renderer remains the commercial endgame.

Other session learnings worth keeping: PresentMon vs RTSS/Afterburner FPS = ETW-pipeline-tracing vs Present()-hook counting — different metrics, not different accuracy (RTSS count-based FPS is the more stable headline number; PresentMon's value is decomposition: Presented vs Displayed vs frame-gen). Displayed FPS = frames that hit the panel (can exceed refresh with VSync off = tearing slices). HWiNFO resamples PresentMon onto its poll tick (≥1000ms, window stats only, no per-frame; <1000ms breaks RTSS-FPS reads). Own PresentMon consumers should attach as clients of the shared Intel PresentMon service, NOT own raw ETW session (session conflicts with HWiNFO/CapFrameX/FrameView). Rainmeter cost is fine at 1Hz (~1% CPU) but scales linearly with update rate — architecturally a widget engine, not a renderer.

Supersedes parts of [[overlay-app-project]] (the re-sign-your-own-driver plan). See [[overlay-frametime-findings]] for why PresentMon frame data matters.
