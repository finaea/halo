# Halo resource usage

Measured CPU, GPU and memory use for Halo's processes, and how each optimisation changed them. The
most recent figures come first; the dated sections below record how they were reached.

All measurements were taken on the development machine: a 20-thread Intel Core i5-14600K, an
NVIDIA RTX 5070 Ti, and a 60 Hz monitor for the widgets. "% of one core" is the share of a single
logical CPU; divide by 20 for the share of the whole machine.

## Summary

| State | Halo CPU (% of one core) | Share of the whole machine | Halo RAM | Measured |
|---|---|---|---|---|
| Idle desktop, no game | **8.3%** | **0.42%** | ~300 MB | 2026-07-20 |
| Uncapped game at ~223 fps, both FPS widgets shown | **41.4%** | **2.1%** | 236 MB | 2026-07-20 |
| Uncapped game at ~231 fps, one FPS widget shown | **25.8%** | **1.3%** | 260 MB | 2026-07-20 |

Later measurements of individual processes, both unelevated (2026-09-13):

- **Collector** at the current provider rates, idle desktop: **3.87% of one core**, 125 MB.
- **Widgets**, 11 widgets at the default 5 Hz: **2.47%**; all 11 at the 10 Hz maximum: **3.92%**
  (with a game running).

For comparison, the stack Halo replaced — Rainmeter with Rainformer, HWiNFO, MSI Afterburner, RTSS
and the NVIDIA overlay — used **19–27% of one core all the time**, with or without a game, and
**1.3–1.8 GB of RAM**, growing through a play session. GPU use is negligible for both (under 2%).

An uncapped game at 220+ fps is the worst case for Halo, because its frame capture work grows with
the frame rate. A frame rate cap reduces it roughly in proportion. Halo's cost also runs on spare
cores outside the game's own frame path, while an overlay such as RTSS draws inside the game's
render thread, where its cost shows up as lower fps rather than in Task Manager.

## Method

- **CPU:** the change in `TotalProcessorTime` for each process over a fixed window (12 s for the
  July measurements, 120 s after 25 s of settling for the September ones), plus per-thread changes
  inside the collector.
- **GPU:** `\GPU Engine(pid_*)\Utilization Percentage` added up per process ID over a 3 s sample.
- **Context:** `Halo.Collector.exe --dump | Select-String "fps.app.name|fps.presented|fps.tap.active"`
  records which game was being measured.

The commands, from an unelevated PowerShell:

```powershell
# GPU share for one process
(Get-Counter '\GPU Engine(*)\Utilization Percentage').CounterSamples |
  Where-Object InstanceName -match "pid_<pid>_" | Measure-Object CookedValue -Sum
```

---

## 2026-07-20: first measurement under load

Taken with an uncapped game at about 218 fps, the tap lane active (about 218 present events a
second), and the frame graphs redrawing at their wake limit of the time, about 140 times a second.
This was deliberately the worst case.

