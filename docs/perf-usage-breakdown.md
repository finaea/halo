# Halo resource usage — measured breakdown & optimization ranking

**Measured:** 2026-07-20 ~00:10, on the live system.
**Workload during measurement (worst-case, deliberately):** `Client-Win64-Shipping` running
**uncapped at ~218 fps**, tap active → present events at ~218/s, frame-graph repaints pinned at
the ~7 ms coalesce cap (~140/s). Idle-desktop numbers will be far lower (no events → 5 Hz ticks)
— measure those separately before optimizing anything that only hurts under load.
**Method:** 12 s `TotalProcessorTime` delta per process; per-thread deltas inside the collector;
`\GPU Engine(pid_*)\Utilization Percentage` summed per PID (3 s sample).

## The ranking

| # | Process | CPU (one core) | CPU (of 20-thread machine) | GPU | RAM |
|---|---|---|---|---|---|
| 1 | **Halo.Widgets** | **24.8%** | 1.24% | 1.7% | 121 MB |
| 2 | **PresentMonService** (bundled child) | **16.6%** | 0.83% | 0.0% | **190 MB** |
| 3 | Halo.Collector | 6.5% | 0.33% | 0.0% | 74 MB |
| — | dwm (shared; includes our composition + everything else) | — | — | 2.6% | — |

**Total Halo footprint under worst-case game load: ~2.4% of the machine, ~48% of one core.**
GPU is a non-issue across the board.

---

## 1. Halo.Widgets — 24.8% of one core (top target)

All of this is load-dependent: repaints are event-driven, and at 218 fps the two fps panels
repaint ~140×/s each wake. Cost drivers, ranked by suspicion (hypotheses — profile before fixing):

| Driver | Why suspected | Evidence status |
|---|---|---|
| Full-panel repaint per wake ×2 fps panels | every wake re-runs Update (all element lambdas) + full D2D redraw + DComp commit, ~140×/s | measured indirectly (CPU scales with event rate) |
| **Text-layout churn** | presented-panel numbers change per wake → every repaint formats new strings (`$"...{value:0.0}ms"`) → new `IDWriteTextLayout` per unique string; the layout cache **disposes ALL 512 entries when full** (RenderContext.Layout), so hot static labels get rebuilt too | code-level certainty; magnitude unmeasured |
| String/GC churn | ~12 text lambdas × 140 wakes/s = ~1,700 string.Format allocations/s on the fps panels | code-level certainty; magnitude unmeasured |
| Graph redraw | 188 bars × 2 series-fills per repaint | probably cheap (D2D rects) |

**Optimization candidates (ranked by value ÷ risk):**
1. **Coalesce 7 → 12–16 ms** (one literal in `App.OnFramesReady`): halves repaint count; graphs
   still advance ≥60×/s. Likely −40% of Widgets CPU under load for near-zero visible change.
2. **LRU/partial eviction for the layout cache** instead of clear-all-at-512: stops rebuilding
   static labels every sweep; also consider skipping the cache for volatile strings (draw them
   with `DrawText`-with-format instead of cached layouts).
