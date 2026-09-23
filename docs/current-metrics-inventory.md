# Current metrics inventory

**The catalogue of what Halo collects.** The byte layout of the shared section is in a separate
document, [metrics-protocol.md](metrics-protocol.md), and is not repeated here.

The list began on 2026-07-19 as a checklist taken from a screenshot of the Rainformer desktop:
everything a replacement had to cover before HWiNFO, MSI Afterburner, RTSS and the NVIDIA App could
be retired. That checklist is complete and the old stack is gone, so this is now simply the
catalogue.

**Last regenerated from `Local\Halo.Metrics.v2` and the shipping code on 2026-09-13.** Every
**Example** below is a real value read from the section on the development machine that day with
`Halo.Collector.exe --dump --json` (IP addresses removed). Every rate is the `nominalRateHz` the
provider registered, read back from the registry rather than copied from a comment.

That run registered **286 metrics**. It was **not elevated**, so four families never registered and
are described here from the code, marked *elevated only*: `fan.<n>.*`, `drive.<x>.temp.c`,
`latency.queue/render/pc.ms` and `render.rate.hz`. An elevated run adds those as well; how many
that comes to depends on the motherboard's fan channels and was not measured.

---

## How to read these tables

**Metric** — The shared-memory metric name. `<i>`, `<n>` and `<x>` are indexes discovered at run
time (GPU; core, fan or rank; drive letter). A reader can use `gpu.count`, `cpu.logical.count` and
`fan.count`, or simply enumerate the registry. Named constants are in
[`MetricNames.cs`](../src/Halo.Metrics/MetricNames.cs), but no reader needs them — the registry
describes itself. *widget-side* means no metric exists: the widget works the value out when it
draws.

**Value semantics** — The registry's `semantics` byte, which tells a reader whether averaging or
re-sampling the number makes sense:

| Semantics | Meaning |
|---|---|
| **latest** | A single sensor or OS reading at poll time, with no averaging |
| **interval avg** | The average over the gap between two polls (Δcounter ÷ Δt) |
| **rolling window** | A sliding time window over individual samples; `windowMs` gives its length |
| **cumulative** | Since the session started, or since the `reset-net` pipe command |
| **running max** | The highest value this session, held until `reset-max` |
| **calc** | Arithmetic over other metrics, with no sensor of its own |
| **static** | Written once when the hardware is discovered, and again only if it is enumerated again |

**Nominal Hz** — How often the number actually changes, which is not always the provider's poll
rate: `fps.app.name` is refreshed once a second inside a 40 Hz poll, the 1% lows are recalculated
at 2 Hz behind a cache, and `net.ip.external` changes every few minutes. **0 means static** — there
is no cadence, so its age keeps growing by design and a refresh-rate slider should ignore it.

The registry also carries **`effectiveRateHz`**: the same number scaled by how well the provider is
actually keeping up. The host measures each provider's real poll rate over a rolling window and
republishes `nominal × measured ÷ configured` for every metric it owns, so a provider that falls
behind shows a number below its constant. On this run every provider was within **1.2%** of
nominal (`lhm-cpu` 5.06 against 5, `cpu-kernel` 10.06 against 10, `nvml` 9.95, `disk-io` 9.99,
`network` 9.98), well inside the 5% target.

