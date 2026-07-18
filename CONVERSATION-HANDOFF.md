# Conversation Handoff — Rainformer skins → custom overlay app

**Date:** 2026-07-18 · **User:** jack.lim@revolab.ai
**Hardware:** Intel i5-14600K (6 P-cores / 8 E-cores, 20 threads), dual-GPU, 144 Hz display.
**Continue from here** — this captures a full session so a new chat started in `RainformerHWiv2` has the context. Companion facts live in `memory/` (`overlay-app-project.md`, `overlay-frametime-findings.md`).

---

## What we investigated (in order)

### 1. How `CPU-RAM.ini` gets per-core %
- Core usage comes from the **UsageMonitor** Rainmeter plugin reading **Windows perf counters** (`Category=Processor`, `Counter=% Processor Time`, `Name=0..N`), **not** HWiNFO. HWiNFO is only used for temp/fan/clock.
- Core count from `%NUMBER_OF_PROCESSORS%` (= 20 logical). `MCLOADCOREn` measures use `Name = n-1`.

### 2. P-core vs E-core mapping (verified live via `GetSystemCpuSetInformation`)
- **EfficiencyClass 1 = P-cores = logical CPUs 0–11** (6 physical, hyperthreaded in pairs: 0/1, 2/3, …, 10/11).
- **EfficiencyClass 0 = E-cores = logical CPUs 12–19** (8 physical, 1 thread each).
- In the skin: **bars 1–12 = P-cores, bars 13–20 = E-cores.** (User's earlier assumption "cores 0–6 = P-cores" was wrong.)

### 3. Hyperthreading & gaming (concept)
- HT doesn't halve a core; a 2nd thread fills idle execution units. Two threads ≈ **~130% total**, each ~65% when both busy, **full speed when only one is active** — never a clean 50/50 split.
- Games are usually **main-thread bound**: one thread pegged, others idle = CPU-bound on the game's serial loop. Windows migrates that thread across P-cores, so it visually smears.

### 4. `GPU2.ini` is repurposed as an FPS counter (pulls MSI Afterburner)
DataSource mapping (skin does **no math** — pure pass-through of Afterburner's MAHM shared memory):
- `Framerate` → big "X FPS" number
- `Framerate 1% Low` → "1p LOW"
- `Frametime` → "FRAMETIME: X ms"
- `Framerate` /144*100 → "Framerate: X%" (144 is hardcoded = monitor refresh)

### 5. Validated the values live (read MAHM + RTSS shared memory directly)
- **Names match exactly** — setup is correctly wired.
- **"1p LOW"** = Afterburner's 1% low, but the window is **Afterburner's** (≈ since game launch / its stat reset), **NOT** "since the skin timer started."
- **"FRAMETIME"** is **total present-to-present frametime, not CPU frametime** (the label is misleading; user chose not to fix it).

### 6. The frametime mismatch (the big finding)
- On-screen overlay showed **<1 ms**; Rainmeter showed **15–20 ms**. Live reads:
  - RTSS raw `dwFrameTime` (from `RTSSSharedMemoryV2`, µs) = **0.353 ms** in Zenless Zone Zero (D3D12).
  - Afterburner `Framerate` = **137 FPS**, Afterburner `Frametime` = **15–20 ms** (≠ 1000/137).
- **Cause = frame generation** decoupling raw `Present()` rate from displayed frames. NOT a skin bug, NOT a timing/poll issue.
  - RTSS raw = instantaneous present interval → **fooled by frame-gen** (<1 ms nonsense).
  - Afterburner `Framerate` = **count-based average → reliable**.
  - Afterburner `Frametime` = a differently-computed value tracking real-render cadence.
- **Verdict:** with frame-gen ON, trust **FPS + 1%/0.1% lows**; ignore both frametimes. With frame-gen OFF, raw frametime becomes truthful (~1000/FPS) and both agree.
- This is **exactly the CPU/GPU/display-frametime ambiguity PresentMon resolves** — the concrete justification for the whole overlay project.

### 7. Poll timing
- Skins use `Update=1000`. Afterburner default polling ~1000 ms; HWiNFO default ~2000 ms.
- Rainmeter can only read **Afterburner/HWiNFO shared memory**, not RTSS's raw per-frame stream. So the skin is always a ~1/sec sampler.

### 8. "Can we read RTSS raw ourselves?" — YES
- Proven: read `RTSSSharedMemoryV2`, field `dwFrameTime` (µs) per app entry (offset 280 in the app entry; entry size from header). Keyed by PID/process name — must pick the foreground 3D app.
- Getting it into Rainmeter needs a bridge (available plugin: `RunCommand.dll`; or a background helper writing a file the skin reads). Ceiling: Rainmeter redraws at its `Update` rate, so it's still a sampler, not a per-frame graph — but bypasses Afterburner's confusing frametime.

---

## The strategic decision (aligned with `Desktop/WIP/Playground/overlay-app-research.md`)

**Goal:** stop needing HWiNFO + Afterburner + RTSS + NVIDIA App running just to feed Rainmeter; long-term build a **native in-game overlay** (Rainmeter is a desktop widget and structurally cannot draw over fullscreen games).

**Agreed architecture — "own the data, borrow the display":**
- **Data layer (own it, low-maintenance):**
  - FPS / frametime / 1%low / frame-gen inference → **PresentMon + ETW** (no injection, no anti-cheat risk).
  - GPU temp/clock/usage/VRAM/power/fan → **NVAPI/NVML** (driver-only, no app).
  - CPU/GPU usage + RAM → **PDH / Win32**.
- **Kernel-driver layer (the ONLY hard/high-maintenance part):** CPU MSR temp/power + mobo fans/volts → embed **LibreHardwareMonitorLib** (ships hardened/re-signed driver). **Avoid raw WinRing0/RTCore64** (vulnerable-driver blocklist, Defender flags).
- **Maintainability tactic:** provider interface (`ISensorProvider`) so vendor swaps (NVAPI↔ADLX↔IGCL) or LHM version bumps are **one-module changes, not rewrites**. Windows-update risk is near-zero except the kernel-driver blocklist (which LHM absorbs for you).
- **HWiNFO shared-memory caveat:** non-commercial/closed-source license + free HWiNFO caps shared memory at **12 h/session**. Fine as a personal prototype shortcut; **LHM is the only path that survives always-on + commercial.**

**Accepted gaps (no clean external API):** true Reflex PC latency (needs game-integrated Reflex markers), exact DLSS-SR mode/preset/version (driver-internal).

**Anti-cheat:** only the *renderer* (injection/hooking) carries AC risk. The data layer is safe. Safe render routes: Vulkan/OpenXR layers, borderless transparent window, or allowlist-partnering.

---

## Recommended next step (where we left off)

**Phase 1 — the collector (sensor service).** Feed the *existing* Rainmeter skins from it via shared memory. Immediate win: closes RTSS + Afterburner + NVIDIA App, fixes frametime via PresentMon, zero AC exposure, low maintenance.

**Phase 2 — the renderer.** Injected/layer in-game overlay (references: goverlay, ReShade's Vulkan layer) on top of the *same* collector. AC is the gating constraint here, cleanly separated from Phase 1.

**Immediate open task I offered to do next:**
> Draft the **Phase 1 collector's module/interface layout** — the `ISensorProvider` shape with PresentMon / NVML / PDH / LHM behind it, plus the **shared-memory block format** the Rainmeter skins would read — so it drops onto the existing GPU2/CPU-RAM skins.

Pick up there.

---

## Reference: shared-memory reading recipes proven this session
- **Afterburner MAHM** (`MAHMSharedMemory`): header at 0 — sig `0x4D41484D`, headerSize@8, numEntries@12, entrySize@16. Each entry: `szSrcName[260]` at entry start, `data` float at entry+1300.
- **RTSS** (`RTSSSharedMemoryV2`): header — appEntrySize@8, appArrOffset@12, appArrSize@16. Each app entry: PID@0, name[260]@4, flags@264 (low word = render API: 8=D3D12; 0x10000=x64), `dwFrameTime` µs @280.
- **CPU P/E map:** `GetSystemCpuSetInformation` (kernel32) → per-logical-CPU `EfficiencyClass` (byte @ record+18), `LogicalProcessorIndex` @14, `CoreIndex` @15.
