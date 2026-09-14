# Current Metrics Inventory

**The catalogue of what Halo collects.** The byte layout of the shared section is a different
document — [metrics-protocol.md](metrics-protocol.md) — and this one never repeats it.

**Originally compiled** 2026-07-19 from a live screenshot of the Rainformer desktop: the checklist
any replacement stack had to cover before HWiNFO + MSI Afterburner + RTSS + NVIDIA App could be
retired. That checklist is done and the old stack is decommissioned, so this is now simply the
catalogue.

**Regenerated against `Local\Halo.Metrics.v2` and the shipping code: 2026-09-13** (collector
ticket 02). Every **Example** below is a real value read out of the section on this machine on that
date with `Halo.Collector.exe --dump --json` (IPs redacted). Every rate is the `nominalRateHz` the
provider registered, read back out of the registry — not a number copied from a comment.

**286 metrics** were registered on that run. It was **unelevated**, so four families never
registered at all and are documented here from the code with an *elevated only* marker:
`fan.<n>.*`, `drive.<x>.temp.c`, `latency.queue/render/pc.ms` and `render.rate.hz`. An elevated
run adds those on top; how many that is depends on the board's fan-channel count and was not
measured here.

---

## How to read these tables

**Metric** — the shared-memory metric name. `<i>`, `<n>` and `<x>` are runtime-discovered indexes
(GPU, core/fan/rank, drive letter): read `gpu.count`, `cpu.logical.count`, `fan.count` or just
enumerate the registry. Convenience constants live in
[`MetricNames.cs`](../src/Halo.Metrics/MetricNames.cs), but no consumer needs them — the registry
is self-describing. `widget-side` means no metric exists: the widget computes it at paint time.

**Value semantics** — the registry's `semantics` byte, which tells a consumer whether averaging or
re-sampling the number is meaningful:

| | |
|---|---|
| **latest** | instantaneous sensor/OS read at poll time, no aggregation |
| **interval avg** | mean over the gap between two polls (Δcounter ÷ Δt) |
| **rolling window** | sliding time window over per-sample data; `windowMs` says how long |
| **cumulative** | since session start, or since the `reset-net` pipe command |
| **running max** | session extremum, latched until `reset-max` |
| **calc** | arithmetic over other metrics, no sensor of its own |
| **static** | written once when the hardware is discovered, and again only on a re-enumeration |

**Nominal Hz** — how often that number actually changes, which is not always the provider's poll
rate: `fps.app.name` is refreshed once a second inside a 40 Hz drain, the 1 % lows are recomputed
at 2 Hz behind a cache, `net.ip.external` every few minutes. **0 means static** — no cadence at
all, so its age grows without bound by design and a refresh slider should ignore it.

The registry also carries **`effectiveRateHz`**: the same number scaled by how well the provider is
really keeping up. The host measures each provider's actual poll rate over a rolling window and
republishes `nominal × measured ÷ configured` for every metric it owns, so an overrunning provider
shows up as a number below its constant. Measured on this run, every provider was within **1.2 %**
of nominal (`lhm-cpu` 5.06 against 5, `cpu-kernel` 10.06 against 10, `nvml` 9.95, `disk-io` 9.99,
`network` 9.98) — comfortably inside the 5 % the ticket asked for.