**Est. latency** — The time from something physically happening to the pixel changing on screen,
given as **min–max**. It is built from the measured cost of each poll and the fixed cadences; see
[Estimated latency](#estimated-latency).

---

<a id="tail"></a>
## From publish to paint

These stages are shared by every metric.

**T1 · Publish.** The provider calls `MetricSink.Set(name, value)`
([MetricSink.cs](../src/Halo.Collector/MetricSink.cs)): a cached slot lookup, then one atomic 8-byte
store into the shared section plus a QPC timestamp, which is what age and staleness are measured
from. **It has no rate of its own** — it runs inside the poll that produced the value.

**T2 · Session maximum** (only metrics registered with `RegisterWithMax`). The same `Set` compares
the value against the running maximum and, if it is higher, makes a second atomic store into the
`<name>.max` slot. **Its rate is the base metric's rate.** The `reset-max` pipe command clears it;
the widget menu's **Reset session max** sends that command.

**T3 · Widget wake.** The `Halo.Widgets` main loop waits in `MsgWaitForMultipleObjectsEx` until the
next window is due, a window message arrives, or the collector signals `Local\Halo.FramesReady.v2`
([App.cs](../src/Halo.Widgets/App.cs)).

**T4 · Read.** `MetricCache` → `CollectorSession.TryGet` looks the name up in the registry once and
then reads the value and timestamp. A changed `collectorStartQpc` discards the whole name→index
cache, because a restarted collector rebuilds the registry and an old index would read a
*different* metric ([metrics-protocol.md, reader rule 2](metrics-protocol.md#the-five-reader-rules)).

**T5 · Paint.** Element functions run again, the flow layout runs, changed text and bars are drawn
into the Direct2D surface, and DirectComposition commits. Unchanged strings and bar lengths are
skipped, so a redraw on unchanged data costs layout work but no drawing.

---

<a id="estimated-latency"></a>
## Estimated latency — the model

Every **Est. latency** figure is `min–max`, built from three kinds of number:

- **Measured** — The cost of each poll, as a median (and 90th percentile where it matters), from
  the `status:` lines in `logs/collector-*.log` and from the provider table's `lastPollMs`.
- **Configured** — Poll periods from [`CollectorRates`](../src/Halo.Collector/CollectorRates.cs),
  flush periods and wake windows, read directly from the code.
- **Unverified** — Marked *inline* wherever a source's own update rate is not known.

**Min** assumes everything lands at the luckiest moment: the sensor updates just before a poll,
which lands just before a widget tick. **Max** assumes every stage misses by a full period. Neither
is typical; the middle of the range is.

### The two shared tails

**T — poll tail: 2–219 ms.** What every polled metric adds after the collector publishes it.

| Stage | Delay | Source |
|---|---|---|
| Waiting for the widget tick (5 Hz default) | 0–200 ms | The widget's `rateHz` in `widgets.json` |
| `MetricCache.Tick` + `Panel.Update` + layout + Direct2D + DirectComposition commit | About 2 ms | Derived: 12.5% of a core ÷ at most 62.5 redraws per second. It includes the other widgets' ticks, so it is an upper estimate |
| DWM composition → scan-out on the widget's monitor | 0–16.7 ms | A 60 Hz widget monitor |

**Tf — frame-event tail: 2–35 ms.** Replaces T for the **FPS counter only**, the one widget with a
frame graph and therefore the only one the `FramesReady` event brings forward. Its 200 ms tick wait
becomes the 16 ms wake window (`App.OnFramesReady`); the redraw and scan-out stages are the same.
With no frames arriving, the widget falls back to **T**.

### Two things the totals keep apart

**Transport latency** (the bold number) is *how old the newest sample is by the time it is on
screen*. **Averaging delay** is *how long until a change is fully reflected*: a rolling 1 s FPS
shows the newest frame within about 47 ms but takes a full second to finish moving. Only the frame
graph has neither, because it draws raw per-frame values with no window at all.

### Assumptions

- **A 60 Hz widget monitor.** The 0–16.7 ms scan-out stage changes with whatever monitor the widget
  is on.
- **The ~2 ms redraw is derived**, not timed directly: it is a process-level CPU percentage divided
  by a redraw count.
- **ETW delivery and event parsing are assumed to take under a millisecond.** This is in-memory
  work with no queue, but nothing measures it on its own.
- **A source's own delay is often unknown** — how old an NVML reading, a SMART temperature or a
  SuperIO fan register already is before Halo reads it. Where the repository records a figure it is
  included; where it does not, the row says so.
- **Idle mode is excluded.** With no 3D app running, frame capture relaxes its flush to 100 ms and
  mutes the tap, which makes the frame lanes much slower until a new frame wakes them (about
  150 ms).

### The spread, worst case

Four of these rows changed when the collector rates were raised on 2026-09-13; the earlier figure
is shown in brackets.

| Rank | Metric group | Max | Why |
|---|---|---|---|
| 1 | `net.ip.external` | **~300 s** | 5-minute refresh; off by default |
| 2 | `drive.<x>.temp.c` | **~10.3 s** *(was ~30.5 s)* | 10 s SMART poll + a 258 ms read |
| 3 | `dlss.*` | **~10.2 s** | 10 s NGX module scan |
| 4 | `gpu.<i>.voltage.v` (LibreHardwareMonitor) | **~1.3 s** | 1 Hz poll + a 78 ms NVAPI sweep |
| 5 | `proc.*` | **~1.22 s** | 1 Hz snapshot |
| 6 | `ram.used.gb`, `drive.<x>.used.b`, IPs, uptime | **~1.22 s** | 1 Hz builtin poll |
| 7 | `fan.<n>.*`, `cpu.vcore.v` | **~1.22 s** | 1 Hz SuperIO poll |
| 8 | `cpu.package.*`, `cpu.clock.mhz` | **~445 ms** | 5 Hz poll + a 26 ms MSR sweep |
| 9 | `cpu.total.pct`, `cpu.core.<i>.pct` | **~235 ms** *(was ~435 ms)* | 10 Hz poll + the 15.6 ms kernel tick |
| 10 | `gpu.<i>.*` (NVML), `net.*.bps`, `drive.<x>.*.bps` | **~219 ms** *(was ~419 ms)* | 10 Hz poll, a read under 1 ms |
| 11 | `fps.displayed` and related metrics | **~87 ms** | 40 Hz poll + the wait for the flip |
| 12 | `fps.presented` and related metrics | **~47 ms** | Sent per present, 10 ms ETW flush |
| — | `cpu.core.<i>.class`, `gpu.count`, `sys.*`, `drive.<x>.total.b` | **n/a** | Static; written at discovery and correct from then on |

**Roughly half of every number above comes from the widget side.** `T` alone is 2–219 ms and is the
same for every polled metric, so nothing on a dashboard widget beats about 219 ms in the worst case,
however fast its provider runs. That is also why the collector rates stop at 10 Hz: beyond that,
the collector is no longer the slow part. Only the FPS counter avoids it, through `Tf`.

---

## What each panel shows

One section per panel type in [`PanelCatalog`](../src/Halo.Shared/Panels/PanelCatalog.cs), the
single declaration both the renderer and the Settings app read, so this list cannot drift from the
code without the build noticing. `{gpu}`, `{n}`, `{x}`, `{stream}` and `{agg}` are filled in per
widget from its options.

| Panel | Metric rows | Repeats | Needs |
|---|---|---|---|
| **clock** | `sys.uptime.s` + widget-side date, time and weekday | — | Nothing |
| **cpu-ram** | `cpu.package.temp.c`, `cpu.total.pct`, `cpu.core.{n}.pct`, `cpu.clock.mhz`, `fan.{n}.rpm`, `ram.pct` | Per logical CPU | PawnIO for temperature, clock and fan |
| **gpu** | `gpu.{gpu}.temp.c / usage.pct / vram.pct / fan.pct / clock.core.mhz / clock.mem.mhz` | One widget per GPU | NVIDIA driver for the full set |
| **fps** | `fps.app.name`, `fps.{stream}`, `fps.low1.{stream}`, `fps.low01.{stream}`, `fps.frametime.{stream}.ms`, `…worst.ms`, `dlss.version` | — | PresentMon (elevated) |
| **latency** | `latency.pc.ms`, `latency.queue.ms`, `latency.render.ms`, `fps.displaylatency.ms`, `latency.click.ms`, `latency.allinput.ms`, `dlss.version`, `dlss.model`, `render.rate.hz` | — | Reflex (PCL Stats) markers |
| **power** | `cpu.vcore.v`, `gpu.{gpu}.voltage.v`, `cpu.package.power.w`, `gpu.{gpu}.power.w` (+ `.max`) | — | PawnIO for Vcore and package power |
| **drives** | `drive.{x}.label / temp.c / used.b / total.b / write.bps / read.bps` | Per volume | PawnIO and SMART for temperatures |
| **network** | `net.ip.external`, `net.ip.internal`, `net.down.bps`, `net.up.bps`, `net.down.bps.max`, `net.down.total.b` | — | Nothing |
| **fans** | `fan.{n}.rpm` | Per fan channel | PawnIO + a SuperIO chip LibreHardwareMonitor supports |
| **topcpu** | `proc.topcpu.{agg}{n}.name / .cpu.pct / .ram.b`, `proc.count` | Per rank (1–10) | Nothing |
| **topram** | `proc.topram.{agg}{n}.name / .ram.b / .cpu.pct` | Per rank (1–10) | Nothing |

Three things the panels work out themselves instead of reading:

- **Fan percentage.** The collector publishes `fan.<n>.control.pct` when the SuperIO chip reports a
  PWM duty cycle for that channel, and nothing otherwise. On a board that does not expose one, the
  widget works out `rpm ÷ max`, where the maximum is a per-channel setting that defaults to the
  highest speed seen this session (at least 1500 rpm). The collector has no board-specific table
  of maximum speeds, and is not meant to.
- **Frame generation multiplier.** `fps.displayed ÷ render.rate.hz`, falling back to `fps.fgratio`.
- **Everything on the clock panel except uptime** — `DateTime.Now`, read once per widget tick.

---

## The registry, as published

Grouped by the provider that owns each metric. `n` is how many instances exist on the development
machine (8 volumes, 20 logical CPUs, 10 ranks × 2 rankings). Read directly from the section; nothing
in these tables is written by hand.

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

`latency.pc.ms` is queue + render + display, added up by the collector so a third-party tool gets
the same headline figure the Latency widget shows. Render is required (no marker-tagged frame means
no PC latency); the other two are added when their provider has them, and each is only used if it
is under 3 s old, so a frozen part cannot keep inflating the total
([PclStatsProvider.cs](../src/Halo.Collector/Providers/PclStatsProvider.cs)).

### Changed since the 2026-09-13 dump

The tables above are a transcript of one real run, so they are left exactly as measured. Three
things in the `presentmon` block changed on 2026-09-14 and will look different the next time the
tables are regenerated:

| Metric | Then | Now | Why |
|---|---|---|---|
| `fps.app.pid` | Did not exist | 1 · — · latest · 40 | The process ID of the tracked foreground app, 0 when there is none. `pclstats` reads it to limit its Reflex markers to one process, and the frame graphs use it to reset when the game changes, so one game's bars never carry into the next. |
| `latency.click.ms` | latest | rolling window (20 s) | It used to be a session average whose timestamp was refreshed on every poll, so a click from twenty minutes earlier read as current. It is now a real 20 s window, and N/A when the window is empty. |
| `latency.allinput.ms` | latest | rolling window (20 s) | Same as above. |

`fps.presented` and `fps.displayed` did not change semantics, but their **values** did. The rate
used to be the frame count divided by the time from the oldest to the newest frame in the window,
and k frames only span k−1 gaps, so every reading was exactly **1 fps** too high (a locked 60 fps
showed 61 next to a FRAMETIME row of 16.7 ms). The rate now comes from the frame intervals
themselves. The 1% and 0.1% lows were not affected, because they already worked from frametimes.

---

<a id="source-providers"></a>
## Source providers

Every provider runs on **its own thread** at `min(DefaultRateHz, MaxRateHz)`. Initialisation is
retried after 1, 5, 30 and 60 s, and ten poll failures in a row force a fresh initialisation. One
provider failing never affects another ([ProviderHost.cs](../src/Halo.Collector/ProviderHost.cs)).

**Rates are code constants, not settings.** They all live in
[`CollectorRates.cs`](../src/Halo.Collector/CollectorRates.cs); the old `settings.defaultRateHz`
and per-provider overrides are gone. The host works out each period once at start-up, so a rate
changed in the settings would not have taken effect anyway. What *can* be set under
`settings.json → collector` is `frameLowsWindowS`, `presentMonEtwFlushMs`, `presentMonTransport`,
`presentedTap`, `networkInterface` and `externalIp{enabled,url,refreshMinutes}`.

**Every provider that enumerates hardware in `Initialize` runs it again on the `rescan` control
command** (`RescanReinitialises`): builtin, cpu-kernel, disk-io, nvml, and the SuperIO, storage
and GPU parts of LibreHardwareMonitor — seven in all. System check's **Rescan hardware** button
sends it. Three kinds of provider are left out:

- **presentmon, pclstats and network** own an ETW session or a counter baseline, and tearing those
  down to look for a new fan would lose frame data or reset the session totals.
- **`lhm-cpu`** has nothing to find: four fixed metrics, no indexed family, and a CPU that cannot be
  added while the machine runs. Opening it again would cost time and gain nothing.
- A second `rescan` **within 10 seconds is ignored**, so holding the button down cannot cause a
  burst of re-opens across the other three LibreHardwareMonitor parts.

| Name | Where it comes from | OS support | Hardware support | Poll or push | Rate · ceiling · worth raising? |
|---|---|---|---|---|---|
| <a id="p-builtin"></a>**builtin** | **Part of Windows** — `kernel32` (`GlobalMemoryStatusEx`, `GetDiskFreeSpaceExW`, `GetVolumeInformationW`) plus .NET sockets. The external IP lookup is the one exception: an HTTPS request to a **third-party service** (`api.ipify.org`), **off by default**. | Windows 10/11 x64. No elevation. | **Any hardware** — there is no hardware dependency at all. Volumes are discovered (`DriveType.Fixed \| Removable`, ready, not network or optical) and checked again every poll, so a newly connected drive appears without a restart. | Poll | **1 Hz** · ceiling **4 Hz** · **Not worth it** — uptime advances 1 s per second, and RAM and free space do not change meaningfully within a second. Capacity and label are written once at discovery rather than every poll, which saves one `GetVolumeInformationW` per volume per second (8 per second on the development machine). |
| <a id="p-cpu-kernel"></a>**cpu-kernel** | **Part of Windows** — `ntdll!NtQuerySystemInformation` class 8, plus `GetLogicalProcessorInformationEx` once per initialisation for the topology. | **Windows 10/11 x64.** No elevation. | **Any vendor and core count.** The P/E class and physical core are read for each logical CPU; above 64 logical CPUs it switches to `NtQuerySystemInformationEx` per processor group. ⚠️ **The multi-group path has not been tested on real hardware** — the development machine is one group of 20. | Poll | **10 Hz** · ceiling **64 Hz** · **Still the most headroom in the collector.** The counters advance on the ~15.6 ms kernel tick, so every step towards 64 Hz is genuinely new data, at about 0.005 ms of CPU per poll. It stops at 10 Hz because the widget side (2–219 ms) dominates beyond that. |
| <a id="p-process"></a>**process** | **Part of Windows** — `ntdll!NtQuerySystemInformation` class 5. | **Windows 10/11 x64 only** — the 64-bit `SYSTEM_PROCESS_INFORMATION` field offsets are fixed in the code ([ProcessProvider.cs](../src/Halo.Collector/Providers/ProcessProvider.cs)). A layout change in a future Windows would break it without an error, which is why System check reports a failed process walk. No elevation. | **Any hardware.** | Poll | **1 Hz** · ceiling **2 Hz** · **Marginal.** The longest poll in the collector (~6.4 ms: a list of about 400 processes and four sorts). Both rankings — per process and combined by name — are always published at 10 ranks each, so a widget's row count and **Sum same-name processes** option cost the collector nothing. |
| <a id="p-disk-io"></a>**disk-io** | **Part of Windows** — `pdh.dll`, the LogicalDisk counter set. | **Windows 2000 and later**; works in any display language through `PdhAddEnglishCounterW`. No elevation. | **Any hardware** — any volume Windows exposes as a LogicalDisk. Rebuilds the PDH query when the set of volumes changes. | Poll | **10 Hz** *(was 5)* · ceiling **64 Hz** · Almost all CPU (102% of wall time). **Useful and cheap.** The raw counters update on every completed I/O ([measured](#counter-granularity-measurement)), so the poll rate sets the resolution: at 5 Hz a 50 ms burst was spread across 200 ms and reported about 4× too low. The increase cost **+0.255 points of one core**. |
| <a id="p-network"></a>**network** | **Part of Windows** — .NET `System.Net.NetworkInformation` over the IP Helper API. | **Windows 10/11.** No elevation. | **Any hardware** — any adapter that is up and is not loopback or a tunnel. `collector.networkInterface` picks one by name, and `"Best"` picks the one with the default route. | Poll | **10 Hz** *(was 5)* · ceiling **64 Hz** · **Useful and cheap**, like disk-io — the byte counters update for every batch of packets ([measured](#counter-granularity-measurement)). The increase cost **+0.037 points**. |
| <a id="p-nvml"></a>**nvml** | **Installed with the NVIDIA display driver** (`nvml.dll`), not bundled by Halo. Loaded by name through P/Invoke; without the driver the provider is unavailable with `no-nvml`. | **Windows with an NVIDIA driver.** No elevation. | **Every NVIDIA GPU**, ordered by PCI bus ID so each index stays the same across restarts ([GpuIndexSpace.cs](../src/Halo.Collector/Providers/GpuIndexSpace.cs)). `nvmlDeviceGetFanSpeedRPM` exists only in newer drivers and is checked at initialisation. The power sanity limit comes from each card's own limit, so it works for any model. AMD and Intel cards are handled by `lhm-gpu`. | Poll | **10 Hz** · ceiling **20 Hz** · **Partly worth it.** Temperature, clocks and power are read on request and do get fresher; `gpu.<i>.usage.pct` does not, because NVML calculates it over its own 1 s window. The registry says so (`semantics = rolling window`, `windowMs = 1000`), and the widget's "?" help repeats it. The ceiling is 20 Hz because one poll makes 8 calls at about 0.2–1 ms each. |
| <a id="p-lhm-cpu"></a>**lhm-cpu** | **NuGet package** — `LibreHardwareMonitorLib` **0.9.6** (MPL-2.0), restored into the project-local cache `tools\nuget-cache`. Needs a **kernel driver** (PawnIO) for MSR access. PawnIO is shared with FanControl, HWiNFO and LibreHardwareMonitor itself, so Halo never uninstalls it. | **Windows 10/11 x64. Requires elevation** — unelevated, `Initialize` finds no hardware, the provider table shows `unelevated`, and every CPU metric stays N/A. | **Intel and AMD.** The temperature match accepts `CPU Package` (Intel) or `Core (Tctl/Tdie)` (AMD); the clock is the highest core clock, whatever the sensors are called. Tested on an **Intel Core i5-14600K**. | Poll | **5 Hz** · ceiling **20 Hz** · **Not worth raising — because of how long a poll takes, not its CPU cost.** Only **1.1% of its wall time is CPU** (2.4 ms of a 216 ms poll); the rest is spent waiting on the driver. But wall time is what limits the period: at 10 Hz, **2.78% of polls ran past their 100 ms period**. ⚠️ Its wall time **depends on what the machine is doing** — 216 ms per poll on an idle machine against 24–31 ms in normal use. |
| <a id="p-lhm-superio"></a>**lhm-superio** | The same **LibreHardwareMonitorLib 0.9.6**; talks to the SuperIO chip over **ISA port I/O**, behind a global mutex that FanControl also uses. | **Windows 10/11 x64. Requires elevation.** | Any SuperIO chip LibreHardwareMonitor has a driver for — on the development machine, an **MSI NCT6687D**. **Every fan channel is published**, with no limit and no name matching, numbered continuously across several SuperIO chips in LibreHardwareMonitor's order. Channel numbers are positions in that list, **not** motherboard header numbers, which is why the Fans widget lets each channel be renamed. | Poll | **1 Hz** · ceiling **2 Hz** · **Cheap, but the gain is unknown** — a fan tachometer needs at least one revolution (about 30–60 ms at 1000–2000 rpm), and how often the NCT6687D updates its own registers has never been measured. The real cost is not CPU but the global ISA mutex shared with FanControl. |
| <a id="p-lhm-storage"></a>**lhm-storage** | The same **LibreHardwareMonitorLib 0.9.6**; SMART and NVMe identify IOCTLs. | **Windows 10/11 x64. Requires elevation.** | Any SATA or NVMe drive that reports a temperature **and** a non-empty vendor or product name. A drive with a blank name cannot be matched to a drive letter; it logs one warning and shows N/A. The LibreHardwareMonitor `Computer` is opened again when the set of volumes changes, so a newly connected drive gets a temperature. | Poll | **Every 10 s** *(was 30 s)* · ceiling **every 5 s** · Only **17% of the poll is CPU** (43.6 ms of 255 ms). **It was polled too rarely.** 30 s had been chosen on cost (32 ms per drive × 8), not on how fast a drive heats up, and an NVMe drive can reach its throttling point in well under 30 s. The increase cost **+0.233 points**. |
| <a id="p-lhm-gpu"></a>**lhm-gpu** | The same **LibreHardwareMonitorLib 0.9.6** → **NVAPI / ADL**. Covers what NVML cannot (NVML has no public API for GPU voltage) and is the *only* source for non-NVIDIA cards. | **Windows 10/11. Works unelevated**, unlike the other three LibreHardwareMonitor parts. | **NVIDIA, AMD and Intel.** An NVIDIA card is matched to its NVML index by name and adds only voltage and fan rpm. An AMD or Intel card takes its own index and publishes the whole `gpu.<i>.*` family from `SensorType.Temperature/Load/SmallData/Fan/Clock/Power/Control/Voltage`. Sensors a card does not report are never registered, so the widget hides that row instead of showing a permanent N/A. ⚠️ **This path has not been tested on a real AMD or Intel GPU.** | Poll | **1 Hz** · ceiling **2 Hz** · **Still the poorest value on an NVIDIA-only machine.** A ~78 ms NVAPI poll (29% of it CPU) produces two metrics. On an AMD or Intel machine the same poll is the entire GPU widget, which is why it stays at 1 Hz rather than going lower. |
| <a id="p-presentmon"></a>**presentmon** (resolved lane) | **Bundled** — the Intel **PresentMon 2** service and `PresentMonAPI2.dll` under `presentmon\` (MIT), started as a **console-mode child process** and never registered as a Windows service. | **Windows 10 1709 and later** (ETW present tracking). **Elevation is needed to own the ETW session.** Connecting to an already-installed, running PresentMon service works unelevated, which is why `needsElevation` is a flag rather than a hard requirement. | **Any GPU vendor and graphics API** — it watches the Windows display kernel's present pipeline, so DirectX 9/11/12, Vulkan and OpenGL are all covered. That is why it is the fallback lane for games the tap cannot see. | Poll (drain) | **40 Hz** · ceiling **120 Hz** · **No gain — it collects frames that are already timestamped, rather than sampling.** Frames carry their own `PRESENT_START_QPC`, so collecting faster only splits the same frames into smaller batches. What changes the latency is `presentMonEtwFlushMs` (10 ms). The lows are recalculated at 2 Hz behind their own cache, and the registry says 2 Hz for those four metrics rather than the provider's ceiling. |
| <a id="p-tap"></a>**present-tap** (tap lane) | **Nothing to download** — the Windows **built-in ETW providers** `Microsoft-Windows-DXGI` and `Microsoft-Windows-Direct3D9`, read through `Microsoft.Diagnostics.Tracing.TraceEvent` **3.2.5**. Owned by the presentmon provider rather than being a separate `ProviderHost` entry. | **Windows 10/11. Requires elevation** (it owns a real-time ETW session). Skipped without a message when unelevated — the presented lane then uses the resolved lane. | **Any GPU vendor, but only some APIs**: DXGI (Direct3D 10/11/12) and Direct3D 9(Ex). **Vulkan and OpenGL games emit no present event here** and use the resolved lane instead (`fps.tap.active = 0`). | **Push** (per present) | **Push — already one event per present**, so there is no rate to raise. The only setting is `presentMonEtwFlushMs` (10 ms, limited to 1–100). Lowering it shortens how long events wait in the buffer, but adds a kernel buffer flush each time. It relaxes to 250 ms when idle. |
| <a id="p-pclstats"></a>**pclstats** | **Nothing to download** — the markers come from the **game's own NVIDIA Reflex SDK**. Halo only enables `PCLStatsTraceLoggingProvider` (GUID `0d216f06-…`, taken exactly from NVIDIA's reference `pclstats.h`, MIT) and reads it through TraceEvent. | **Windows 10/11. Requires elevation.** Reports `unelevated`, and the host retries with increasing delays. | **Only games that include Reflex.** Nothing about it depends on the GPU — it reads the game's own instrumentation, not hardware — but in practice Reflex ships with NVIDIA-supported games. With no markers, the four metrics stay stale and the widget dims. | **Push** (ETW markers) → published periodically | **5 Hz** · ceiling **20 Hz** · **Free, but pointless to raise.** The poll makes no system call at all, but the markers it averages arrive only 5–10 times a second and the values are 1.5 s rolling averages, so publishing faster just repeats the same number. |
| <a id="p-max"></a>**`.max` session maxima** | **Internal** — the running maximum inside `MetricSink`, plus the `Halo.Control.v2` named pipe (`reset-max`). | Any; pure arithmetic. | Not applicable. | Calc (inside the base metric's `Set`) | **No rate of its own** — the comparison runs inside the base metric's `Set`, so it is exactly as fast as whatever it tracks and costs one well-predicted branch. |
| <a id="p-widget"></a>**widget-side** | **Internal** — `Halo.Widgets`: the system clock, plus arithmetic over metrics already in shared memory. | Windows 10/11 x64 (DirectComposition + `WS_EX_NOREDIRECTIONBITMAP`). | Not applicable. | Pull (per tick) / event (frame graphs) | **Text at 5 Hz** by default, adjustable per widget from 0.5 Hz up to the fastest metric it shows (at most 10 Hz). **This is the more expensive half.** Raising a widget's refresh rate is the only way extra collector resolution reaches the screen, because sampled graphs draw one point per tick. Frame graphs are different: they are woken by frame events, at most once every 16 ms (`App.OnFramesReady`). |

---

<a id="provider-cost-measurement"></a>
## Provider cost measurement (2026-09-13)

**Why this section exists.** Every cost figure here used to be `median lastPoll × rate`, and
`lastPoll` is a `Stopwatch` reading — that is, **wall time**. For providers whose polls mostly
*wait* on hardware, that greatly overstates CPU use. It has now been measured properly, and the two
are reported separately.

**Method.** A standalone test program repeated each provider's exact work per poll — the same
`NtQuerySystemInformation` class, the same PDH counters, the same NVML calls, the same
LibreHardwareMonitor `Computer` parts and `Update()` sweep — and ran each at its old rate and then
at the proposed one. CPU time was taken with `QueryThreadCycleTime` (calibrated at 3.46 million
cycles per CPU millisecond, which gives microsecond resolution instead of the 15.6 ms
`GetThreadTimes` tick), and I/O with `GetProcessIoCounters`. **Halo was fully stopped** so nothing
competed with it.

### Per poll

| Provider | Wall ms | **CPU ms** | **CPU as % of wall** | I/O operations | I/O bytes |
|---|---|---|---|---|---|
| `cpu-kernel` | 0.004 | 0.005 | — *(4 µs, below the resolution)* | 0 | 0 |
| `network` | 0.203 | 0.117 | 58% | 3 | 264 |
| `disk-io` | 0.199 | 0.202 | 102% | 1 | 6,616 |
| `nvml` | 0.350 | 0.316 | 90% | 0 | 0 |
| `lhm-gpu` | 77.7 | 22.2 | **29%** | 4 | 0 |
| `lhm-storage` | 255.0 | 43.6 | **17%** | 1,194 | 51,668 |
| `lhm-cpu` | 216.6 | 2.4 | **1.1%** | 48 | 384 |

### What the rate increases cost

Of the seven increases that were costed, **five were adopted** and two (`lhm-cpu` 5 → 10 Hz and
`lhm-gpu` 1 → 2 Hz) were rejected — on their own they were 81% of the predicted cost.

| Provider | Change | Adopted? | **Predicted CPU change** (points of one core) | I/O operations change |
|---|---|---|---|---|
| `cpu-kernel` | 5 → 10 Hz | ✅ | +0.001 | 0 |
| `network` | 5 → 10 Hz | ✅ | +0.037 | +15/s |
| `lhm-storage` | 30 s → 10 s | ✅ | +0.233 | +80/s |
| `disk-io` | 5 → 10 Hz | ✅ | +0.255 | +5/s |
| `nvml` | 5 → 10 Hz | ✅ | +0.283 | 0 |
| **The five adopted** | | | **+0.809 points of one core** = **+0.04% of the 20-thread development machine** | +100/s |
| `lhm-gpu` | 1 → 2 Hz | ❌ Rejected | +1.652 | +4/s |
| `lhm-cpu` | 5 → 10 Hz | ❌ Rejected (polls run past their period) | +1.759 | +240/s |

The whole-process check of that prediction is in
[perf-usage-breakdown.md § Collector rates, 2026-09-13](perf-usage-breakdown.md#collector-rates-2026-09-13).
The collector runs at **3.87% of one core** unelevated and idle at the new rates, and the increases
are **smaller than that method can detect** (a spread of ±0.46 against an expected +0.29 on an
unelevated run), so they agree with the prediction without confirming it. What *is* confirmed there
is the per-poll model itself: applying these CPU-to-wall ratios to the live provider table predicts
4.06%, against a measured 3.87%.

### What this changes

- **The LibreHardwareMonitor parts are not expensive in CPU, but they occupy their threads for a
  long time.** `lhm-cpu` spends 1.1% of its poll computing. The old "13.21% of a core" was thread
  occupancy; the real CPU use is 1.22%.
- **Occupancy still matters**, because it is what limits a provider's period and makes polls run
  late. The case against `lhm-cpu` at 10 Hz stands, just for the right reason.
- ⚠️ **`lhm-cpu`'s wall time depends on what the machine is doing.** It took 216 ms per poll here
  (an idle machine, with Halo stopped), against 24–31 ms inside the running collector, with CPU time
  about 2.4 ms in both. The likely cause — **not verified** — is cores waking up: LibreHardwareMonitor
  reads per-core MSRs by moving its thread across all 20 logical CPUs, and on an idle machine those
  cores are in deep sleep states. If that is right, polls are *more* likely to run late when the
  machine is idle, which would also explain the 2.78% of collector polls over 100 ms.

### Drives: SMART polling does not wake them

The development machine has **three spinning hard drives** alongside four SSDs. Windows turns hard
drives off after **20 minutes on mains power** (`DISKIDLE` = 0x4b0) and 10 minutes on battery.

All 11 measured polls finished in **218–277 ms**, at both 30 s and 10 s spacing, with no
multi-second outliers. A 3.5" hard drive takes 5–10 s to spin up, so **no drive is being woken by
the poll** at either interval. The cost per poll was also the same at both spacings, so nothing is
being cached or woken differently. This is what made the move to 10 s safe.

**Open question:** whether the SMART request resets Windows' disk idle timer. If it does, a 10 s
poll against a 20-minute timeout means those hard drives never spin down. This has not been
checked; comparing `StartStopCycleCount` with `PowerOnHours` (elevated) would answer it. No polling
rate within the 0.2 Hz ceiling would change this either way.

---

<a id="counter-granularity-measurement"></a>
## Counter-granularity measurement (2026-09-13)

Two provider comments claimed their source counters updated too slowly for the poll rate to
matter. Both were assumptions that had never been measured, and **both were wrong** — which is what
justified raising the disk-io and network rates. The results are recorded here so they do not need
to be worked out again.

**Method.** The *raw* running total (not the rate PDH calculates) was read every 100 ms during
sustained load, and again while idle.

**`\LogicalDisk(C:)\Disk Write Bytes/sec`** — continuous 4 MB writes:

| Sample | Raw total | Change |
|---|---|---|
| 1 | 91,711,232,512 | 260,235,264 |
| 2 | 91,923,159,552 | 211,927,040 |
| 3 | 92,156,107,264 | 232,947,712 |
| … | *every 100 ms sample moved* | 210–260 MB |
| 12 | 93,936,171,520 | 12,607,488 ← writes stop |
| 14 | 93,936,187,904 | **16,384** ← a single background write while otherwise idle |

**`GetIPStatistics().BytesReceived`** (the IP Helper value `NetworkProvider` actually reads, not
PDH) — a continuous download on the Ethernet adapter:

| Sample | Raw total | Change |
|---|---|---|
| 1 | 830,048,948 | 7,053,652 |
| 2 | 836,271,320 | 6,222,372 |
| … | *every 100 ms sample moved* | 3–7 MB |
| 11 | 879,196,144 | **90** ← background traffic still visible at 100 ms once idle |

**Conclusion.** Both counters update on every completed I/O or batch of packets. Neither updates
only once a second. Therefore:

- Each published `drive.<x>.read/write.bps` and `net.down/up.bps` is a **real average over one poll
  period**, not an old total divided again.
- **The poll rate sets the resolution.** At the old 5 Hz, a 50 ms burst was spread across 200 ms and
  reported about 4× too low; at 10 Hz it is spread across 100 ms.
- ⚠️ Widget graphs sample on the widget tick (5 Hz by default), so **half of the extra values are
  not drawn** unless that widget's refresh rate is raised in Settings.

**Still not verified** for other providers: how old an NVML reading, a SMART temperature or a
SuperIO fan register already is before Halo reads it.

---

## Published but never displayed

These are registered in shared memory, each costing a slot and a write, but no widget row shows
them:

| Metric(s) | Count | Why it exists |
|---|---|---|
| `proc.top*` ranks beyond the widget's row count, and the ranking not in use | Up to 90 | Both rankings (per process, and combined by name) are published at 10 ranks each, so a widget's row count and its **Sum same-name processes** option change instantly and cost the collector nothing. The default of 5 rows on one ranking leaves 90 unread. |
| `cpu.name` | 1 (conditional) | Only used as the CPU / RAM title when a widget has no title of its own. |
| `gpu.<i>.vendor` | Per GPU | Used by the Settings GPU picker and System check; no widget row shows it. |
| `sys.*` except uptime | 5 | Read by System check (elevation, PawnIO, Windows build, collector version). |

`fan.<n>.name` is shown only as the default label on the Fans widget when a channel has not been
renamed, and is also useful in `--dump` for working out which channel is which header.
`cpu.core.<i>.class` and `.physical` are used by the CPU / RAM widget's per-core grid.

**Removed in v2**, because they were published and never read: `drive.<x>.activity.pct` and
`fan.<n>.pct`. (`fps.app.pid` was removed at the same time and later added back with a real use —
see [Changed since the 2026-09-13 dump](#changed-since-the-2026-09-13-dump).)

---

## Changes from the v1 section (2026-09-12 audit)

The move to the v2 contract and collector changed every table above.

| Before | Now |
|---|---|
| `Local\Halo.Metrics.v1`, 221 metrics | **`Local\Halo.Metrics.v2`, 286** on the development machine unelevated; an elevated run adds the fan, drive temperature and PCL families (not counted — no elevated run was possible then) |
| `gpu.temp.c` and so on, for one GPU at index 0 only | **`gpu.<i>.*` for every GPU** — NVML ordered by PCI bus ID, then LibreHardwareMonitor's AMD and Intel cards; `gpu.count`, `gpu.<i>.name`, `gpu.<i>.vendor` |
| No CPU topology | **`cpu.logical.count`, `cpu.core.<i>.class` and `cpu.core.<i>.physical`** for every logical CPU, with processor-group support |
| Fan channels limited to 8, then 16; choosing them needed `fanNames` in settings.json | **No limit** (other than the registry's capacity), numbered continuously across SuperIO chips, with `fan.count`, `fan.<n>.rpm.max` and `fan.<n>.control.pct` |
| `fan.<n>.pct` calculated in the collector from a configured maximum | **Removed.** The chip's own PWM duty is published when it exists; otherwise the widget works out rpm ÷ max |
| Volumes listed in `settings.json → driveLetters` | **Discovered and checked every poll**, including opening LibreHardwareMonitor again so a newly connected drive gets a temperature |
| `settings.defaultRateHz` plus per-provider overrides | **[`CollectorRates`](../src/Halo.Collector/CollectorRates.cs)** — code constants |
| disk-io 5 Hz · network 5 Hz · drive temperatures every 30 s | **10 Hz · 10 Hz · every 10 s** |
| `nominalRateHz` often the provider's *ceiling* (`MaxRateHz`) | **The metric's real cadence** — presentmon's lows say 2 Hz, static metrics say 0, `net.ip.external` says 0.0033 |
| Static metrics written again on every poll | **Written once at discovery, nominal 0 Hz** — `ram.total.gb`, `drive.<x>.total.b`, `drive.<x>.label`, `gpu.<i>.vram.total.mb`, `fan.<n>.name`. A `rescan` writes them again, which is how a renamed volume updates |
| `rescan` was logged and did nothing | **Initialises all 7 hardware-enumerating providers again**, each on its own thread |
| PC latency existed only inside the Latency widget | **`latency.pc.ms`** published as a calc metric; the catalogue's `pclat` row points at it (it used to point at `latency.render.ms`, which was wrong) |
| `presentmon` and `pclstats` reported `needsElevation = false`, `lastError = failed` | **`true` / `unelevated`** — the provider table now says why |
| `lhm-cpu` and `lhm-superio` reported `ok` unelevated while publishing nothing | **`degraded` + `unelevated`** — LibreHardwareMonitor initialises without administrator rights and then reads N/A for everything, and "ok" would have made System check tell the user their sensors were working |
| `effectiveRateHz` fixed at whatever nominal rate was registered | **Measured** per provider over a rolling window and republished scaled, so a provider that falls behind is visible |
| Top-process count 5, with a `topProcessCount` setting nothing read | **Fixed at 10**; widgets show 1–10 |
| ⚠️ All provider costs given as `lastPoll × rate` | **That is how long the thread is busy, not CPU time.** Both are now reported separately; see [Provider cost measurement](#provider-cost-measurement) |
| ⚠️ disk-io and network described as "~1 s counters, faster polling measures jitter" | **Measured and wrong** — the counters update per I/O and per batch of packets; see [Counter-granularity measurement](#counter-granularity-measurement) |

**What that run could not verify.** It was unelevated, so `fan.<n>.*`, `drive.<x>.temp.c`,
`cpu.package.*`, `cpu.vcore.v` and the whole `pclstats` family (including `latency.pc.ms`) never
produced real values, and `presentmon` could not own an ETW session because the production
collector already held it. The development machine has no AMD or Intel GPU and no CPU with more
than 64 threads, so the `lhm-gpu` fallback for other vendors and the `NtQuerySystemInformationEx`
per-group path are **reviewed in code but untested**.
