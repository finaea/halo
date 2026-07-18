---
name: overlay-frametime-findings
description: Live-verified how the Rainmeter skins get their data, and why RTSS vs Afterburner frametime disagree
metadata:
  type: project
---

Session findings from reading live shared memory on the user's PC (i5-14600K: 6 P-cores logical 0-11 hyperthreaded / 8 E-cores logical 12-19; dual-GPU; 144Hz), validating the Rainformer skins:

- **CPU core % (CPU-RAM.ini):** UsageMonitor plugin → Windows perf counters (`\Processor(N)\% Processor Time`), NOT HWiNFO. Core count from `%NUMBER_OF_PROCESSORS%`. Skin bars 1-12 = P-cores, 13-20 = E-cores (verified via GetSystemCpuSetInformation EfficiencyClass).
- **GPU2.ini is repurposed as an FPS counter:** pulls MSI Afterburner MAHM shared memory. DataSource mapping: `Framerate`→big FPS number; `Framerate 1% Low`→"1p LOW"; `Frametime`→"FRAMETIME". Names verified to match MAHM exactly. Skin does NO math — pure pass-through.
- **Frametime mismatch explained (live-proven):** RTSS raw `dwFrameTime` (RTSSSharedMemoryV2, µs) read 0.353ms in Zenless Zone Zero while Afterburner `Framerate`=137 and Afterburner `Frametime`=15-20ms. Cause = **frame generation** decoupling raw Present() rate from displayed frames. RTSS raw = instantaneous present interval (fooled by frame-gen); Afterburner Framerate = reliable count-based average; Afterburner Frametime ≠ 1000/FPS. Trust FPS + 1%/0.1% lows; both frametime readouts unreliable while frame-gen on. This is exactly the CPU/GPU/display-frametime ambiguity **PresentMon resolves** — concrete justification for the [[overlay-app-project]] PresentMon choice.
- **Poll timing:** skins use Update=1000; Afterburner default polling ~1000ms; HWiNFO default ~2000ms. Rainmeter can only read Afterburner/HWiNFO shared memory, not RTSS raw per-frame.