| Process | CPU (% of one core) | Share of the machine | GPU | RAM |
|---|---|---|---|---|
| **Halo.Widgets** | **24.8%** | 1.24% | 1.7% | 121 MB |
| **PresentMonService** (bundled child process) | **16.6%** | 0.83% | 0.0% | **190 MB** |
| Halo.Collector | 6.5% | 0.33% | 0.0% | 74 MB |
| dwm (shared; includes Halo's composition and everything else) | — | — | 2.6% | — |

**Halo in total: about 2.4% of the machine, or 48% of one core.** GPU use was negligible throughout.

What drove each process's cost at that point:

- **Halo.Widgets** — Every frame event redrew both FPS widgets completely, about 140 times a second:
  every text function ran, Direct2D redrew everything, and DirectComposition committed. Numbers that
  change on every redraw also created a new DirectWrite text layout each time, and the layout cache
  discarded **all 512 entries** when it filled, so unchanging labels were rebuilt too.
- **PresentMonService** — The service sampled GPU and CPU telemetry (power, clocks, temperatures)
  for its own metrics, which Halo never reads, at its default rate. It also flushed ETW every 5 ms.
- **Halo.Collector** — No single thread went above 0.5% of a core; the cost was spread across about
  20 provider and ETW threads. The tap's ETW thread reads every DXGI and Direct3D 9 event on the
  system (browsers included) and filters by process ID afterwards.

## Round 1: the load-related fixes

Three changes, measured again the same evening with the game at about 229 fps:

1. **The service's telemetry sampling was turned down** with
   `pmSetTelemetryPollingPeriod(session, 0, 5000)` when the SDK starts.
2. **The frame graph wake limit went from 7 ms to 16 ms**, matching the 60 Hz widget monitor. Redrawing
   faster than the monitor can show wastes CPU.
3. **The layout cache keeps two generations** instead of discarding everything when full, so
   unchanging labels are no longer rebuilt.

| Process | Before (218–252 fps) | After (229 fps) | Change |
|---|---|---|---|
| Halo.Widgets | 24.8–41.6% of a core, 121→213 MB | **17.7% of a core, 119 MB** | About −50% CPU |
| PresentMonService | 16.6–21.1% of a core, 168–190 MB | **14.3% of a core, 65 MB** | −20% CPU, **−120 MB RAM** |
| Halo.Collector | 6.5–8.3% of a core | 9.1% of a core | About the same (tap events grow with fps) |
| **Total** | 48–71% of a core, 385–462 MB | **41% of a core (2.1% of the machine), 312 MB** | About −40% CPU, −25% RAM under load |

## Round 2: making frame capture quiet when nothing is running

An idle-desktop measurement (game closed) showed that the service's CPU use did not depend on load
at all:

| Process | CPU (% of one core) | RAM |
|---|---|---|
| Halo.Widgets | **1.9%** — no frame events, 5 Hz ticks, as designed | 141 MB |
| Halo.Collector | 6.4% — providers, plus the tap reading desktop presents | 74 MB |
| **PresentMonService** | **15.6% — higher than under game load** | 23 MB |
| **Halo total** | **23.9%** | 238 MB |
| The old stack, same window | 17.6% (constant), plus 1,836 MB of memory | |

The service stayed at 12.6–15.6% whether or not a game was running. That pointed at the 5 ms ETW
flush (200 kernel buffer flushes a second, always) and the stream of graphics events from the whole
desktop, rather than at frame processing. At that point idle Halo cost more than the old stack.

The fix was to make frame capture idle-aware. After **10 s without frames** from the tracked
process — based on frames, not on which window has focus, since any app becomes the target when
focused but only apps that present produce frames — the service's flush relaxes to 100 ms, the tap's
providers are muted, and the tap's own flush slows to 250 ms. New frames wake it again within about
150 ms through the resolved lane, so the presented FPS widget uses the resolved figures for the
first moment of a game. The normal flush period also changed from 5 ms to 10 ms at the same time.

| Process | Idle before round 2 | Idle after |
|---|---|---|
| PresentMonService | 15.6% of a core | **0.0%** |
| Halo.Collector | 6.4% | 5.1% |
| Halo.Widgets | 1.9% | 3.2% (within normal variation) |
| **Halo total** | **23.9% of a core** | **8.3% of a core (0.42% of the machine)**, 299 MB |

Idle Halo now costs **less than half of the old stack's constant 17.6%**.

## Compared with the old stack (2026-07-20)

The old stack turned out to be **still running** alongside Halo, so both were measured live with the
same method, minutes apart. The first old-stack sample was taken while the game was in the
background and the Halo samples while it was in front (218 → 252 fps, uncapped), which favours the
old stack, because its cost barely depends on load while Halo's grows with frame events.

| Old stack process | CPU (% of one core) | GPU | RAM |
|---|---|---|---|
| Rainmeter (Rainformer skins) | 11.7% | 0.3% | 283 MB |
| HWiNFO64 | 13.1% | 0% | 43 MB |
| MSI Afterburner | 1.0% | 0% | 41 MB |
| RTSS + HooksLoader | ~0% visible; its real cost is *inside the game's frame time* | 0% | 76 MB |
| NVIDIA Overlay (5 processes) | 0.5% | 0% | **735 MB** |
| nvcontainer (4 processes) | 0.6% | 0% | 81 MB |
| *(NVDisplay.Container left out — driver infrastructure that stays either way)* | | | *(141 MB)* |
| **Old stack total** | **27% of a core (1.35% of the machine), constant** | 0.3% | **~1,260 MB** |

A second comparison after round 1, with both stacks sampled in **the same 12 s window** and the game
steady at about 220 fps uncapped:

| | Old stack | Halo (after round 1) |
|---|---|---|
| CPU | 19.2% of one core, constant (Rainmeter 7.3 + HWiNFO 10.7) | **37.6% of one core** (48–71% before round 1) |
| RAM | **1,818 MB and rising** — nvcontainer grew 81 → 425 → **675 MB** during the session (replay and capture buffers), NVIDIA Overlay 765 MB | **248 MB** (the service down to 33 MB after the telemetry change) |

In summary:

- **RAM: a large saving**, about 1.3–1.8 GB down to about 0.25 GB. The NVIDIA overlay's five
  processes (735 MB) were the largest single item, and Halo covers what it was used for.
- **GPU: no difference.** Both were under 2% on this card.
- **CPU on an idle desktop: a saving of more than half.** Rainmeter's skin updates and HWiNFO's
  sensor polling never stop, while Halo's frame capture goes quiet without a game.
- **CPU in a game: Halo's visible figure is higher**, but the two are not measured the same way.
  Halo's cost runs on spare cores, outside the game. RTSS and overlay costs run *inside* the present
  path — the hook and on-screen display are drawn by the game's own render thread — so they reduce
  fps directly in a way Task Manager never shows. On a 20-thread CPU with cores to spare, background
  CPU is cheap; time inside the frame is not.

## Final state, 2026-07-20

With Rainmeter, HWiNFO, Afterburner, RTSS and the NVIDIA overlay shut down, and the game running
uncapped at about 223 fps:

| Process | CPU (% of one core) | GPU | RAM |
|---|---|---|---|
| Halo.Widgets | 21.6% | 1.2% | 140 MB |
| PresentMonService | 10.8% (16.6–21.1% before the changes) | 0% | 22 MB |
| Halo.Collector | 9.1% | 0% | 75 MB |
| **Total** | **41.4% of a core (2.1% of the machine)** | 1.2% | **236 MB** |

Idle: **8.3% of one core, about 300 MB** (see round 2). The idle mode also passed its first real
test: the log shows active → idle → active transitions following the game exactly, including waking
again when the game returned.

The only other monitoring-related residue was `nvcontainer` (3 processes, 445 MB), which is NVIDIA
driver and app infrastructure that stays after the overlay is turned off.

### With one FPS widget turned off

With the DISPLAYED FPS widget turned off in Settings (leaving only PRESENTED), under a similar load
(~231 fps, frame generation ×2.02, tap active):

| Process | CPU (% of one core) | GPU | RAM |
|---|---|---|---|
| Halo.Widgets | **12.5%** (21.6% with both FPS widgets) | 0.96% | 134 MB |
| PresentMonService | 7.1% | 0% | 32 MB |
| Halo.Collector | 6.2% | 0% | 94 MB |
| **Total** | **25.8% of a core (1.3% of the machine)** | ~1% | **260 MB** |

- **Widgets −9.1 points (−42%).** A turned-off widget's window is fully removed — no layout, no
  drawing, no frame event wakes. This is the direct cost of one FPS widget redrawing on every frame
  event.
- The service and collector each read about 3 points lower than in the final-state table. Neither
  depends on which widgets are shown (the collector publishes both lanes either way), so this is
  normal variation between runs, not a saving.

---

## Collector rates, 2026-09-13

The provider cadences became code constants in `CollectorRates.cs`, and three were raised:
**disk-io 5 → 10 Hz, network 5 → 10 Hz, drive temperatures every 30 s → every 10 s** (`cpu-kernel`
and `nvml` were already at 10 Hz). The isolated test in
[current-metrics-inventory.md § Provider cost measurement](current-metrics-inventory.md#provider-cost-measurement)
predicted that the five increases together would cost **+0.809 points of one core**. This section
checks that prediction for the whole process.

**Method.** Two builds of the same commit — one with the new rates and one with the old, differing
only in `CollectorRates.cs` — were run **alternately** (new, old, new, old…), so any change in the
machine over the ~20-minute run affects both equally. Each run had 25 s to settle and then a 120 s
window, four times each. Unelevated, idle desktop, with a production Halo running throughout.

| Build | % of one core, per run | Mean | Standard deviation | RAM |
|---|---|---|---|---|
| **New rates** | 3.763 · 3.958 · 3.672 · 4.075 | **3.867** | 0.183 | 125.0 MB |
| Old rates | 3.203 · 3.437 · 3.555 · 4.492 | **3.672** | 0.566 | 125.4 MB |
| **Difference, per pair** | +0.560 · +0.521 · +0.117 · **−0.417** | **+0.195** | 0.455 | −0.4 MB |

### The change is too small for this method to see

Two of the five increases do nothing unelevated (`lhm-storage` never starts without administrator
rights, and `cpu-kernel` and `nvml` were already at 10 Hz), so what this comparison could actually
detect is **disk-io +0.255 and network +0.037 = +0.29 points**. The spread between pairs is
**±0.46** — larger than the effect. The fourth pair even came out negative, because that old-rate
run (4.49%) was the busiest sample of the whole set.

So the result *agrees with* +0.29 but cannot confirm it. That is not a problem with the change; it
is the limit of measuring a whole process's CPU time on a machine doing other work. **The per-poll
test remains the precise tool** for questions this small. This method is meant for whole-process
comparisons of one point and above.

### The cross-check that does work

Each provider's occupancy, from the live provider table: 25 samples over 50 s at the new rates
(`occupancy = median lastPollMs × rateHz ÷ 10`, that is, % of one core of *thread* time):

| Provider | Rate | Median ms | 90th percentile ms | Occupancy (% of one core) |
|---|---|---|---|---|
| `builtin` | 1 Hz | 0.83 | 1.05 | 0.083 |
| `cpu-kernel` | 10 Hz | 0.18 | 0.28 | 0.180 |
| `process` | 1 Hz | 6.35 | 7.41 | 0.635 |
| `disk-io` | 10 Hz | 0.39 | 0.49 | 0.392 |
| `network` | 10 Hz | 0.17 | 0.29 | 0.171 |
| `nvml` | 10 Hz | 0.40 | 0.67 | 0.400 |
| `lhm-cpu` | 5 Hz | 7.21 | 16.16 | 3.604 |
| `lhm-superio` | 1 Hz | 0.01 | 0.03 | 0.001 |
| `lhm-gpu` | 1 Hz | 77.75 | 93.48 | 7.775 |
| `lhm-storage`, `presentmon`, `pclstats` | — | — | — | Unavailable unelevated |
| **Total occupancy** | | | | **13.24** |

Occupancy is 13.2% of a core, but the process measures **3.87%**. That is exactly what the per-poll
measurement showed: most of the LibreHardwareMonitor parts' time is spent *waiting*, not computing.
Applying the measured CPU-to-wall ratios (`lhm-gpu` 29%, `lhm-cpu` 1.1%, everything else about 100%)
to the table above predicts **4.06% of one core**. The measured mean is **3.867%** — a 5% gap
between two completely independent methods, which is good evidence that both are right.

`lhm-gpu` alone is 7.8 points of occupancy and about 2.2 points of CPU, for two metrics. It stays at
1 Hz only because on an AMD or Intel machine it provides the entire GPU widget.

## Widget refresh rate, 2026-09-13

Each widget now has its own refresh rate (`rateHz`), limited by the fastest metric it shows
(0.5–10 Hz), and graphs take **one sample per widget tick** instead of running on their own 1–5 Hz
clock. So the top of the Settings slider is also the top of the widget process's cost, and this
measures it. The estimate beforehand was "at most 10 Hz × about 11 widgets ≈ twice the idle 1.9% of
a core".

**Method.** The same `TotalProcessorTime` change as above — 25 s to settle, then a 120 s window —
on the Halo.Widgets process. The two settings were run **alternately** (5, 10, 5, 10) by editing
`rateHz` in the config and letting Halo apply it live, so nothing was restarted or rebuilt between
runs. An 11-widget layout, an unelevated development collector, and a production Halo running
throughout.

| Setting | % of one core, per run | Mean | RAM |
|---|---|---|---|
| All 11 widgets at 5 Hz (default) | 2.318 · 2.630 | **2.474** | 118–123 MB |
| All 11 widgets at 10 Hz (slider maximum) | 3.710 · 4.127 | **3.919** | 123–125 MB |
| **Difference, per pair** | +1.392 · +1.497 | **+1.445** | +2 MB |

**3.92% of one core is the most a user can ask for** — every widget at the highest setting the slider
offers. That is 1.58× the 5 Hz default rather than 2×, and very close to the estimate (2 × 1.9 = 3.8).
Doubling the tick does not double the cost because most of a tick is comparison: text elements
compare strings and bars round to 1/200 (`Render/Elements.cs`), so a tick that changes nothing costs
layout work and no drawing at all.

Two notes on the figure:

- **The machine was not idle.** A game was running on the main monitor throughout, which is also why
  the two runs of each setting differ by about 0.3 points. The difference between pairs is the
  reliable figure here; the absolute numbers include the game's variation.
- **Running unelevated makes the 10 Hz figure slightly higher.** Without SuperIO access the Fans
  widget has no `fan.*` metrics, so `PanelRates.MaxHz` finds nothing to limit it and allows the full
  10 Hz. With an elevated collector, the Fans widget is limited to its real 1 Hz and cannot be
  raised, so a real installation's 10 Hz worst case is a little cheaper than measured here.

The FPS widget is left out of both on purpose: it is woken by frame events, with a fixed 5 Hz tick in
between, so its cost does not change with the slider.

---

## Open at the time of the last measurement

- **An elevated measurement of the collector at the current rates.** `lhm-storage`, `lhm-cpu`'s real
  MSR reads and frame capture did not run in the 2026-09-13 comparison, because no UAC prompt was
  available.
- **The text layout path for values that change on every redraw**, which could reduce the FPS
  widget's remaining cost further.
- **Separate flush settings for the service and the tap**, only needed if the shared 10 ms proves too
  slow for the tap.
- **Occasional long `lhm-cpu` polls** (a `lastPoll` of 243 ms was once seen in the status log) —
  possibly only when FanControl is holding the SuperIO mutex.
- **Memory over long sessions:** Halo.Widgets grew from 121 to 213 MB over about 20 minutes of heavy
  frame events before round 1, and the collector measured 75 → 94 MB in a later run. Neither has been
  confirmed as a leak.
