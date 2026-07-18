---
name: overlay-app-project
description: User's project to build a self-sufficient hardware/game overlay, replacing HWiNFO/Afterburner/RTSS/NVIDIA-app
metadata:
  type: project
---

User (jack.lim@revolab.ai) is building their own hardware/game metrics overlay to stop depending on HWiNFO + MSI Afterburner + RTSS + NVIDIA App just to feed their Rainmeter skins (Rainformer HWiNFO Edition, at the repo root). Long-term goal per their research is a **native in-game overlay** (which Rainmeter structurally cannot do — it's a desktop widget).

Research doc: `C:\Users\final\Desktop\WIP\Playground\overlay-app-research.md` (thorough, web-level).

**Chosen architecture (agreed):**
- Two-process split: sensor/UI service + thin injected renderer (copies Afterburner+RTSS).
- Data layer (low-maintenance, own it): PresentMon/ETW for FPS/frametime/1%low/frame-gen; NVAPI/NVML for GPU sensors; PDH/Win32 for CPU/GPU usage + RAM.
- Kernel-driver layer (high-maintenance, the ONLY hard part): CPU MSR temp/power + mobo fans/volts. Prefer embedding **LibreHardwareMonitorLib** (ships hardened/re-signed driver) over rolling own EV/WHQL driver. AVOID raw WinRing0/RTCore64 (vulnerable-driver blocklist, Defender flags).
- Maintainability tactic: provider interface so vendor swaps (NVAPI↔ADLX↔IGCL) or LHM version bumps are one-module changes, not rewrites.
- Recommended near-term: hybrid — self-source GPU (NVML) + FPS (PresentMon) + usage (PDH) to drop RTSS/Afterburner/NVIDIA-app now; keep HWiNFO (or LHM) for the driver-dependent CPU/mobo sensors. NOTE: consuming HWiNFO shared memory is non-commercial/closed-source licensed and free HWiNFO caps shared memory at 12h/session — fine for personal use, breaks a commercial product.

**Accepted gaps — narrowed 2026-07-19:** PresentMon 2.x now provides **Click-to-Photon + All-Input-to-Photon** (full PC latency, ETW, no game integration — in Halo plan) and DLSS presence/version is readable via NGX module enumeration of the game PID (nvngx_dlss/dlssg/dlssd.dll, read-only, no injection — in plan). Still out of reach: exact DLSS preset/mode (needs NGX hooking) and marker-based Reflex PCL (optional FrameView SDK slot, unbuilt).

**2026-07-19 pivot — building "Halo" (Hardware Analytics & Live Overlay), a native widget app, skipping interim phases.** User chose: C# (.NET 8) for BOTH processes (Halo.Collector elevated + Halo.Widgets NativeAOT + on-demand WPF Halo.Settings); scope = the 10 panels from their live screenshot, 1:1 Rainformer visuals, single GPU (5070 Ti); 10Hz default / 100Hz cap with per-provider auto-limiting; two FPS widget instances (Presented vs Displayed) with rolling-60s 1%/0.1% lows; no HWiNFO/RTSS/Afterburner. GPL code reuse (InfoPanel etc.) explicitly ALLOWED — user confirmed personal-use-only, commercial ambition dropped. Full plan: `native-widget-implementation-plan.md`; metric checklist: `current-metrics-inventory.md`.

**Portable-first policy (user requirement):** all Halo deps/binaries/config/NuGet-cache live inside the project folder; only pointer-registrations may be global (PresentMon service SCM entry, scheduled task, HKCU Run value) and each must be logged with cleanup command in `global-installs.md` the moment it's created. Pre-existing & never-touch: .NET SDK 9 at C:\Program Files\dotnet, PawnIO at C:\Program Files\PawnIO (owned by FanControl!).

**Key hardware fact (from FanControl v3.json, C:\Program Files (x86)\FanControl\Configurations):** board SuperIO = **Nuvoton NCT6687D**, LHM-on-PawnIO reads it today (FanControl v240): 8 fan channels = CPU Fan, Pump Fan, System Fan #1–6 at `/lpc/nct6687d/fan/0-7`. M0 spike (a) resolved except drive temps (FanControl had Storage disabled by choice, so unverified). User runs FanControl for fan control — Halo must stay monitor-only and coexist (LHM global ISA mutex). Clock skin's "Day: 200" = day-of-year (%j), intentional.

See [[overlay-frametime-findings]] for the live empirical validation done this session.