3. **Skip text re-evaluation on event wakes** for elements whose metrics only change at
   collector cadence (displayed panel text can't change faster than the 40 Hz publish): dampen
   `Update` for TextEl to ~25 Hz while letting graphs run per wake.
4. Micro: cache formatted strings per rounded value (fps as int changes ~few times/s at most).

## 2. PresentMonService — 16.6% of one core, 190 MB RAM

Bundled Intel service, our child. Drivers:

| Driver | Why suspected | Action |
|---|---|---|
| **Internal hardware telemetry sampling** | the service polls GPU/CPU telemetry (power, clocks, temps) for its own metric system — **we never consume any of it** and never called `pmSetTelemetryPollingPeriod`, so it runs at the service default | **Strong lead, zero cost:** call `pmSetTelemetryPollingPeriod(session, 0, 5000)` (max period) at SDK start — likely a large cut of both CPU and the 190 MB (telemetry rings) |
| Manual ETW flush every 5 ms | 200 kernel buffer sweeps/s (`pmSetEtwFlushPeriod`) | raising `presentMonEtwFlushMs` 5→10 halves it, but **also slows the tap** (shared setting) — split into two settings first if pursued |
| Event volume at 218 fps | ETW parse + frame resolution + NSM writes per frame | inherent; scales with game fps |

## 3. Halo.Collector — 6.5% of one core (healthy)

No hot thread: no single thread exceeded 0.5% of a core in the sample — the cost is spread
across ~20 provider/ETW threads.

- **The feared lhm-cpu saturation did NOT reproduce.** Status logs earlier showed
  `lastPoll=243ms` (which would saturate its thread); this sample shows nothing close.
  Watch item: check `status:` log lines periodically — it may be intermittent (e.g. only when
  the SuperIO mutex is contended by FanControl).
- Tap ETW thread parses **all** DXGI/D3D9 events system-wide (browsers etc.) and filters by pid
  *after* parse; at higher desktop present volume this grows. Possible micro-opt: cheap
  provider/ID filter before payload parse (already mostly the case), or ETW-level PID filter
  (`EnableTraceEx2` filtering) — only worth it if this thread ever shows up in a profile.
- Tap flush loop: 200 `Flush()` syscalls/s, shared setting with the service (see above).

## 4. GPU + RAM notes

- Widgets GPU 1.7% at 140 repaints/s — D2D on the 5070 Ti is loafing; ignore.
- dwm's 2.6% includes composing our layered windows + the game + everything; not attributable.
- RAM: service 190 MB is the outlier (trace buffers + frame stores + telemetry rings — the
  telemetry fix above may shrink it); Widgets 121 MB (D2D + layout caches + .NET) and collector
  74 MB are unremarkable.

---

## Round 1 results (2026-07-20 ~00:25, game @229 fps — comparable load)

Steps 1+2 applied, plus a two-generation layout cache, plus the coalesce retuned to the
**60 Hz widget monitor** (16 ms, not the 12 ms the doc guessed — repaints beyond the panel
display's refresh are pure waste):

| Process | Before (218–252 fps) | After (229 fps) | Δ |
|---|---|---|---|
| Halo.Widgets | 24.8–41.6% core, 121→213 MB | **17.7% core, 119 MB** | ≈ −50% CPU; RAM growth suspect addressed (observe long-run) |
| PresentMonService | 16.6–21.1% core, 168–190 MB | **14.3% core, 65 MB** | −20% CPU, **−120 MB RAM** (telemetry rings) |
| Halo.Collector | 6.5–8.3% core | 9.1% core | ~flat (tap event volume scales with fps) |
| **Total** | 48–71% core, 385–462 MB | **41% core (2.1% machine), 312 MB** | ≈ −40% CPU, −25% RAM under load |

Remaining from the original order: idle baseline measurement, volatile-string path (step 4's
second half), split flush settings (step 5), lhm-cpu observation (step 6).

## Idle-desktop baseline (2026-07-20 ~00:50, game closed, Code foreground, fps N/A throughout)

| | CPU (one core) | RAM |
|---|---|---|
| Halo.Widgets | **1.9%** — event-silent, 5 Hz ticks, as designed | 141 MB |
| Halo.Collector | 6.4% — providers + tap parsing desktop presents | 74 MB |
| **PresentMonService** | **15.6% — HIGHER than under game load** | 23 MB |
| **Halo total** | **23.9%** | 238 MB |
| Old stack total (same window) | 17.6% (constant) + 1,836 MB parked | |

**Finding: the service's CPU is constant, not load-driven** (12.6–15.6% regardless of game
state) — pointing at the 5 ms manual ETW flush (200 kernel sweeps/s, always) plus the
desktop-wide graphics-event firehose, not frame processing. It is now two-thirds of Halo's
idle cost, and idle Halo (23.9%) currently sits *above* the old stack's constant (17.6%).

**Round 2 proposal — make the fps pipeline idle-aware:**
1. **Adaptive flush**: when no 3D target is tracked, re-issue `pmSetEtwFlushPeriod(100+)` and
   slow the tap's flush loop to match; restore the configured 5 ms the moment a target
   appears (both are runtime-adjustable calls; latency only matters while a game runs).
2. Optionally idle the tap harder: disable its DXGI/D3D9 providers while target = 0 so the
   desktop's present firehose (browsers, editors) never reaches the callback.
3. Re-measure idle; target: Halo idle well under the old stack's 17.6%.

### Round 2 results (2026-07-20 ~00:43, idle desktop, idle mode engaged)

Implemented: frame-based idle detection (10 s without frames for the tracked pid — NOT
focus-based; any desktop app becomes a target when focused, only presenting apps make frames)
→ service flush relaxed to 100 ms + tap providers muted + tap flush 250 ms. Frames resuming
re-arms within ~150 ms via the resolved lane (tap mute never blinds detection — the presented
panel rides its resolved fallback for the first moments of a game). Active flush default also
changed 5 → 10 ms (user preference).

| | Idle before round 2 | Idle after | 
|---|---|---|
| PresentMonService | 15.6% core | **0.0%** |
| Halo.Collector | 6.4% | 5.1% |
| Halo.Widgets | 1.9% | 3.2% (noise) |
| **Halo total** | **23.9% core** | **8.3% core (0.42% machine)**, 299 MB |

Idle Halo is now **less than half the old stack's constant 17.6%**, closing the one axis
where it still lost. Remaining backlog: volatile-string layout path, split service/tap flush
settings (only relevant if 10 ms proves too coarse for the tap), lhm-cpu spike observation,
Widgets long-session RAM observation.

## Suggested attack order

| Step | Change | Expected effect | Effort / risk |
|---|---|---|---|
| 1 | `pmSetTelemetryPollingPeriod` → max/off at SDK start | −big chunk of PresentMonService CPU + RAM | one call, near-zero risk |
| 2 | Coalesce 7 → ~12 ms | −~40% Widgets CPU under load | one literal; slight graph-smoothness trade |
| 3 | Measure idle baseline + re-measure under load | validates 1–2, quantifies layout churn | measurement only |
| 4 | Layout-cache LRU + volatile-string path | −remaining Widgets churn | small, contained |
| 5 | Split `presentMonEtwFlushMs` into service/tap settings, raise service side | −service flush cost, keeps tap live | small |
| 6 | Re-check lhm-cpu `lastPoll` spikes over a few sessions | confirm or retire the saturation theory | observation |

---

## Old stack vs Halo — measured head-to-head (2026-07-20 ~00:20)

The retired stack was found **still running** alongside Halo, so both sides were measured live
with the identical method, minutes apart. Caveat on states: the old-stack sample landed while
the game was backgrounded, the Halo samples while it was foreground (218 → 252 fps uncapped) —
which is the *unfavorable* direction for Halo, since the old stack's cost is nearly
load-independent while Halo's scales with frame events.

| Old stack process | CPU (one core) | GPU | RAM |
|---|---|---|---|
| Rainmeter (Rainformer suite) | 11.7% | 0.3% | 283 MB |
| HWiNFO64 | 13.1% | 0% | 43 MB |
| MSI Afterburner | 1.0% | 0% | 41 MB |
| RTSS + HooksLoader | ~0% (visible; hook cost hides *inside the game's frame time*) | 0% | 76 MB |
| NVIDIA Overlay (×5) | 0.5% | 0% | **735 MB** |
| nvcontainer (×4) | 0.6% | 0% | 81 MB |
| *(NVDisplay.Container excluded — driver infra that stays regardless)* | | | *(141 MB)* |
| **Old stack total** | **27% core (1.35% machine), constant** | 0.3% | **~1,260 MB** |

| Halo (game foreground, uncapped) | CPU (one core) | GPU | RAM |
|---|---|---|---|
| @218 fps | 48% core (2.4% machine) | 1.7% | 385 MB |
| @252 fps | 71% core (3.6% machine) | 1.8% | 462 MB |
| idle desktop (estimate — clean sample still needed) | ~12–15% core | ~0% | ~380 MB |

### Verdict: saved or lost?

- **RAM: big save — roughly −800 MB** (~1.26 GB → ~0.4 GB). The NVIDIA overlay's five
  processes (735 MB) were the elephant; Halo replaces that functionality for ~0.
- **GPU: wash.** Both sides <2% on this card — noise.
- **CPU at idle desktop: save, roughly half** (est. ~12–15% vs a constant 27% of one core —
  Rainmeter's 1 s skin updates + HWiNFO's sensor polling never stop; Halo's fps path goes
  quiet without a game). Needs one clean idle measurement to confirm.
- **CPU in-game: Halo's *visible* number is higher** (48–71% vs ~27% of one core), **but the
  comparison is between different currencies**: Halo's cost runs on spare cores, outside the
  game's frame path; RTSS/overlay cost executes *inside* the present path (hook + OSD drawn by
  the game's own render thread), taxing fps directly in a way Task Manager never attributes.
  On a 20-thread CPU with cores to spare, background % is cheap; in-frame microseconds are not.
  Halo's in-game cost also scales with fps (252 fps uncapped is the pathological case — a
  144-cap roughly halves the event rate) and steps 1–2 of the attack order above target
  exactly this number.
- **Action item this comparison surfaced: the old stack is still running.** Every number in
  the "old stack" table is being paid *right now, on top of Halo*. Decommissioning it
  (Rainmeter skins ×, HWiNFO ×, AB/RTSS ×, NVIDIA overlay off) is the single largest
  optimization available today: −27% of a core and −1.3 GB immediately.
- **Watch item:** Halo.Widgets RAM grew 121 → 213 MB across ~20 min under sustained event
  load — likely .NET heap lag vs the layout-cache churn, but worth watching for a leak.

### Re-measure after round 1 — simultaneous window (2026-07-20 ~00:40, game steady ~220 fps uncapped)

Both stacks sampled in the **same 12 s window** (eliminates the state-flip problem of the
earlier samples):

| | Old stack (still running) | Halo (post-round-1) |
|---|---|---|
| CPU | 19.2% of one core (constant; Rainmeter 7.3 + HWiNFO 10.7 dominate) | **37.6% of one core** (was 48–71% pre-round-1) |
| RAM | **1,818 MB and climbing** — nvcontainer has ballooned 81→425→**675 MB** across the session (replay/capture buffers); NVIDIA Overlay 765 MB | **248 MB** (service down to 33 MB post-telemetry-fix) |

Reading it honestly: under this pathological load (uncapped ~220 fps), Halo's visible CPU is
~2× the old stack's — but the old stack's ~19–27% is **constant at idle too**, its true RTSS
cost hides inside the game's frame time, and its RAM grows monotonically through a play
session while Halo's is flat at ~250 MB (7× less). At a frame cap or on the desktop, Halo
drops well below the old stack on every axis.

---

## Final state (2026-07-20 ~00:52) — old stack decommissioned

Rainmeter, HWiNFO, Afterburner, RTSS, and the NVIDIA overlay are **shut down**. Remaining
non-Halo residue: `nvcontainer` ×3 (445 MB) — NVIDIA driver/app container infrastructure that
survives overlay shutdown; trimmable via NVIDIA app background settings if desired.

Halo alone, game running uncapped ~223 fps (worst case):

| | CPU (one core) | GPU | RAM |
|---|---|---|---|
| Halo.Widgets | 21.6% | 1.2% | 140 MB |
| PresentMonService | 10.8% (was 16.6–21.1 pre-optimization) | 0% | 22 MB |
| Halo.Collector | 9.1% | 0% | 75 MB |
| **Total** | **41.4% core (2.1% of machine)** | 1.2% | **236 MB** |

Idle: **8.3% of one core, ~300 MB** (round-2 table above). The round-2 idle mode also passed
its first real-world exercise: the log shows active→idle→active transitions tracking the
game's lifecycle exactly (frame-based re-arm at 00:51:23 on game return).

Net vs the start of the evening (old stack constant 19–27% core + 1.4→1.8 GB growing, plus
pre-optimization Halo at 48–71% core): the machine now runs **one** monitoring stack at
2.1% total CPU under worst-case load, 0.4% idle, ~0.25 GB flat — with latency/DLSS telemetry
the old stack never had, and nothing injected into any game.

**Repro commands** (unelevated PowerShell):
CPU: `TotalProcessorTime` delta over 12 s per process. GPU:
`(Get-Counter '\GPU Engine(*)\Utilization Percentage').CounterSamples | ? InstanceName -match "pid_<pid>_"` summed.
Context stamp: `Halo.Collector.exe --dump | Select-String "fps.app.name|fps.presented|fps.tap.active"`.

---

## Collector rates after R1 (2026-09-13, ticket 02)

Ticket 02 turned the provider cadences into code constants and raised three of them:
**disk-io 5 → 10 Hz · network 5 → 10 Hz · drive temps every 30 s → every 10 s** (`cpu-kernel` and
`nvml` were already at 10 Hz after ticket 01). The isolated harness in
[current-metrics-inventory.md § Provider cost measurement](current-metrics-inventory.md#provider-cost-measurement)
predicted the five R1 raises would cost **+0.809 points of one core** in total. This is the
whole-process check of that prediction.

**Method** (as documented above): `TotalProcessorTime` delta over a fixed window, per process.
Two builds of the same commit — one with the R1 rates, one with the pre-ticket-02 ones, differing
in nothing but `CollectorRates.cs` — were run **interleaved** (new, old, new, old, …) so machine
drift across the ~20-minute run cancels between the arms. 25 s of settling, then a 120 s window,
4 repetitions each. Unelevated, idle desktop, Jack's production v1 stack running throughout.

| Arm | % of one core, per rep | mean | sd | RAM |
|---|---|---|---|---|
| **R1 rates** | 3.763 · 3.958 · 3.672 · 4.075 | **3.867** | 0.183 | 125.0 MB |
| pre-ticket-02 rates | 3.203 · 3.437 · 3.555 · 4.492 | **3.672** | 0.566 | 125.4 MB |
| **paired delta** | +0.560 · +0.521 · +0.117 · **−0.417** | **+0.195** | 0.455 | −0.4 MB |

### Reading it honestly: the change is below this method's noise floor

Two of the five raises are dormant on an unelevated run (`lhm-storage` never initialises without
admin; `cpu-kernel` and `nvml` were already at 10 Hz), so what this A/B could actually see is
**disk-io +0.255 and network +0.037 = +0.29 points**. The paired standard deviation is **±0.46** —
larger than the effect. The fourth pair even came out negative, because that `old` run (4.49 %) was
the busiest sample of the whole set.

So the result is *consistent with* +0.29 and cannot confirm it. That is not a failure of the change;
it is the honest limit of a process-level `TotalProcessorTime` delta on a machine doing other work.
**The per-sweep harness remains the precise instrument** for questions this size; this method is
for whole-process comparisons in the 1-point-and-up range, which is what it was built for.

### The cross-check that does work

Per-provider occupancy from the live provider table, 25 samples over 50 s at the R1 rates
(`occupancy = median lastPollMs × rateHz ÷ 10`, i.e. % of one core of *thread* time):

| Provider | rate | median ms | p90 ms | occupancy (% of one core) |
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
| `lhm-storage`, `presentmon`, `pclstats` | — | — | — | unavailable unelevated |
| **total occupancy** | | | | **13.24** |

Occupancy is 13.2 % of a core but the process measures **3.87 %** — which is exactly the point the
per-sweep measurement made: most of the LHM parts' wall time is *waiting*, not computing. Applying
the measured CPU:wall ratios (`lhm-gpu` 29 %, `lhm-cpu` 1.1 %, everything else ~100 %) to the table
above predicts **4.06 % of one core**. The measured mean is **3.867 %** — a 5 % gap between two
completely independent methods, which is the best evidence available that both are right.

`lhm-gpu` alone is 7.8 points of occupancy and ~2.2 points of the CPU, for two metrics. It stays at
1 Hz only because on an AMD or Intel machine it *is* the entire GPU panel (hardware plan H2).

### What this leaves open

- **Elevated cost is unmeasured.** No UAC was available. `lhm-storage` (the +0.233 of the
  prediction), `lhm-cpu`'s real MSR sweep and the PresentMon lane never ran. An elevated
  repetition of this same A/B is the missing datum.
- **Widget-side cost is unchanged by this ticket** and still the expensive half. The extra
  resolution from 10 Hz disk/network does not reach the screen until the per-widget Hz slider
  lands in ticket 03 — the graphs still sample once per widget tick.

---

## fps-displayed widget disabled (2026-07-20) — same game, uncapped ~231 fps

User disabled the DISPLAYED fps panel in Settings (one fps widget remains: PRESENTED).
Comparable load to the final-state row above (~223 → ~231 fps, FG ×2.02, tap active).

| | CPU (one core) | GPU | RAM |
|---|---|---|---|
| Halo.Widgets | **12.5%** (was 21.6% with both fps panels) | 0.96% | 134 MB |
| PresentMonService | 7.1% | 0% | 32 MB |
| Halo.Collector | 6.2% | 0% | 94 MB |
| **Total** | **25.8% core (1.3% of machine)** | ~1% | **260 MB** |

- **Widgets −9.1 points (−42%)**: the disabled panel's window is fully disposed — no layout,
  no paint, no frames-ready wakes for it. This is the per-fps-panel repaint cost measured
  directly; it also bounds what optimization #4 (layout/volatile-string path) can recover.
- Service/collector read ~3 points lower each than the final-state row; they don't depend on
  widget enablement (collector publishes both lanes regardless), so treat that as run-to-run
  variance, not a saving from the disable.
- Collector RAM 75→94 MB — added to the existing Widgets long-session RAM watch item.