**Est. latency** — wall-clock from the thing physically happening to the pixel changing on screen,
as **min–max**. Built from the measured per-poll costs and the fixed cadences; see
[Estimated latency](#estimated-latency).

---

<a id="tail"></a>
## The shared publish → paint tail

**T1 · publish.** The provider calls `MetricSink.Set(name, value)`
([MetricSink.cs:51-54](../src/Halo.Collector/MetricSink.cs#L51-L54)): cached slot lookup, then one atomic
8-byte store into the shared section plus a QPC write-stamp (that stamp is what `age`/staleness is
computed from). **No rate of its own** — it runs inline inside the poll that produced the value.

**T2 · session max** (only metrics registered with `RegisterWithMax`). The same `Set` compares
against the running max and, if higher, does a second atomic store into the `<name>.max` slot
([MetricSink.cs:55-59](../src/Halo.Collector/MetricSink.cs#L55-L59)). **Rate = the base metric's
rate.** Cleared by the `reset-max` pipe command, which the widget context menu sends.

**T3 · widget wake.** The `Halo.Widgets` master loop sleeps in `MsgWaitForMultipleObjectsEx` until
the soonest window is due, a window message arrives, or the collector signals
`Local\Halo.FramesReady.v2` ([App.cs:133-150](../src/Halo.Widgets/App.cs#L133-L150)).

**T4 · read.** `MetricCache` → `CollectorSession.TryGet` resolves the name to a registry index once
and then reads value + timestamp; a changed `collectorStartQpc` throws the whole name→index cache
away, because a restarted collector rebuilds the registry and a stale index reads a *different*
metric ([metrics-protocol.md, reader rule 2](metrics-protocol.md)).

**T5 · paint.** Element lambdas re-evaluate, the flow layout runs, changed text/bars are redrawn
into the D2D surface and DirectComposition commits. Unchanged strings and bar fractions are
dirty-checked away, so a repaint on stale data costs layout work but no drawing.

---

<a id="estimated-latency"></a>
## Estimated latency — the model

Every **Est. latency** figure is `min–max`, built from three kinds of number:

- **measured** — per-poll execution cost, median (and p90 where it matters), from the `status:`
  lines in `logs/collector-*.log` and from the provider table's `lastPollMs`
- **configured** — poll periods from [`CollectorRates`](../src/Halo.Collector/CollectorRates.cs),
  flush periods and coalesce windows, read straight from the code
- **unverified** — marked *inline* wherever a source's own update cadence isn't known

**Min** assumes everything lands at the luckiest instant (the sensor updates the moment before a
poll, which lands the moment before a widget tick). **Max** assumes every stage misses by a full
period. Neither is typical — the middle of the range is.

### The two shared tails

**T — poll tail: 2–219 ms.** What every polled metric pays after the collector publishes it.

| Stage | Delay | Source |
|---|---|---|
| widget tick wait (5 Hz default) | 0–200 ms | [WidgetWindow.cs:32-35](../src/Halo.Widgets/WidgetWindow.cs#L32-L35) |
| `MetricCache.Tick` + `Panel.Update` + layout + D2D + DComp commit | ~2 ms | derived: 12.5 % of a core ÷ ≤62.5 repaints/s — includes the other widgets' ticks, so it's an upper estimate |
| DWM compose → widget monitor scanout | 0–16.7 ms | 60 Hz widget monitor |

**Tf — frame-event tail: 2–35 ms.** Replaces T on the **FPS panel only** — the one panel with a
frame graph, so the only one the `FramesReady` event pulls forward. Its 200 ms tick wait is
replaced by the 16 ms coalesce ([App.cs:176](../src/Halo.Widgets/App.cs#L176)); the repaint and
scanout stages are identical. With no frames flowing the panel falls back to **T**.

### Two things the totals deliberately separate

**Transport latency** (the bold number) is *how old the newest sample is by the time it is on screen*.
**Aggregation settle** is *how long until a change is fully reflected* — a rolling 1 s FPS shows
the newest frame within ~47 ms but takes a full second to finish moving. Only the frame graph has
neither: it draws raw per-frame values with no window at all.

### Assumptions, stated plainly

- **60 Hz widget monitor.** The 0–16.7 ms scanout stage scales with whatever panel the widget is on.
- **The ~2 ms repaint is derived**, not timed directly — a process-level CPU percentage divided by
  a repaint count.
- **ETW dispatch and blob-parse stages are assumed sub-ms.** In-memory work with no queue, but
  nothing measures them in isolation.
- **A source's own staleness is often unknown** — how stale an NVML reading, a SMART temperature or
  a SuperIO fan register already is before Halo reads it. Where the repo records a figure it is
  included; where it doesn't, the row says so.
- **Idle mode is excluded.** With no 3D app the fps pipeline relaxes its flush to 100 ms and mutes
  the tap, which makes the frame lanes much slower until a frame re-arms them (~150 ms).

### The spread, worst case

Ticket 02 moved four of these rows (rates plan R1). The old figure is shown where it changed.

| Rank | Metric group | Max | Why |
|---|---|---|---|
| 1 | `net.ip.external` | **~300 s** | 5-minute refresh interval; off by default |
| 2 | `drive.<x>.temp.c` | **~10.3 s** *(was ~30.5 s)* | 10 s SMART poll + a 258 ms read |
| 3 | `dlss.*` | **~10.2 s** | 10 s NGX module scan |
| 4 | `gpu.<i>.voltage.v` (LHM path) | **~1.3 s** | 1 Hz poll + a 78 ms NVAPI sweep |
| 5 | `proc.*` | **~1.22 s** | 1 Hz snapshot |
| 6 | `ram.used.gb`, `drive.<x>.used.b`, IPs, uptime | **~1.22 s** | 1 Hz builtin poll |
| 7 | `fan.<n>.*`, `cpu.vcore.v` | **~1.22 s** | 1 Hz SuperIO poll |
| 8 | `cpu.package.*`, `cpu.clock.mhz` | **~445 ms** | 5 Hz poll + a 26 ms MSR sweep |
| 9 | `cpu.total.pct`, `cpu.core.<i>.pct` | **~235 ms** *(was ~435 ms)* | 10 Hz poll + the 15.6 ms kernel tick |
| 10 | `gpu.<i>.*` (NVML), `net.*.bps`, `drive.<x>.*.bps` | **~219 ms** *(was ~419 ms)* | 10 Hz poll, sub-ms read |
| 11 | `fps.displayed` and friends | **~87 ms** | 40 Hz drain + the flip wait |
| 12 | `fps.presented` and friends | **~47 ms** | per-present push, 10 ms ETW flush |
| — | `cpu.core.<i>.class`, `gpu.count`, `sys.*`, `drive.<x>.total.b` | **n/a** | static; written at discovery and correct forever |

**Roughly half of every number above is the widget half.** `T` alone is 2–219 ms and is identical
for every polled metric, so nothing on a dashboard panel beats ~219 ms worst case no matter how
fast its provider runs. That is also why R1 stops at 10 Hz: past that the collector is no longer
the bottleneck. Only the FPS panel escapes it, via `Tf`.

---

## What each panel shows

One section per panel type in
[`PanelCatalog`](../src/Halo.Shared/Panels/PanelCatalog.cs) — the single declaration the renderer
and the Settings app both read, so this list cannot drift from the code without the build noticing.
`{gpu}`, `{n}`, `{x}`, `{stream}` and `{agg}` are resolved per widget from its options.

| Panel | Metric rows | Repeats | Needs |
|---|---|---|---|
| **clock** | `sys.uptime.s` + widget-side date/time/weekday | — | nothing |
| **cpu-ram** | `cpu.package.temp.c`, `cpu.total.pct`, `cpu.core.{n}.pct`, `cpu.clock.mhz`, `fan.{n}.rpm`, `ram.pct` | per logical CPU | PawnIO for temp/clock/fan |
| **gpu** | `gpu.{gpu}.temp.c / usage.pct / vram.pct / fan.pct / clock.core.mhz / clock.mem.mhz` | one widget per GPU | NVIDIA driver for the full set |
| **fps** | `fps.app.name`, `fps.{stream}`, `fps.low1.{stream}`, `fps.low01.{stream}`, `fps.frametime.{stream}.ms`, `…worst.ms`, `dlss.version` | — | PresentMon (elevated) |
| **latency** | `latency.pc.ms`, `latency.queue.ms`, `latency.render.ms`, `fps.displaylatency.ms`, `latency.click.ms`, `latency.allinput.ms`, `dlss.version`, `dlss.model`, `render.rate.hz` | — | Reflex (PCL Stats) markers |
| **power** | `cpu.vcore.v`, `gpu.{gpu}.voltage.v`, `cpu.package.power.w`, `gpu.{gpu}.power.w` (+ `.max`) | — | PawnIO for Vcore and package power |
| **drives** | `drive.{x}.label / temp.c / used.b / total.b / write.bps / read.bps` | per volume | PawnIO/SMART for temps |
| **network** | `net.ip.external`, `net.ip.internal`, `net.down.bps`, `net.up.bps`, `net.down.bps.max`, `net.down.total.b` | — | nothing |
| **fans** | `fan.{n}.rpm` | per fan channel | PawnIO + a SuperIO chip LHM knows |
| **topcpu** | `proc.topcpu.{agg}{n}.name / .cpu.pct / .ram.b`, `proc.count` | per rank (1–10) | nothing |
| **topram** | `proc.topram.{agg}{n}.name / .ram.b / .cpu.pct` | per rank (1–10) | nothing |

Three things the panels compute themselves rather than reading:

- **Fan percentage.** The collector publishes `fan.<n>.control.pct` when the SuperIO chip reports a
  PWM duty cycle for that channel, and nothing else; a board that doesn't expose one leaves the
  widget to derive `rpm ÷ max` from `fan.<n>.rpm.max` (hardware plan H4). The collector has no
  board-specific maximum table and never will.
- **Frame-gen multiplier.** `fps.displayed ÷ render.rate.hz`, falling back to `fps.fgratio`.
- **Everything on the clock panel except uptime** — `DateTime.Now`, captured once per widget tick.

---

## The registry, as published

Grouped by the provider that owns each metric. `n` is how many instances exist on this machine
(8 volumes, 20 logical CPUs, 10 ranks × 2 rankings). Read straight out of the section; nothing here
is hand-written.

<!-- BEGIN generated: Halo.Collector.exe --dump --json, 2026-09-13, unelevated -->

**`builtin`**

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `drive.<x>.label` | 8 | text | static | 0 *(static)* | Local NVME |
| `drive.<x>.total.b` | 8 | B | static | 0 *(static)* | 2,046.9e9 |
| `drive.<x>.used.b` | 8 | B | latest | 1 | 1,351.8e9 |
| `net.ip.external` | 1 | text | latest | 0.0033 | *(redacted)* |
| `net.ip.internal` | 1 | text | latest | 1 | *(redacted)* |
| `ram.pct` | 1 | % | latest | 1 | 79.761 |
| `ram.total.gb` | 1 | GB | static | 0 *(static)* | 31.725 |
| `ram.used.gb` | 1 | GB | latest | 1 | 25.304 |
| `sys.collector.version` | 1 | text | static | 0 *(static)* | 0.1.0 |
| `sys.elevated` | 1 | — | static | 0 *(static)* | 0 |
| `sys.os.build` | 1 | — | static | 0 *(static)* | 26,200 |
| `sys.pawnio.installed` | 1 | — | static | 0 *(static)* | 1 |
| `sys.pawnio.version` | 1 | text | static | 0 *(static)* | 2.2.0.0 |
| `sys.uptime.s` | 1 | s | cumulative | 1 | 136,580 |

**`cpu-kernel`**

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `cpu.core.<i>.class` | 20 | — | static | 0 *(static)* | 0 |
| `cpu.core.<i>.pct` | 20 | % | interval avg | 10 | 16.667 |
| `cpu.core.<i>.physical` | 20 | count | static | 0 *(static)* | 0 |
| `cpu.logical.count` | 1 | count | static | 0 *(static)* | 20 |
| `cpu.total.pct` | 1 | % | interval avg | 10 | 40 |

**`process`**

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `proc.count` | 1 | count | latest | 1 | 377 |
| `proc.topcpu.<n>.cpu.pct` | 10 | % | interval avg | 1 | 22.012 |
| `proc.topcpu.<n>.name` | 10 | text | latest | 1 | valheim |
| `proc.topcpu.<n>.ram.b` | 10 | B | latest | 1 | 3.2e9 |
| `proc.topcpu.agg.<n>.cpu.pct` | 10 | % | interval avg | 1 | 22.012 |
| `proc.topcpu.agg.<n>.name` | 10 | text | latest | 1 | valheim |
| `proc.topcpu.agg.<n>.ram.b` | 10 | B | latest | 1 | 3.2e9 |
| `proc.topram.<n>.cpu.pct` | 10 | % | interval avg | 1 | 22.012 |
| `proc.topram.<n>.name` | 10 | text | latest | 1 | valheim |
| `proc.topram.<n>.ram.b` | 10 | B | latest | 1 | 3.2e9 |
| `proc.topram.agg.<n>.cpu.pct` | 10 | % | interval avg | 1 | 22.012 |
| `proc.topram.agg.<n>.name` | 10 | text | latest | 1 | valheim |
| `proc.topram.agg.<n>.ram.b` | 10 | B | latest | 1 | 3.2e9 |

**`disk-io`**

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `drive.<x>.read.bps` | 8 | B/s | interval avg | 10 | 0 |
| `drive.<x>.write.bps` | 8 | B/s | interval avg | 10 | 43,675.386 |

**`network`**

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `net.down.bps` | 1 | B/s | interval avg | 10 | 20,355.531 |
| `net.down.bps.max` | 1 | B/s | running max | 10 | 3,612,454.504 |
| `net.down.total.b` | 1 | B | cumulative | 10 | 4,060,750 |
| `net.up.bps` | 1 | B/s | interval avg | 10 | 136,075.582 |
| `net.up.bps.max` | 1 | B/s | running max | 10 | 3,405,717.781 |
| `net.up.total.b` | 1 | B | cumulative | 10 | 8,557,551 |

**`nvml`** — one block per NVIDIA device; `gpu.count` counts NVML plus LHM's AMD/Intel cards.

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `gpu.<i>.clock.core.mhz` | 1 | MHz | latest | 10 | 3,030 |
| `gpu.<i>.clock.mem.mhz` | 1 | MHz | latest | 10 | 16,001 |
| `gpu.<i>.fan.pct` | 1 | % | latest | 10 | 43 |
| `gpu.<i>.fan.rpm` | 1 | rpm | latest | 10 | 1,386 |
| `gpu.<i>.name` | 1 | text | static | 0 *(static)* | NVIDIA GeForce RTX 5070 Ti |
| `gpu.<i>.power.w` | 1 | W | latest | 10 | 117.093 |
| `gpu.<i>.power.w.max` | 1 | W | running max | 10 | 131.373 |
| `gpu.<i>.temp.c` | 1 | °C | latest | 10 | 51 |
| `gpu.<i>.usage.pct` | 1 | % | rolling window (1 s) | 10 | 32 |
| `gpu.<i>.vendor` | 1 | text | static | 0 *(static)* | nvidia |
| `gpu.<i>.vram.pct` | 1 | % | calc | 10 | 24.279 |
| `gpu.<i>.vram.total.mb` | 1 | MB | static | 0 *(static)* | 16,303 |
| `gpu.<i>.vram.used.mb` | 1 | MB | latest | 10 | 3,958.277 |
| `gpu.count` | 1 | count | static | 0 *(static)* | 1 |

**`lhm-cpu`** *(values N/A on this unelevated run)*

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `cpu.clock.mhz` | 1 | MHz | latest | 5 | *N/A* |
| `cpu.name` | 1 | text | static | 0 *(static)* | Intel Core i5-14600K |
| `cpu.package.power.w` | 1 | W | latest | 5 | *N/A* |
| `cpu.package.power.w.max` | 1 | W | running max | 5 | *N/A* |
| `cpu.package.temp.c` | 1 | °C | latest | 5 | *N/A* |

**`lhm-superio`** *(values N/A on this unelevated run)*

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `cpu.vcore.v` | 1 | V | latest | 1 | *N/A* |
| `cpu.vcore.v.max` | 1 | V | running max | 1 | *N/A* |
| `fan.count` | 1 | count | static | 0 *(static)* | *N/A* |
| `fan.<n>.rpm` | *elevated only* | rpm | latest | 1 | — |
| `fan.<n>.rpm.max` | *elevated only* | rpm | running max | 1 | — |
| `fan.<n>.name` | *elevated only* | text | static | 0 *(static)* | — |
| `fan.<n>.control.pct` | *elevated only, when the chip reports a PWM duty* | % | latest | 1 | — |

**`lhm-storage`** *(the whole provider is unavailable unelevated — `lastError = unelevated`)*

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `drive.<x>.temp.c` | *elevated only* | °C | latest | 0.1 | — |

**`lhm-gpu`** — NVAPI/ADL extras; the only source for an AMD or Intel card's whole family.

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `gpu.<i>.voltage.v` | 1 | V | latest | 1 | 0.970 |
| `gpu.<i>.voltage.v.max` | 1 | V | running max | 1 | 0.975 |

**`presentmon`** *(values N/A here: no 3D app, and a production collector owns the one
PresentMon ETW session)*

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `dlss.fg.present` | 1 | — | latest | 0.5 | *N/A* |
| `dlss.model` | 1 | text | latest | 0.5 | *N/A* |
| `dlss.rr.present` | 1 | — | latest | 0.5 | *N/A* |
| `dlss.sr.present` | 1 | — | latest | 0.5 | *N/A* |
| `dlss.version` | 1 | text | latest | 0.5 | *N/A* |
| `fps.app.name` | 1 | text | latest | 1 | *N/A* |
| `fps.displayed` | 1 | fps | latest | 40 | *N/A* |
| `fps.displaylatency.ms` | 1 | ms | latest | 40 | *N/A* |
| `fps.fgratio` | 1 | — | latest | 40 | *N/A* |
| `fps.frametime.displayed.ms` | 1 | ms | latest | 40 | *N/A* |
| `fps.frametime.displayed.worst.ms` | 1 | ms | latest | 40 | *N/A* |
| `fps.frametime.presented.ms` | 1 | ms | latest | 40 | *N/A* |
| `fps.frametime.presented.worst.ms` | 1 | ms | latest | 40 | *N/A* |
| `fps.low01.displayed` | 1 | fps | latest | 2 | *N/A* |
| `fps.low01.presented` | 1 | fps | latest | 2 | *N/A* |
| `fps.low1.displayed` | 1 | fps | latest | 2 | *N/A* |
| `fps.low1.presented` | 1 | fps | latest | 2 | *N/A* |
| `fps.presented` | 1 | fps | latest | 40 | *N/A* |
| `fps.refresh.hz` | 1 | Hz | latest | 1 | *N/A* |
| `fps.tap.active` | 1 | — | latest | 1 | *N/A* |
| `latency.allinput.ms` | 1 | ms | latest | 40 | *N/A* |
| `latency.click.ms` | 1 | ms | latest | 40 | *N/A* |

**`pclstats`** *(the whole provider is unavailable unelevated — `lastError = unelevated`)*

| Metric | n | Unit | Semantics | Nominal Hz | Example |
|---|---|---|---|---|---|
| `latency.queue.ms` | *elevated only* | ms | rolling window (1.5 s) | 5 | — |
| `latency.render.ms` | *elevated only* | ms | rolling window (1.5 s) | 5 | — |
| `latency.pc.ms` | *elevated only* | ms | calc, derived | 5 | — |
| `render.rate.hz` | *elevated only* | Hz | rolling window (≥0.5 s) | 5 | — |

<!-- END generated -->

`latency.pc.ms` is new in ticket 02: queue + render + display, summed by the collector so a
third-party tool gets the same headline the LATENCY panel draws. Render is the mandatory
component (no marker-tagged frame, no PC latency); the other two add on when their provider has
them, each read back with a 3 s freshness bound so a frozen component cannot keep inflating the sum
([PclStatsProvider.cs](../src/Halo.Collector/Providers/PclStatsProvider.cs)).

### Changed since the 2026-09-13 dump

The table above is a transcript of one real run, so it is left as it was measured. Three things in
the `presentmon` block have moved since (audit-feedback ticket 02, 2026-09-14) and will show
differently on the next regeneration:

| Metric | Then | Now | Why |
|---|---|---|---|
| `fps.app.pid` | did not exist | 1 · — · latest · 40 | PID of the tracked foreground app, 0 when there is none. `pclstats` reads it back to scope its Reflex markers to one process, and the widget's frame graphs use it as their reset key so one game's bars never bleed into the next. |
| `latency.click.ms` | latest | rolling window (20 s) | Was a session average that got restamped fresh every poll, so a click from twenty minutes ago read as live. Now a real 20 s window, and N/A when it empties. |
| `latency.allinput.ms` | latest | rolling window (20 s) | Same. |

`fps.presented` and `fps.displayed` did not change semantics, but their **values** did: the rate
used to be frame count ÷ the span from the oldest to the newest frame in the window, and k frames
bound only k−1 gaps, so every reading was a flat **+1** (a locked 60 fps published 61 beside a
FRAMETIME row that said 16.7 ms). The rate now comes from the frametime intervals themselves. The
1 % / 0.1 % lows are unaffected — they already worked off frametimes.

---

<a id="source-providers"></a>
## Source Providers

Every provider runs on its **own thread** at `min(DefaultRateHz, MaxRateHz)`, with init backoff
1/5/30/60 s and a forced re-init after 10 consecutive poll failures; one provider crashing never
touches another ([ProviderHost.cs](../src/Halo.Collector/ProviderHost.cs)).

**Rates are code constants, not settings** (rates plan R1). They all live in
[`CollectorRates.cs`](../src/Halo.Collector/CollectorRates.cs); `settings.defaultRateHz` and the
per-provider overrides are gone. The host computes each period once at start-up, so a hot-reloadable
rate would not have taken effect anyway. What *is* configurable under `settings.json → collector`:
`frameLowsWindowS`, `presentMonEtwFlushMs`, `presentMonTransport`, `presentedTap`,
`networkInterface` and `externalIp{enabled,url,refreshMinutes}`.

**Every provider that enumerates hardware in `Initialize` re-runs it on the `rescan` control
command** (`RescanReinitialises`): builtin, cpu-kernel, disk-io, nvml, and the SuperIO, Storage and
GPU parts of LHM — seven in all. Three kinds of provider stay out:

- **presentmon, pclstats, network** own an ETW session or a counter baseline; tearing those down to
  look for a new fan would lose frame data or reset the session totals.
- **`lhm-cpu`** has nothing to re-discover (four fixed metrics, no indexed family, a CPU that
  cannot be hot-plugged) and re-opening it is *dangerous*: LibreHardwareMonitor 0.9.6 throws an NRE
  out of `CpuId.Get` on a re-`Open()` and, when two re-opens land a few seconds apart,
  access-violates inside `CpuId..ctor` and takes the whole process down (measured 2026-09-13). An
  AV is not catchable, so the only fix is not to ask.
- Repeat `rescan` commands **inside 10 seconds are dropped**, so a held-down button in a future
  Settings page cannot become a re-open storm across the other three LHM parts.

| Name | Installation source | OS support | Hardware support | Poll / push | Rate · cap · worth raising? |
|---|---|---|---|---|---|
| <a id="p-builtin"></a>**builtin** | **Inherent to Windows** — `kernel32` (`GlobalMemoryStatusEx`, `GetDiskFreeSpaceExW`, `GetVolumeInformationW`) plus .NET BCL sockets. The external-IP step is the one exception: an HTTPS GET to a **third-party service** (`api.ipify.org`), **off by default**. | Win10/11 x64. No elevation. | **Vendor-agnostic** — no hardware dependency at all. Volumes are discovered (`DriveType.Fixed \| Removable`, ready, not network or optical) and reconciled every poll, so a plugged-in drive publishes without a restart. | poll | **1 Hz** · cap **4 Hz** · **Not worth it** — uptime advances 1 s per second, and RAM and free space don't move meaningfully sub-second. Capacity and label are now written once at discovery rather than every poll, which drops one `GetVolumeInformationW` per volume per second (8/s here). |
| <a id="p-cpu-kernel"></a>**cpu-kernel** | **Inherent to Windows** — `ntdll!NtQuerySystemInformation` class 8, plus `GetLogicalProcessorInformationEx` once per init for the topology. | **Win10/11 x64.** No elevation. | **Vendor- and count-agnostic.** P/E class and physical-core id are read per logical CPU; above 64 logical CPUs it switches to `NtQuerySystemInformationEx` per processor group. ⚠️ **The multi-group path has no hardware here to test it on** — this machine is one group of 20. | poll | **10 Hz** · cap **64 Hz** · **Still the best headroom in the collector.** Counters advance on the ~15.6 ms kernel tick, so every step toward 64 Hz is genuinely new data at ~0.005 ms CPU per sweep. R1 stops at 10 because the widget tail (2–219 ms) dominates past that. |
| <a id="p-process"></a>**process** | **Inherent to Windows** — `ntdll!NtQuerySystemInformation` class 5. | **Win10/11 x64 only** — the 64-bit `SYSTEM_PROCESS_INFORMATION` field offsets are hard-coded ([ProcessProvider.cs](../src/Halo.Collector/Providers/ProcessProvider.cs)); a layout change in a future Windows would break it silently, which is why the System check surfaces a failed walk. No elevation. | **Vendor-agnostic.** | poll | **1 Hz** · cap **2 Hz** · **Marginal.** The heaviest poll in the collector by wall time (~6.4 ms: ~400-entry list + four LINQ sorts). Both rankings — per instance and same-name aggregated — are always published at 10 ranks each, so a widget's row count and aggregation toggle cost the collector nothing. |
| <a id="p-disk-io"></a>**disk-io** | **Inherent to Windows** — `pdh.dll`, LogicalDisk counter set. | **Win2000+**; locale-safe via `PdhAddEnglishCounterW`. No elevation. | **Vendor-agnostic** — any volume Windows exposes as a LogicalDisk. Rebuilds the PDH query when the volume set changes. | poll | **10 Hz** *(was 5)* · cap **64 Hz** · Essentially all CPU (102 % of wall). **Meaningful and cheap.** The raw counters update per I/O completion ([measured](#counter-granularity-measurement)), so the poll rate sets the resolution: at 5 Hz a 50 ms burst was smeared across 200 ms and under-reported ~4×. The raise cost **+0.255 points of one core**. |
| <a id="p-network"></a>**network** | **Inherent to Windows** — .NET `System.Net.NetworkInformation` over the IP Helper API. | **Win10/11.** No elevation. | **Vendor-agnostic** — any adapter that is Up and is not loopback/tunnel; `collector.networkInterface` pins one by name, `"Best"` picks the default-route one. | poll | **10 Hz** *(was 5)* · cap **64 Hz** · **Meaningful and cheap**, same as disk-io — octet counters update per packet batch ([measured](#counter-granularity-measurement)). The raise cost **+0.037 points**. |
| <a id="p-nvml"></a>**nvml** | **Ships with the NVIDIA display driver** (`nvml.dll`), not bundled by Halo. Loaded by name via P/Invoke; absent driver = provider unavailable with `no-nvml`. | **Windows with an NVIDIA driver.** No elevation. | **Every NVIDIA device**, ordered by PCI bus id so the index is stable across boots ([GpuIndexSpace.cs](../src/Halo.Collector/Providers/GpuIndexSpace.cs)). `nvmlDeviceGetFanSpeedRPM` is newer-driver only and probed at init. The power sanity ceiling comes from each card's own limit, so it scales to any model. AMD and Intel cards are covered by `lhm-gpu`. | poll | **10 Hz** · cap **20 Hz** · **Partly worth it.** Temp, clocks and power are read on demand and do get fresher; `gpu.<i>.usage.pct` does not — NVML computes it inside its own 1 s window, which the registry now says out loud (`semantics = rolling window`, `windowMs = 1000`) so the widget "?" can repeat it. Cap is 20 because one poll issues 8 calls at ~0.2–1 ms each. |
| <a id="p-lhm-cpu"></a>**lhm-cpu** | **Downloaded package** — `LibreHardwareMonitorLib` **0.9.6** NuGet (MPL-2.0), restored into the project-local cache `tools\nuget-cache`. Needs a **ring0 driver** (PawnIO) for MSR access — PawnIO at `C:\Program Files\PawnIO` belongs to FanControl and must never be uninstalled with Halo. | **Win10/11 x64. Requires elevation** — unelevated, `Initialize` finds no hardware, the provider table shows `unelevated`, and every CPU metric stays N/A. | **Intel and AMD.** The temperature match accepts `CPU Package` (Intel) or `Core (Tctl/Tdie)` (AMD); the clock logic is a name-agnostic max-of-non-bus. Verified on **i5-14600K**. | poll | **5 Hz** · cap **20 Hz** · **Don't raise — and the reason is occupancy, not CPU.** Just **1.1 % of its wall time is CPU** (2.4 ms of a 216 ms sweep); the rest waits on ring0 round-trips. But wall time is what blocks the period: at 10 Hz **2.78 % of polls overrun their 100 ms period**. ⚠️ Its wall cost is **machine-state dependent** — 216 ms/sweep on an idle machine vs 24–31 ms live. |
| <a id="p-lhm-superio"></a>**lhm-superio** | The same **LibreHardwareMonitorLib 0.9.6**; talks to the SuperIO chip over **ISA port I/O** behind a global mutex that FanControl also contends for. | **Win10/11 x64. Requires elevation.** | Whatever SuperIO chip LHM has a driver for. This board: **MSI NCT6687D**. **Every fan channel is published** — no cap, no name matching, numbering continuous across multiple SuperIO chips, in LHM identifier order. Channel numbers are positional indexes into that enumeration, **not** board header numbers, which is why the FANS widget carries a per-channel nickname. | poll | **1 Hz** · cap **2 Hz** · **Cheap, but the gain is unknown** — a fan tachometer needs at least one revolution (~30–60 ms at 1000–2000 rpm) and the NCT6687D's own register scan cadence has never been measured. The real cost isn't CPU, it's the global ISA mutex shared with FanControl. |
| <a id="p-lhm-storage"></a>**lhm-storage** | The same **LibreHardwareMonitorLib 0.9.6**; SMART / NVMe identify IOCTLs. | **Win10/11 x64. Requires elevation.** | Any SATA or NVMe drive that reports a temperature **and** a non-empty vendor/product descriptor — blank descriptors cannot be mapped to a volume letter, log a warning once and read N/A. Re-opens the LHM `Computer` when the volume set changes, so a hot-plugged drive gets a temperature. | poll | **every 10 s** *(was 30 s)* · cap **every 5 s** · Just **17 % of the sweep is CPU** (43.6 ms of 255 ms). **It was under-polled.** 30 s was chosen on wall cost (32 ms per drive × 8), not on how fast a drive's temperature moves — an NVMe can climb to its throttle point in well under 30 s. The raise cost **+0.233 points**. |
| <a id="p-lhm-gpu"></a>**lhm-gpu** | The same **LibreHardwareMonitorLib 0.9.6** → **NVAPI / ADL**. Covers what NVML cannot (GPU voltage has no public NVML API) and is the *only* source for non-NVIDIA cards. | **Win10/11. Works unelevated** (unlike the other three LHM parts). | **NVIDIA, AMD and Intel.** An NVIDIA card is matched to its NVML index by name and contributes only voltage + fan rpm; an AMD or Intel card claims its own index and publishes the whole `gpu.<i>.*` family from `SensorType.Temperature/Load/SmallData/Fan/Clock/Power/Control/Voltage`. Sensors the card doesn't report never register, so the widget row hides itself instead of showing a permanent N/A. ⚠️ **No AMD or Intel GPU was available to test that path.** | poll | **1 Hz** · cap **2 Hz** · **Still the worst value on an NVIDIA-only machine.** A ~78 ms NVAPI sweep (29 % of it CPU) yields two metrics. On an AMD or Intel machine the same sweep is the entire GPU panel, which is why the rate stays 1 Hz rather than dropping. |
| <a id="p-presentmon"></a>**presentmon** (resolved lane) | **Bundled download** — Intel **PresentMon 2** service + `PresentMonAPI2.dll` under `presentmon\` (MIT), spawned as a **console-mode child**, never SCM-registered. | **Win10 1709+** (ETW present tracking). **Elevation to own the ETW session**; attaching to an already-installed running PresentMon service works unelevated, which is why `needsElevation` is a flag and not a hard gate. | **GPU-vendor and graphics-API agnostic** — it observes the DXGK/present pipeline, so DX9/11/12, Vulkan and OpenGL all resolve. This is why it is the fallback lane for titles the tap cannot see. | poll (drain) | **40 Hz** · cap **120 Hz** · **No gain — it is a drain cadence, not a sample rate.** Frames carry their own `PRESENT_START_QPC`, so draining faster just splits the same frames into smaller batches. What moves the latency is `presentMonEtwFlushMs` (10 ms). Lows recompute at 2 Hz behind their own cache, and the registry now says 2 Hz for those four metrics rather than the provider's ceiling. |
| <a id="p-tap"></a>**present-tap** (door-1 lane) | **No download** — Windows **inbox ETW providers** `Microsoft-Windows-DXGI` and `Microsoft-Windows-Direct3D9`, consumed through `Microsoft.Diagnostics.Tracing.TraceEvent` **3.2.5**. Owned by the presentmon provider, not a separate `ProviderHost` entry. | **Win10/11. Requires admin** (owns a real-time ETW session). Silently skipped unelevated — the presented panel rides the resolved lane. | **GPU-vendor agnostic but API-limited**: DXGI (D3D10/11/12) and D3D9(Ex) only. **Vulkan and OpenGL titles emit no runtime present event** and fall back to the resolved lane (`fps.tap.active = 0`). | **push** (per present) | **Push — already per present**, no rate to raise. The only knob is `presentMonEtwFlushMs` (10 ms, clamped 1–100); lowering it cuts the buffer dwell but costs a kernel buffer sweep per flush. Relaxes to 250 ms when idle. |
| <a id="p-pclstats"></a>**pclstats** | **No download** — the markers are emitted by the **game's own NVIDIA Reflex SDK**; Halo just enables `PCLStatsTraceLoggingProvider` (GUID `0d216f06-…` taken literally from NVIDIA's reference `pclstats.h`, MIT) and consumes it via TraceEvent. | **Win10/11. Requires admin.** Reports `unelevated` and the host retries with backoff. | **Reflex-instrumented games only.** Nothing about it is GPU-specific — it reads game instrumentation, not hardware — but Reflex ships with NVIDIA titles in practice. No markers → the four metrics stay stale and the panel dims. | **push** (ETW markers) → periodic publish | **5 Hz** · cap **20 Hz** · **Free but pointless to raise.** The poll does no syscall at all — but the markers it averages arrive at only ~5–10 pings/s and the values are 1.5 s rolling means, so a faster publish republishes the same number. |
| <a id="p-max"></a>**`.max` session maxima** | **Internal** — the `MetricSink` running-max latch plus the `Halo.Control.v2` named pipe (`reset-max`). | Any; pure arithmetic. | N/A. | calc (inline with the base metric's `Set`) | **No rate of its own** — the compare runs inline inside the base metric's `Set`, so it is exactly as fast as whatever it tracks and costs one predicted branch. |
| <a id="p-widget"></a>**widget-side** | **Internal** — `Halo.Widgets`: the system clock, plus arithmetic over metrics already in shared memory. | Win10/11 x64 (DirectComposition + `WS_EX_NOREDIRECTIONBITMAP`). | N/A. | pull (per tick) / event (frame graphs) | **Text 5 Hz** by default · **This is the expensive half.** Raising the widget tick is the only way extra collector resolution reaches the screen; sampled graphs draw one point per tick regardless. Frame graphs are exempt: event-driven, coalesced to 16 ms ([App.cs:176](../src/Halo.Widgets/App.cs#L176)). |

---

<a id="provider-cost-measurement"></a>
## Provider cost measurement (2026-09-13)

**Why this exists.** Every cost figure here used to be `median lastPoll × rate`, and `lastPoll` is a
`Stopwatch` reading — i.e. **wall time**. For providers whose polls mostly *wait* on hardware, that
massively overstates CPU. It was measured properly and the two are now reported separately.

**Method.** A standalone harness replicated each provider's exact per-poll work — the same
`NtQuerySystemInformation` class, the same PDH counters, the same NVML calls, the same LHM
`Computer` parts and `Update()` sweep — and ran each at its old rate and then at the proposed one.
CPU was taken with `QueryThreadCycleTime` (calibrated at 3.46M cycles per CPU-ms, so microsecond
resolution instead of the 15.6 ms `GetThreadTimes` tick); I/O with `GetProcessIoCounters`. **Halo
was fully stopped** so nothing contended.

### Per sweep

| Provider | wall ms | **CPU ms** | **CPU as % of wall** | IO ops | IO bytes |
|---|---|---|---|---|---|
| `cpu-kernel` | 0.004 | 0.005 | — *(4 µs, below resolution)* | 0 | 0 |
| `network` | 0.203 | 0.117 | 58 % | 3 | 264 |
| `disk-io` | 0.199 | 0.202 | 102 % | 1 | 6,616 |
| `nvml` | 0.350 | 0.316 | 90 % | 0 | 0 |
| `lhm-gpu` | 77.7 | 22.2 | **29 %** | 4 | 0 |
| `lhm-storage` | 255.0 | 43.6 | **17 %** | 1,194 | 51,668 |
| `lhm-cpu` | 216.6 | 2.4 | **1.1 %** | 48 | 384 |

### What R1 actually cost

Of the seven raises that were costed, **five were adopted** and two (`lhm-cpu` 5→10 Hz, `lhm-gpu`
1→2 Hz) were rejected — they were 81 % of the predicted bill on their own.

| Provider | Change | Adopted? | **Predicted CPU Δ** (points of one core) | IO ops Δ |
|---|---|---|---|---|
| `cpu-kernel` | 5 → 10 Hz | ✅ *(landed in ticket 01)* | +0.001 | 0 |
| `network` | 5 → 10 Hz | ✅ ticket 02 | +0.037 | +15/s |
| `lhm-storage` | 30 s → 10 s | ✅ ticket 02 | +0.233 | +80/s |
| `disk-io` | 5 → 10 Hz | ✅ ticket 02 | +0.255 | +5/s |
| `nvml` | 5 → 10 Hz | ✅ *(landed in ticket 01)* | +0.283 | 0 |
| **the five adopted** | | | **+0.809 points of one core** = **+0.04 % of this 20-thread machine** | +100/s |
| `lhm-gpu` | 1 → 2 Hz | ❌ rejected | +1.652 | +4/s |
| `lhm-cpu` | 5 → 10 Hz | ❌ rejected (overruns) | +1.759 | +240/s |

The whole-process check of that prediction is in
[perf-usage-breakdown.md § Collector rates after R1](perf-usage-breakdown.md): the collector runs
at **3.87 % of one core** unelevated and idle at the R1 rates, and the raises are **below that
method's noise floor** (paired sd ±0.46 against a +0.29 expected effect on an unelevated run) — so
they are consistent with the prediction but not confirmed by it. What *is* confirmed there is the
per-sweep model itself: applying these CPU:wall ratios to the live provider table predicts 4.06 %
against a measured 3.87 %.

### What this changes

- **The LHM parts are not CPU-expensive, they are *occupancy*-expensive.** `lhm-cpu` spends 1.1 % of
  its poll computing. The old "13.21 % of a core" was thread occupancy; real CPU is 1.22 %.
- **Occupancy still matters** — it is what blocks a provider's period and causes overruns. The
  argument against `lhm-cpu` at 10 Hz stands unchanged, just for the right reason.
- ⚠️ **`lhm-cpu` wall time is machine-state dependent.** 216 ms/sweep here (idle machine, Halo
  stopped) versus 24–31 ms in the live collector, with CPU ~2.4 ms in both. Hypothesis —
  **unverified** — is core-wake latency: LHM reads per-core MSRs by switching thread affinity across
  all 20 logical CPUs, and on an idle machine those cores sit in deep C-states. If so, overrun risk
  is *worse* when the machine is idle, which also explains the 2.78 % of collector polls over 100 ms.

### Drives: SMART polling does not wake them

This machine has **three spinning HDDs** — E `TOSHIBA DT01ACA050`, G `WDC WD10PURZ-85U8XY0`,
I `HGST HTS541010A9E680` — alongside four SSDs (C, D, J NVMe; F SATA). Windows' spin-down timeout
is **20 min on AC** (`DISKIDLE` = 0x4b0), 10 min on DC.

All 11 measured sweeps completed in **218–277 ms**, at both 30 s and 10 s spacing, with no
multi-second outliers. A 3.5" HDD spin-up takes 5–10 s, so **no drive is being woken by the poll** —
at either interval. Per-sweep cost was also identical at both spacings, so nothing is being cached
or woken differently. This is what made the 10 s move safe.

**Open question:** whether the SMART IOCTL resets Windows' disk idle timer. If it does, a 10 s poll
against a 20-minute timeout means those three HDDs never park at all. Unverified — checking
`StartStopCycleCount` against `PowerOnHours` (elevated) would settle it. Note that no polling rate
within the 0.2 Hz cap would change this either way.

---

<a id="counter-granularity-measurement"></a>
## Counter-granularity measurement (2026-09-13)

Two provider comments claimed their source counters updated too slowly for the poll rate to
matter. Both were assumptions, never measured, and **both were wrong** — which is what justified
the disk-io and network raises. Recorded here so nobody re-derives them.

**Method.** Read the *raw* accumulating counter (not the PDH-computed rate) every 100 ms while
generating sustained load, then again while idle.

**`\LogicalDisk(C:)\Disk Write Bytes/sec`** — sustained 4 MB-chunk writes:

| sample | raw total | delta |
|---|---|---|
| 1 | 91,711,232,512 | 260,235,264 |
| 2 | 91,923,159,552 | 211,927,040 |
| 3 | 92,156,107,264 | 232,947,712 |
| … | *every 100 ms sample moved* | 210–260 MB |
| 12 | 93,936,171,520 | 12,607,488 ← writes stop |
| 14 | 93,936,187,904 | **16,384** ← a lone background write, while otherwise idle |

**`GetIPStatistics().BytesReceived`** (IP Helper — what `NetworkProvider` actually reads, not PDH)
— sustained download on the Ethernet adapter:

| sample | raw total | delta |
|---|---|---|
| 1 | 830,048,948 | 7,053,652 |
| 2 | 836,271,320 | 6,222,372 |
| … | *every 100 ms sample moved* | 3–7 MB |
| 11 | 879,196,144 | **90** ← ambient traffic resolved at 100 ms once idle |

**Conclusion.** Both counters are updated per I/O completion / per packet batch. Neither has a
~1 s granularity. Therefore:

- Each published `drive.<x>.read/write.bps` and `net.down/up.bps` is a **real average over one
  poll period**, not a re-division of a stale integer.
- **The poll rate sets the resolution.** At the old 5 Hz a 50 ms burst was smeared across 200 ms and
  under-reported roughly 4×; at 10 Hz it is smeared across 100 ms.
- ⚠️ The widget graph still samples on the widget tick (5 Hz by default), so **half the extra
  values are not drawn** until the per-widget Hz slider is raised. The collector half is done; the
  widget half is ticket 03 (rates plan R2/R4).

**Still unverified** for other providers: how stale an NVML reading, a SMART temperature or a
SuperIO fan register already is before Halo reads it.

---

## Published but never displayed

Registered in shared memory, costing a slot and a write, with no panel reading them today:

| Metric(s) | Count | Why it exists |
|---|---|---|
| `proc.top*` ranks 5–9 and the unused ranking | 90 | Both rankings (per instance, same-name aggregated) are published at 10 ranks each so a widget's row count and aggregation toggle are instant and cost the collector nothing. A default `topN = 5` on one ranking leaves 90 unread. |
| `cpu.core.<i>.class` / `.physical` | 40 | New in ticket 02. The ticket-03 core grid is their first consumer; until then they are static, cost one write each at discovery, and are exactly what a third-party widget needs to lay out a hybrid CPU. |
| `fan.<n>.name` | per channel | LHM's own sensor names. The FANS panel shows per-channel nicknames instead. Genuinely useful in `--dump` for working out which channel is which header. |
| `cpu.name` | 1 (conditional) | Only used when a widget has no `title` option. |
| `gpu.<i>.vendor` | per GPU | Written for the Settings GPU picker and the System check; no panel row shows it. |
| `sys.*` | 5 | The System check page (ticket 04) is their consumer. |

**Removed in v2** (were published and never read): `drive.<x>.activity.pct`, `fps.app.pid`,
`fan.<n>.pct`.

---

## Changes since the 2026-09-12 audit

Ticket 01 (contract) and ticket 02 (collector) between them changed every table above.

| Was | Now |
|---|---|
| `Local\Halo.Metrics.v1`, 221 metrics | **`Local\Halo.Metrics.v2`, 286** on this machine unelevated; an elevated run adds the fan, drive-temp and PCL families on top (count not measured — no elevated run was possible here) |
| `gpu.temp.c` etc., one GPU, index 0 only | **`gpu.<i>.*` for every GPU** — NVML ordered by PCI bus id, then LHM's AMD/Intel cards; `gpu.count`, `gpu.<i>.name`, `gpu.<i>.vendor` |
| No CPU topology | **`cpu.logical.count`, `cpu.core.<i>.class`, `cpu.core.<i>.physical`** for every logical CPU, with processor-group handling |
| Fan channels capped at 8, then 16; selection needed `fanNames` in settings.json | **No cap** (bounded only by registry capacity), numbering continuous across SuperIO chips, `fan.count` + `fan.<n>.rpm.max` + `fan.<n>.control.pct` |
| `fan.<n>.pct` computed in the collector from a configured max | **Gone.** The chip's own PWM duty is published when it exists; otherwise the widget derives rpm ÷ max (H4) |
| Volumes listed in `settings.json → driveLetters` | **Discovered and reconciled every poll**, including a LHM re-open so a hot-plugged drive gets a temperature |
| `settings.defaultRateHz` + per-provider overrides | **[`CollectorRates`](../src/Halo.Collector/CollectorRates.cs)** — code constants (R1) |
| disk-io 5 Hz · network 5 Hz · drive temps every 30 s | **10 Hz · 10 Hz · every 10 s** |
| `nominalRateHz` often the provider's *ceiling* (`MaxRateHz`) | **The metric's real cadence** — presentmon's lows say 2 Hz, static metrics say 0, `net.ip.external` says 0.0033 |
| Static metrics re-written every poll at the poll rate | **Written once at discovery, nominal 0 Hz** — `ram.total.gb`, `drive.<x>.total.b`, `drive.<x>.label`, `gpu.<i>.vram.total.mb`, `fan.<n>.name`. A `rescan` re-writes them, which is how a renamed volume refreshes |
| `rescan` logged and did nothing | **Re-initialises all 8 hardware-enumerating providers** on their own threads |
| PC latency existed only inside `LatencyPanel` | **`latency.pc.ms`** published as a Calc metric; `PanelCatalog`'s `pclat` row points at it (it pointed at `latency.render.ms`, which was wrong) |
| `presentmon` / `pclstats` reported `needsElevation = false`, `lastError = failed` | **`true` / `unelevated`** — the provider table now says why |
| `lhm-cpu` / `lhm-superio` reported `ok` unelevated while publishing nothing | **`degraded` + `unelevated`** — LHM initialises fine without admin and then reads N/A off everything; "ok" would have the System check tell the user their sensors were working |
| `effectiveRateHz` frozen at whatever nominal was registered | **Measured** per provider over a rolling window and republished scaled, so an overrunning provider is visible |
| Top-process publish count 5, with a `topProcessCount` config key nothing read | **Fixed at 10**, widgets clamp to 1–10 |
| ⚠️ All provider costs quoted as `lastPoll × rate` | **That is wall-clock thread occupancy, not CPU.** Both are reported separately; see [Provider cost measurement](#provider-cost-measurement) |
| ⚠️ disk-io and network annotated "~1 s counters, faster polling measures jitter" | **Measured wrong** — counters update per I/O and per packet batch; see [Counter-granularity measurement](#counter-granularity-measurement) |

**What this run could not verify.** It was unelevated, so `fan.<n>.*`, `drive.<x>.temp.c`,
`cpu.package.*`, `cpu.vcore.v` and the whole `pclstats` family (including the new `latency.pc.ms`)
were never exercised with real values, and `presentmon` could not own an ETW session because the
production collector already holds it. No AMD or Intel GPU and no >64-thread CPU exist on this
machine, so the `lhm-gpu` vendor fallback and the `NtQuerySystemInformationEx` per-group path are
**code-reviewed but untested**.
