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

**Repro commands** (unelevated PowerShell):
CPU: `TotalProcessorTime` delta over 12 s per process. GPU:
`(Get-Counter '\GPU Engine(*)\Utilization Percentage').CounterSamples | ? InstanceName -match "pid_<pid>_"` summed.
Context stamp: `Halo.Collector.exe --dump | Select-String "fps.app.name|fps.presented|fps.tap.active"`.
