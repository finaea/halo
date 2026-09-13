# Current Metrics Inventory

**Originally compiled** 2026-07-19 from a live screenshot of the Rainformer desktop — the checklist
any replacement stack had to cover before HWiNFO + MSI Afterburner + RTSS + NVIDIA App could be
retired.

**Re-audited against the shipping code and a live `Halo.Collector.exe --dump`: 2026-09-12.**
Every **Example** below is a real value read out of `Local\Halo.Metrics.v1` on this machine on that
date (public/LAN IPs redacted). Every rate is traced to the line that sets it. **221 metrics** are
registered in shared memory; **159 are on screen** in the current widget layout. Of the 62
that are not: **17 are never read by any code** (see [Published but never displayed](#published-but-never-displayed))
and **45 are hidden by configuration** — 10 unused fan channels, 30 from the process ranking each
Top widget does not pick, and 5 from the disabled `fps-displayed` panel.

---

## How to read these tables

**Metric** — the shared-memory metric name (`Halo.Shared\Metrics\MetricNames.cs`). `widget-side`
means no metric exists: the widget computes or reads it at paint time.

**Value semantics:**
- **latest** — instantaneous sensor/OS read at poll time, no aggregation
- **interval avg** — mean over the gap between two polls (Δcounter ÷ Δt)
- **rolling Ns** — sliding time window over per-frame/per-sample data, recomputed continuously
- **decaying avg** — running sum/count halved when the count passes 200 (≈ rolling over the last few hundred samples)
- **cumulative** — since session start or since the `reset-net` pipe command
- **running max** — session extremum, latched forever until `reset-max`
- **calc** — arithmetic over other metrics, no sensor of its own

**Halo source** — the full chain, step by step, from the first number anyone could read to the
pixels. Steps shared by every metric are factored out into the **tail** below; rows say "→ **tail**"
rather than repeating it.

**Provider** — links to its row in [Source Providers](#source-providers).

**Est. latency** — wall-clock from the thing physically happening to the pixel changing on
screen, as **min–max**. Built from the measured per-poll costs and the configured cadences; see
[Estimated latency](#estimated-latency) below for the model and its assumptions.

---

<a id="tail"></a>
## The shared publish → paint tail

**T1 · publish.** The provider calls `MetricSink.Set(name, value)`
([MetricSink.cs:38](../src/Halo.Collector/MetricSink.cs#L38)): cached slot lookup, then one atomic
8-byte store into the shared section plus a QPC write-stamp (that stamp is what `age`/staleness is
computed from). **No rate of its own** — it runs inline inside the poll that produced the value.

**T2 · session max** (only metrics registered with `RegisterWithMax`). The same `Set` compares
against the running max and, if higher, does a second atomic store into the `<name>.max` slot
([MetricSink.cs:42-46](../src/Halo.Collector/MetricSink.cs#L42-L46)). **Rate = the base metric's
rate.** Cleared by the `reset-max` pipe command, which the widget context menu sends
([App.cs:473](../src/Halo.Widgets/App.cs#L473) → [Program.cs:153](../src/Halo.Collector/Program.cs#L153)).

**T3 · widget wake.** The `Halo.Widgets` master loop sleeps in `MsgWaitForMultipleObjectsEx` until
the soonest window is due, a window message arrives, or the collector signals
`Local\Halo.FramesReady.v1` ([App.cs:132-152](../src/Halo.Widgets/App.cs#L132-L152)).

**T4 · read.** `MetricCache.Tick()` re-reads the shared section once per wake
([MetricCache.cs:26](../src/Halo.Widgets/MetricCache.cs#L26)). Name→index mappings are cached and
invalidated if the collector restarts (start-QPC check,
[MetricCache.cs:43](../src/Halo.Widgets/MetricCache.cs#L43)).

**T5 · format + paint.** The due window rebuilds its element tree; each `TextEl.Text` lambda formats
the value. Repaint is **dirty-driven** — if the formatted string is unchanged, no D2D work happens.
Draw goes to a D2D target on the shared D3D11 device, composited by DirectComposition.

**Widget tick rate = 5 Hz**, from
`Math.Clamp(Config.RateHz ?? Settings.DefaultRateHz, 0.1, 100)`
([WidgetWindow.cs:30](../src/Halo.Widgets/WidgetWindow.cs#L30)). `defaultRateHz` is **5** in
[config/settings.json](../config/settings.json) and no widget in
[config/widgets.json](../config/widgets.json) sets a per-widget `rateHz`, so all 12 windows tick at
5 Hz. **Configurable** globally (`defaultRateHz`) and per widget (`rateHz`, cap 100).

> **Worst-case staleness of any number on screen ≈ one publish interval + one 200 ms widget tick.**

**Graph sampling = 5 Hz, not configurable.** Non-frame `GraphEl`s sample on their own clock,
independent of the widget tick ([Elements.cs:194-200](../src/Halo.Widgets/Render/Elements.cs#L194-L200));
every panel hard-codes `SampleRateHz = 5` (commit `b783a43`, "Sample all non-FPS graphs at 5 Hz").
Not exposed as a setting because the ring is a fixed 188 samples wide at 1 logical px per sample —
the rate *is* the visible history span (188 ÷ 5 ≈ 38 s), so a knob would silently change what the
graph means rather than just how smooth it is.
⚠️ `graphHistoryS` (600) exists in settings and in the Settings UI
([GeneralPage.xaml.cs:56](../src/Halo.Settings/Pages/GeneralPage.xaml.cs#L56)) but **nothing reads
it** — dead config.

**Frame graphs are the exception** — they are event-driven, not sampled: see [§4](#4-fps-counter-panels).

---

<a id="estimated-latency"></a>
## Estimated latency — the model

Every **Est. latency** cell is `min–max`, built from three kinds of number:

- **measured** — per-poll execution cost, median (and p90 where it matters), from 4,907 `status:`
  lines across `logs/collector-*.log`
- **configured** — poll periods, flush periods, coalesce windows, read straight from the code
- **unverified** — marked *inline* wherever a source's own update cadence isn't known

**Min** assumes everything lands at the luckiest instant (the sensor updates the moment before a
poll, which lands the moment before a widget tick). **Max** assumes every stage misses by a full
period. Neither is typical — the middle of the range is.

### The two shared tails

**T — poll tail: 2–219 ms.** What every polled metric pays after the collector publishes it.

| Stage | Delay | Source |
|---|---|---|
| widget tick wait (5 Hz) | 0–200 ms | [WidgetWindow.cs:30](../src/Halo.Widgets/WidgetWindow.cs#L30) |
| `MetricCache.Tick` + `Panel.Update` + layout + D2D + DComp commit | ~2 ms | derived: 12.5% of a core ÷ ≤62.5 repaints/s — includes the other widgets' ticks, so it's an upper estimate |
| DWM compose → widget monitor scanout | 0–16.7 ms | 60 Hz widget monitor |

**Tf — frame-event tail: 2–35 ms.** Replaces T on the **FPS panel only** — the one panel with a
frame graph ([Panel.cs:20](../src/Halo.Widgets/Render/Panel.cs#L20)), so the only one the
`FramesReady` event pulls forward. Its 200 ms tick wait is replaced by the 16 ms coalesce
([App.cs:176](../src/Halo.Widgets/App.cs#L176)); the repaint and scanout stages are identical.
With no frames flowing the panel falls back to **T**.

### Two things the totals deliberately separate

**Transport latency** (the number in bold) is *how old the newest sample is when you see it*.
**Aggregation settle** (the italic note) is *how long until a change is fully reflected* — a rolling
1 s FPS shows the newest frame within ~47 ms but takes a full second to finish moving. Only the
frame graph has neither: it draws raw per-frame values with no window at all.

### Assumptions, stated plainly

- **60 Hz widget monitor.** The 0–16.7 ms scanout stage scales with whatever panel the widget is on.
- **The ~2 ms repaint is derived**, not timed directly — it comes from a process-level CPU
  percentage divided by a repaint count.
- **ETW dispatch and blob-parse stages are assumed sub-ms.** They are in-memory work with no queue,
  but nothing measures them in isolation.
- **A source's own staleness is often unknown** — how stale an NVML reading, a SMART temperature or
  a SuperIO fan register already is before Halo reads it. Where the repo records a figure it is
  included; where it doesn't, the cell says so.
- **Idle mode is excluded.** With no 3D app the fps pipeline relaxes its flush to 100 ms and mutes
  the tap, which makes the frame lanes much slower until a frame re-arms them (~150 ms).

### The spread, worst case

| Rank | Metric group | Max | Why |
|---|---|---|---|
| 1 | `net.ip.external` | **~300 s** | 5-minute refresh interval |
| 2 | `drive.<x>.temp.c` | **~30.5 s** | 30 s SMART poll interval + a 258 ms read |
| 3 | `dlss.*` | **~10.2 s** | 10 s NGX module scan |
| 4 | `gpu.voltage.v`, `gpu.fan.rpm` (LHM path) | **~1.27 s** | 1 Hz poll + a 54 ms NVAPI sweep |
| 5 | `proc.*` | **~1.22 s** | 1 Hz snapshot |
| 6 | `ram.*`, `drive` space, IPs, uptime | **~1.22 s** | 1 Hz builtin poll |
| 7 | `fan.*`, `cpu.vcore.v` | **~1.22 s** | 1 Hz SuperIO poll |
| 8 | `cpu.package.*`, `cpu.clock.mhz` | **~445 ms** | 5 Hz poll + a 26 ms MSR sweep |
| 9 | `cpu.total.pct`, `cpu.core.*` | **~435 ms** | 5 Hz poll + the 15.6 ms kernel tick |
| 10 | `gpu.*` (NVML), `net.*.bps`, `drive.*.bps` | **~419 ms** | 5 Hz poll, sub-ms read |
| 11 | `fps.displayed` and friends | **~87 ms** | 40 Hz drain + the flip wait |
| 12 | `fps.presented` and friends | **~47 ms** | per-present push, 10 ms ETW flush |

**Roughly half of every number above is the widget half.** `T` alone is 2-219 ms, and it is
identical for every polled metric — so nothing on a dashboard panel can beat ~219 ms worst case
no matter how fast its provider runs. Only the FPS panel escapes it, via `Tf`.

## 1. Clock / Uptime panel

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `widget-side` (date) | 12/9/2026 | — | latest | Rainmeter `Time` | **1** No collector involvement at all. **2** The panel's `Text` lambda calls `c.Now.ToString("d/M/yyyy")` ([ClockPanel.cs:18](../src/Halo.Widgets/PanelDefs/ClockPanel.cs#L18)), where `Now` is `DateTime.Now` captured once per widget tick. **3** → **tail** T5 only (format + dirty-check + paint). Effective refresh 5 Hz; the date string changes once a day, so the dirty check discards essentially every repaint. | [widget-side](#p-widget) | **~2-219 ms** · no collector step · **T** 2-219 (tick 0-200 + paint ~2 + scanout 0-16.7) |
| `widget-side` (day of year) | Day: 255 | — | latest | Rainmeter `Time` | Same as the date row, `c.Now.DayOfYear` ([ClockPanel.cs:25](../src/Halo.Widgets/PanelDefs/ClockPanel.cs#L25)). | [widget-side](#p-widget) | **~2-219 ms** · no collector step · **T** 2-219 (tick 0-200 + paint ~2 + scanout 0-16.7) |
| `widget-side` (time) | 22:47:13 | — | latest | Rainmeter `Time` | Same chain; `c.Now.ToString("H:mm:ss")` ([ClockPanel.cs:35](../src/Halo.Widgets/PanelDefs/ClockPanel.cs#L35)). The 5 Hz tick is what makes the seconds digit look live — at 1 Hz it would visibly stutter against the system clock. The `:ss` tail renders at 13 pt inside a 20 pt string via `InlineSizePt`, matching Rainformer's `InlineSetting=Size`. | [widget-side](#p-widget) | **~2-219 ms** · no collector step · **T** 2-219 (tick 0-200 + paint ~2 + scanout 0-16.7) |
| `widget-side` (weekday) | SATURDAY | — | latest | Rainmeter `Time` | Same chain; `c.Now.DayOfWeek` upper-cased ([ClockPanel.cs:48](../src/Halo.Widgets/PanelDefs/ClockPanel.cs#L48)). | [widget-side](#p-widget) | **~2-219 ms** · no collector step · **T** 2-219 (tick 0-200 + paint ~2 + scanout 0-16.7) |
| `sys.uptime.s` | 39481.656 → `0d 10h 58m 1` | s | latest | Rainmeter `Uptime` | **1** `BuiltinProvider.Poll` reads `Environment.TickCount64 / 1000.0` — the OS tick counter since boot, no syscall cost ([BuiltinProvider.cs:70](../src/Halo.Collector/Providers/BuiltinProvider.cs#L70)). **2** Poll runs at **1 Hz** (`DefaultRateHz = 1`, cap 4, [BuiltinProvider.cs:18-19](../src/Halo.Collector/Providers/BuiltinProvider.cs#L18-L19)) — **not configurable**: `Program.cs:127` adds the provider with no `requestedRateHz` override, so nothing can raise it. Deliberate — uptime advances 1 s per second, a faster poll cannot produce a new value. **3** → **tail** T1. **4** → **tail** T3/T4. **5** `ValueFormat.Uptime` splits into `d/h/m/s` in Rainformer's exact `%4!i!d %3!i!h %2!i!m %1!i!` shape ([ValueFormat.cs:31](../src/Halo.Widgets/ValueFormat.cs#L31)), painted into the uptime pill. The seconds digit is 1 Hz-accurate but polled onto a 5 Hz tick, so it can be up to 200 ms late. | [builtin](#p-builtin) | **~3-1220 ms** · poll wait 0-1000 + read 1.1 + **T** 2-219 · *value only changes 1x/s* |

## 2. POWER panel

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `cpu.vcore.v` | 1.280 | V | latest | HWiNFO | **1** LHM opens a `Computer` with `IsMotherboardEnabled` only ([LhmProvider.cs:51-58](../src/Halo.Collector/Providers/LhmProvider.cs#L51-L58)); the SuperIO chip (NCT6687D on this board) is read over ISA port I/O through the PawnIO/WinRing0 ring0 driver — **needs elevation**, and the port access is serialised behind a global ISA mutex shared with FanControl. **2** `hw.Update()` sweeps every SuperIO sensor. **3** `PollSuperIo` picks the first `SensorType.Voltage` sensor whose name contains "core", or `VIN0` ([LhmProvider.cs:180-183](../src/Halo.Collector/Providers/LhmProvider.cs#L180-L183)), and rejects NaN. **4** Poll runs at **1 Hz** (`DefaultRateHz = 1`, hard cap **2 Hz**, [LhmProvider.cs:31-47](../src/Halo.Collector/Providers/LhmProvider.cs#L31-L47)). **Not configurable** — `Program.cs:134` adds it with no rate override, and the cap exists because each sweep is millisecond-scale port I/O behind a mutex another live app (FanControl) also wants. **5** → **tail** T1 + **T2** (registered via `RegisterWithMax`). **6** → **tail** T3-T5; formatted to 3 decimals on the POWER panel ([PowerPanel.cs:54](../src/Halo.Widgets/PanelDefs/PowerPanel.cs#L54)). | [lhm-superio](#p-lhm-superio) | **~5-1222 ms** · poll wait 0-1000 + ISA sweep 2.7 + **T** 2-219 · *+ unknown EC scan cadence* |
| `cpu.vcore.v.max` | 1.316 | V | running max | HWiNFO | **tail T2** over `cpu.vcore.v` — compared and latched on every one of its 1 Hz publishes. Survives forever (the dump shows `age=37955s`) until the context-menu reset fires `reset-max`. | [`.max` sink](#p-max) | same as `cpu.vcore.v` — the max compare is inline in the same `Set` |
| `gpu.voltage.v` | 0.840 | V | latest | HWiNFO | **1** NVML has no public voltage API, so this comes from LHM's NVIDIA GPU part (NVAPI) — a separate `Computer` with `IsGpuEnabled` only ([LhmProvider.cs:57](../src/Halo.Collector/Providers/LhmProvider.cs#L57)); **works unelevated**. **2** `PollGpu` takes the first `SensorType.Voltage` sensor on the first `GpuNvidia` hardware ([LhmProvider.cs:264-266](../src/Halo.Collector/Providers/LhmProvider.cs#L264-L266)). **3** Poll at **1 Hz**, cap 2 Hz, **not configurable** (same reason as SuperIO — the whole LHM part is one `Update()` sweep). **4** → **tail** T1 + T2 + T3-T5. | [lhm-gpu](#p-lhm-gpu) | **~56-1273 ms** · poll wait 0-1000 + NVAPI sweep 54 + **T** 2-219 |
| `gpu.voltage.v.max` | 0.995 | V | running max | HWiNFO | **tail T2** over `gpu.voltage.v`. | [`.max` sink](#p-max) | same as `gpu.voltage.v` |
| `cpu.package.power.w` | 56.434 | W | latest | HWiNFO | **1** The LHM CPU part reads the RAPL MSRs through the ring0 driver — **needs elevation**. **2** `PollCpu` accepts a `SensorType.Power` sensor named `CPU Package` or `Package`, value > 0 ([LhmProvider.cs:150-152](../src/Halo.Collector/Providers/LhmProvider.cs#L150-L152)). RAPL is itself an energy counter the CPU integrates, so "latest" here means "the package's own averaging window", not a true instant. **3** Poll at **`settings.defaultRateHz` = 5 Hz**, cap **20 Hz** ([Program.cs:133](../src/Halo.Collector/Program.cs#L133), [LhmProvider.cs:35](../src/Halo.Collector/Providers/LhmProvider.cs#L35)). **Configurable** — this is one of only three providers wired to `defaultRateHz`. **4** → **tail** T1 + T2 + T3-T5. | [lhm-cpu](#p-lhm-cpu) | **~28-445 ms** · poll wait 0-200 + MSR sweep 26 (p90 52) + **T** 2-219 · *RAPL is itself an integrated window* |
| `cpu.package.power.w.max` | 204.93 | W | running max | HWiNFO | **tail T2** over `cpu.package.power.w`, at its 5 Hz cadence. | [`.max` sink](#p-max) | same as `cpu.package.power.w` |
| `gpu.power.w` | 31.737 | W | latest | HWiNFO | **1** `nvmlDeviceGetPowerUsage` on device index 0 returns board power in milliwatts ([NvmlProvider.cs:116](../src/Halo.Collector/Providers/NvmlProvider.cs#L116)). **2** **Sanity gate:** the sample is dropped entirely if it exceeds 4× the card's own power limit, read once at init from `nvmlDeviceGetPowerManagementLimitConstraints` ([NvmlProvider.cs:67-70](../src/Halo.Collector/Providers/NvmlProvider.cs#L67-L70)). Added because resuming from S3 made NVML return `NVML_SUCCESS` with 371,940 W on a 310 W card, which the `.max` latch then kept forever (commit `781ea97`). Rejections are logged at most once a minute. **3** ÷ 1000 → watts. **4** Poll at **`defaultRateHz` = 5 Hz**, cap **20 Hz** ([Program.cs:132](../src/Halo.Collector/Program.cs#L132)) — the cap is 20 because one poll issues 8 NVML calls at ~0.2-1 ms each. **Configurable** via `defaultRateHz`. **5** → **tail** T1 + T2 + T3-T5. | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 |
| `gpu.power.w.max` | 247.001 | W | running max | HWiNFO | **tail T2** over `gpu.power.w` — and the only reason the ceiling gate in step 2 above exists. | [`.max` sink](#p-max) | same as `gpu.power.w` |

## 3. DRIVES panel

**Eight volumes as configured today: C D E F G H I J** (`driveLetters` in
[settings.json](../config/settings.json) — was 7 at the original audit; J was added later). The list
is **hot-reloaded**: `BuiltinProvider.SyncDrives` registers new letters and stales removed ones each
poll ([BuiltinProvider.cs:48-66](../src/Halo.Collector/Providers/BuiltinProvider.cs#L48-L66)),
`DiskIoProvider` rebuilds its whole PDH query on change
([DiskIoProvider.cs:74-79](../src/Halo.Collector/Providers/DiskIoProvider.cs#L74-L79)), and
`lhm-storage` invalidates its letter→model map
([LhmProvider.cs:198-209](../src/Halo.Collector/Providers/LhmProvider.cs#L198-L209)).

| Metric | Example (C:) | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `drive.<x>.label` | "Local NVME" | — | latest | Rainmeter `FreeDiskSpace` | **1** `GetVolumeInformationW("C:\\", …)` returns the volume label ([BuiltinProvider.cs:141](../src/Halo.Collector/Providers/BuiltinProvider.cs#L141)). **2** Written as a **seqlock string** (not an 8-byte slot) — the writer bumps a sequence counter, copies bytes, bumps again; readers retry on an odd counter. **3** Poll **1 Hz**, cap 4, **not configurable** (no rate override in `Program.cs`; a volume label changes about never). **4** → **tail** T3-T5; drawn as `(C:) Local NVME` in the drive's title row ([DrivesPanel.cs:49](../src/Halo.Widgets/PanelDefs/DrivesPanel.cs#L49)). | [builtin](#p-builtin) | **~3-1220 ms** · poll wait 0-1000 + read 1.1 + **T** 2-219 · *label is effectively static* |
| `drive.<x>.used.b` | 1343735713792 | B | latest | Rainmeter `FreeDiskSpace` | **1** `GetDiskFreeSpaceExW` returns total bytes and free-to-caller bytes; used = total − freeToCaller ([BuiltinProvider.cs:86-90](../src/Halo.Collector/Providers/BuiltinProvider.cs#L86-L90)). Note this is *free to this user*, so a quota'd volume reports the quota view, not the raw disk. **2** If the call fails (volume unmounted) the metric is **marked stale** rather than zeroed, so the panel shows N/A instead of a lie. **3** Poll **1 Hz**, cap 4, **not configurable** — the call hits the filesystem, and free space does not meaningfully move at sub-second resolution. **4** → **tail** T1, T3-T5. **5** `ValueFormat.AutoScale` (Rainmeter's 1024-step scaler) → `1.2 T`. | [builtin](#p-builtin) | **~3-1220 ms** · poll wait 0-1000 + read 1.1 + **T** 2-219 |
| `drive.<x>.total.b` | 2046931496960 | B | latest | Rainmeter `FreeDiskSpace` | Same call and cadence as `used.b` — both come out of the one `GetDiskFreeSpaceExW`. | [builtin](#p-builtin) | **~3-1220 ms** · poll wait 0-1000 + read 1.1 + **T** 2-219 · *total is static* |
| *(used bar)* | 66% fill | — | calc | skin math | **1** Widget-side only: `used.b × 100 ÷ total.b` at paint time. **2** Fill color flips to `barWarn` at ≥ 75% ([DrivesPanel.cs:110-114](../src/Halo.Widgets/PanelDefs/DrivesPanel.cs#L110-L114)). **3** Recomputed on every 5 Hz tick; no metric is published for it. | [widget-side](#p-widget) | same as `drive.<x>.used.b` — computed at paint time, adds nothing |
| `drive.<x>.temp.c` | 50 | °C | latest | HWiNFO (NVMe/SMART) | **1** A separate LHM `Computer` with `IsStorageEnabled` issues SMART / NVMe identify IOCTLs — **needs elevation**, and each drive costs tens of milliseconds. **2** Halo maps volume letters to disk models itself (`DriveMap.LetterToModel`), because LHM keys temperatures by *device model string* while the panel is per *volume letter*. **3** Matching is deliberately fuzzy: containment either way, then whitespace-stripped containment, because the IOCTL descriptor and LHM's name differ by vendor prefix/size suffix ([LhmProvider.cs:231-241](../src/Halo.Collector/Providers/LhmProvider.cs#L231-L241)). No match → **marked stale** and logged once per letter. **4** Poll at **1/30 Hz (once every 30 s)**, hard cap **0.2 Hz** ([LhmProvider.cs:35](../src/Halo.Collector/Providers/LhmProvider.cs#L35)). **Not configurable** — the cap would refuse a faster request anyway. ⚠️ An earlier revision of this doc claimed SMART reads "can spin up or stall a sleeping drive"; that was never sourced and is **contradicted by measurement** — 11 sweeps at 30 s and 10 s spacing all completed in 218-277 ms, far too fast for the 5-10 s spin-up of the three HDDs on this machine ([Provider cost measurement](#provider-cost-measurement)). The live dump shows `age=7.88s`, i.e. mid-interval. **5** → **tail** T1, T3-T5; colored through `WarnColor` staging. | [lhm-storage](#p-lhm-storage) | **~260 ms - 30.5 s** · poll wait 0-30000 + SMART 258 + **T** 2-219 · *worst case is a full 30 s poll wait* |
| `drive.<x>.read.bps` | 0 | B/s | interval avg | perf counters / HWiNFO | **1** One PDH query holds `\LogicalDisk(C:)\Disk Read Bytes/sec`, added via `PdhAddEnglishCounterW` — English names on purpose, so a localised Windows still binds ([DiskIoProvider.cs:49](../src/Halo.Collector/Providers/DiskIoProvider.cs#L49)). **2** `PdhCollectQueryData` samples all counters at once; **the first collect is discarded** (`_primed`) because a rate counter needs two samples ([DiskIoProvider.cs:83](../src/Halo.Collector/Providers/DiskIoProvider.cs#L83)). **3** PDH computes Δbytes ÷ Δt between our two collects — so the window is exactly our poll period, ~200 ms. **4** Poll at **5 Hz** (`DefaultRateHz = 5`, cap 64, [DiskIoProvider.cs:22-23](../src/Halo.Collector/Providers/DiskIoProvider.cs#L22-L23)). **Rate not configurable** (no override in `Program.cs`; only the *drive list* is) — though ⚠️ **the usual justification for that is wrong**: the raw byte counters update **per I/O completion**, not on a ~1 s tick (measured 2026-09-13, see [Counter-granularity measurement](#counter-granularity-measurement)), so the poll rate genuinely sets the resolution and short bursts are under-reported at 5 Hz. **5** → **tail** T1, T3-T5. **6** Drawn twice: as `AutoScale` text and as an arrow whose color flips to `red` when the rate is > 0 ([DrivesPanel.cs:135](../src/Halo.Widgets/PanelDefs/DrivesPanel.cs#L135)). | [disk-io](#p-disk-io) | **~2-419 ms** · poll wait 0-200 + PDH collect 0.2 + **T** 2-219 · *counter updates per I/O — measured, so this is the whole story* |
| `drive.<x>.write.bps` | 20395.633 | B/s | interval avg | perf counters / HWiNFO | Identical to `read.bps`, counter `\LogicalDisk(C:)\Disk Write Bytes/sec`, same query, same collect, same 5 Hz. | [disk-io](#p-disk-io) | **~2-419 ms** · poll wait 0-200 + PDH collect 0.2 + **T** 2-219 · *as read.bps* |
| `drive.<x>.activity.pct` | 3.918 | % | interval avg | HWiNFO | Same PDH query, counter `\LogicalDisk(C:)\% Disk Time`, clamped 0-100 ([DiskIoProvider.cs:89](../src/Halo.Collector/Providers/DiskIoProvider.cs#L89)). ⚠️ **Published but never displayed** — no panel reads it (verified: zero hits for `DriveActivityPct` under `src\Halo.Widgets`). Registered for all 8 volumes, so it costs 8 counters and 8 slots for nothing. | [disk-io](#p-disk-io) | n/a — published but never drawn |
| *(read / write history graphs)* | sparkline pair | — | 5 Hz samples of an interval avg | same | **1** Two half-width `GraphEl`s (94 px each), one series **per drive**, all in the `histogram` color ([DrivesPanel.cs:169-174](../src/Halo.Widgets/PanelDefs/DrivesPanel.cs#L169-L174)). **2** Each series samples `drive.<x>.write.bps` / `read.bps` at **`SampleRateHz = 5`** on the graph's own clock — 94 samples ≈ 19 s of visible history. **Not configurable** (see tail). **3** Autoscaled to the ring max, so the two halves have independent Y scales. **4** Either half can be hidden via the `graphDriveWrite` / `graphDriveRead` widget options (commit `c0ca99a`). | [disk-io](#p-disk-io) → [widget-side](#p-widget) | same as the rate it samples; the 5 Hz sampler fires on the same tick, adding nothing |

Live temps at the 2026-09-12 dump: C 50 · D 52 · E 36 · F 37 · G 42 · H 43 · I 32 · J 46 °C.

<a id="4-fps-counter-panels"></a>
## 4. FPS COUNTER panels (PRESENTED and DISPLAYED)

Frame data is **two independent lanes** feeding two instances of the same panel
(`Options["stream"]`). Today `fps-presented` is enabled and `fps-displayed` is disabled in
[widgets.json](../config/widgets.json) — disabling it was worth 9 points of widget CPU
([perf-usage-breakdown.md](perf-usage-breakdown.md), commit `6c844f7`).

**Tap lane (presented).** Halo's own real-time ETW session on the inbox DXGI and Direct3D9 providers,
reporting each present the instant its start event flushes — no wait for the frame's displayed/dropped
fate. Frames are tagged `FrameFlags.Provisional`.

**Resolved lane (displayed).** The bundled Intel PresentMon 2 service + `PresentMonAPI2.dll`, which
knows each frame's actual fate (displayed, dropped, generated, repeated) and its flip times.

**Which lane the presented panel uses** is decided per poll by `fps.tap.active`: the tap when it has
seen a present in the last 2 s, resolved-lane fallback otherwise
([PresentMonProvider.cs:345-357](../src/Halo.Collector/Providers/PresentMonProvider.cs#L345-L357)).
Vulkan/OpenGL titles emit no DXGI/D3D9 present event, so they always land on the fallback.

**Target selection** is the foreground window's PID, re-checked every 40 Hz poll, with a shell
blocklist (explorer, dwm, the Halo processes, …)
([PresentMonProvider.cs:443-478](../src/Halo.Collector/Providers/PresentMonProvider.cs#L443-L478)).
A target switch clears all stats and accumulators.

**Idle mode.** 10 s with no frames → the service's ETW flush relaxes to 100 ms and the tap's ETW
providers are *disabled* (the session stays alive); a frame re-arms both in ~150 ms
([PresentMonProvider.cs:393-417](../src/Halo.Collector/Providers/PresentMonProvider.cs#L393-L417)).
The criterion is frames, not focus — focusing VS Code makes it a "target" but produces no frames.
While idle the provider calls `MarkAllStale("fps.")` every poll, which is why the live dump shows
`fps.app.pid` and `fps.refresh.hz` as N/A even though `fps.app.name` still reads `"Code"` (strings
carry no stale flag).

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `fps.presented` | N/A (desktop idle) | fps | rolling 1 s | Afterburner MAHM `Framerate` | **1** The ETW session is created on `Microsoft-Windows-DXGI` (`CA11C036-…`) and `Microsoft-Windows-Direct3D9` at Informational level ([PresentTap.cs:62-64](../src/Halo.Collector/Providers/PresentTap.cs#L62-L64)) — **needs admin**. **2** A dedicated thread sits in `Source.Process()`; a second thread calls `session.Flush()` on a timer, because ETW's own flush granularity is 1 s and that is useless for a live readout. Flush period = **`settings.presentMonEtwFlushMs` = 10 ms**, clamped 1-100, and `timeBeginPeriod(1)` is set so `Sleep(10)` is not rounded up to 15.6 ms ([PresentTap.cs:91-105](../src/Halo.Collector/Providers/PresentTap.cs#L91-L105)). **This is configurable**; 250 ms while idle. **3** Per event: filter to the target PID, accept only `PresentStart`(42) / `PresentMpoStart`(55) / D3D9 `Present`(1), and drop `DXGI_PRESENT_TEST` occlusion probes ([PresentTap.cs:150-166](../src/Halo.Collector/Providers/PresentTap.cs#L150-L166)). **4** Frametime = Δ raw QPC **per swapchain** (`_lastBySwapchain`), so a multi-swapchain app does not interleave into garbage; the first present on a swapchain and any gap > 1000 ms is discarded as a new baseline. **5** The entry goes into a `FrameStats` window and `Consume()` runs **per present** — FPS = frame count ÷ actual span over the newest 1 s, **anchored to the newest frame's timestamp, not wall-clock** ([FrameStats.cs:132](../src/Halo.Collector/FrameStats.cs#L132)). **6** → **tail** T1, published **per present** (~240 publishes/s at 240 fps — no gating, the rolling values glide). **7** → **tail** T3-T5; the panel colors it through `WarnColor(v, 30, 60, 90, 120)`. | [present-tap](#p-tap) | **~3-47 ms** · ETW flush 0-11 + dispatch/parse ~1 + **Tf** 2-35 · *+1 s for a change to fully settle* |
| `fps.displayed` | N/A | fps | rolling 1 s (displayed frames only) | — (did not exist) | **1** `PresentMonSdkSource` spawns the bundled PresentMon 2 service as a **console-mode child** — no SCM registration — and P/Invokes `PresentMonAPI2.dll`. **2** `pmSetEtwFlushPeriod(presentMonEtwFlushMs)` tunes the service's own ETW flush ([PresentMonSdkSource.cs:48](../src/Halo.Collector/Providers/PresentMonSdkSource.cs#L48)) — **configurable**, same setting as the tap. **3** A frame query is registered for `PresentStartQpc`, `BetweenPresents`, `BetweenDisplayChange`, `UntilDisplayed`, `FrameType`, `ClickToPhotonLatency`, … and the byte offset of each field inside the returned blob is cached at init. **4** Every poll, `pmConsumeFrames` drains the service's shared ring in `Capacity`-sized batches until a short read ([PresentMonSdkSource.cs:267-307](../src/Halo.Collector/Providers/PresentMonSdkSource.cs#L267-L307)). Each blob becomes a `FrameEntry` with a real `PRESENT_START_QPC` and flags: `Displayed` when either display-side value is real, `Generated` for any frame type that is not Application/NotSet/Repeated. **5** `FrameStats.Consume` counts only `Displayed` frames over the newest 1 s ÷ actual span. **6** Poll = drain + publish at **40 Hz** (`DefaultRateHz = 40`, cap **120**, [PresentMonProvider.cs:29-30](../src/Halo.Collector/Providers/PresentMonProvider.cs#L29-L30)). **Not configurable — deliberately.** It is a *drain* cadence, not a sample rate: frames carry their own QPC timestamps, so polling faster does not add data, it just splits the same frames into more batches. **7** → **tail** T1 (40 Hz), T3-T5. | [presentmon](#p-presentmon) | **~2-87 ms** · flip wait 0-16.7 + service flush 0-10 + drain 0-25 + read 0.2 + **Tf** 2-35 · *+1 s settle* |
| `fps.frametime.presented.ms` | N/A | ms | rolling 100 ms mean | Afterburner `Frametime` | The tap chain above, steps 1-4, then `AvgFrametimeShortMs` = mean of every frame in the newest **100 ms** ([FrameStats.cs:91](../src/Halo.Collector/FrameStats.cs#L91)). 100 ms rather than 1 s so the number feels live; published per present. Falls back to the resolved lane's identical computation when the tap is inactive. | [present-tap](#p-tap) | **~3-47 ms** · as `fps.presented` · *+100 ms settle* |
| `fps.frametime.displayed.ms` | N/A | ms | rolling 100 ms mean | — | Resolved lane, same 100 ms window but over **flip-to-flip** deltas (`BetweenDisplayChange`) — what the screen actually did, not what the app submitted. Published at the 40 Hz drain. | [presentmon](#p-presentmon) | **~2-87 ms** · as `fps.displayed` · *+100 ms settle* |
| `fps.frametime.presented.worst.ms` | N/A | ms | rolling 1 s max | — | Same stream and windows as above, but the **maximum single frametime** in the newest 1 s rather than the mean ([FrameStats.cs:117](../src/Halo.Collector/FrameStats.cs#L117)). 1 s on purpose: a hitch stays readable for a full second instead of flashing past in 100 ms. | [present-tap](#p-tap) | **~3-47 ms** · as `fps.presented` · *holds the peak for 1 s by design* |
| `fps.frametime.displayed.worst.ms` | N/A | ms | rolling 1 s max | — | Resolved-lane equivalent, over displayed frametimes. | [presentmon](#p-presentmon) | **~2-87 ms** · as `fps.displayed` · *holds 1 s* |
| `fps.low1.presented` · `fps.low01.presented` | N/A | fps | rolling 60 s | Afterburner (since-reset window) | **1** Same frame stream. **2** Every frametime in the **full rolling window** is collected, sorted, and the worst 1% (or 0.1%) averaged; low FPS = `1000 ÷ meanWorst` — the CapFrameX definition ([FrameStats.cs:161-173](../src/Halo.Collector/FrameStats.cs#L161-L173)). Returns 0 below 16 samples. **3** Window = **`settings.frameLowsWindowS` = 60 s** — **configurable**. **4** The sort is the expensive part, so the result is **recomputed at 2 Hz and cached** between ([FrameStats.cs:93-94](../src/Halo.Collector/FrameStats.cs#L93-L94)) — hard-coded, not configurable, because at 240 fps the window holds ~14,400 floats and sorting that at the publish rate would dominate the collector. **5** → **tail** T1 at the lane's publish rate (the value only actually changes 2×/s), T3-T5. | [present-tap](#p-tap) / [presentmon](#p-presentmon) | **~3-547 ms** · lane latency + lows recompute wait 0-500 (2 Hz cache) · *+60 s for the window to turn over* |
| `fps.low1.displayed` · `fps.low01.displayed` | N/A | fps | rolling 60 s | — | Same computation over the displayed-frametime list. | [presentmon](#p-presentmon) | **~2-587 ms** · as above on the resolved lane |
| `fps.refresh.hz` | N/A (stale while idle) | Hz | latest | skin hard-coded /144 | **1** `MonitorFromWindow(hwnd, NEAREST)` → `GetMonitorInfoW` → `EnumDisplaySettingsW` reads `dmDisplayFrequency` of the monitor the **target window** is on ([PresentMonProvider.cs:552-565](../src/Halo.Collector/Providers/PresentMonProvider.cs#L552-L565)) — so dragging a game to the 60 Hz panel changes it. **2** Republished on a **1 Hz** sub-cadence inside the 40 Hz poll (`_nextSlowPublishQpc`), because it is constant between target changes. Hard-coded, not configurable. **3** → **tail** T1, T3-T5. **4** The widget computes `Framerate: N%` = `fps ÷ refresh × 100` at paint time ([FpsPanel.cs:179-181](../src/Halo.Widgets/PanelDefs/FpsPanel.cs#L179-L181)). | [presentmon](#p-presentmon) | **~2-1035 ms** · 1 Hz sub-cadence wait 0-1000 + **Tf** 2-35 · *constant between target switches* |
| `fps.app.name` | "Code" | — | latest | — | Foreground window PID → `Process.GetProcessById(pid).ProcessName`, shell names blocked, published on the same 1 Hz sub-cadence as a seqlock string. Drives the panel's first row, or `NO 3D APP` when idle. | [presentmon](#p-presentmon) | **~2-1035 ms** · 1 Hz sub-cadence wait 0-1000 + **Tf** 2-35 |
| `fps.app.pid` | N/A | count | latest | — | Same 1 Hz sub-cadence. ⚠️ **Published but never displayed** — no panel reads it, and the idle branch's `MarkAllStale("fps.")` re-stales it 40×/s anyway. | [presentmon](#p-presentmon) | n/a — published but never drawn |
| `fps.tap.active` | 0 | — | latest | — | `1` when the tap has both a target and a present inside the last 2 s ([PresentTap.cs:51-53](../src/Halo.Collector/Providers/PresentTap.cs#L51-L53)). Published every 40 Hz poll. Its only consumer is the **frame-graph lane filter** below — it decides whether the presented graph draws `Provisional` entries or resolved ones. | [presentmon](#p-presentmon) | n/a — no pixels of its own; consumed by the graph lane filter |
| `fps.fgratio` | N/A | × | rolling / decaying calc | — | **1** Preferred path: `fps.displayed × (mean app simulation interval ÷ 1000)`, clamped 0.25-8 — this works even when generated frames are not type-tagged. The sim interval is a **decaying avg** (sum/count halved past 200 samples, [PresentMonProvider.cs:336-340](../src/Halo.Collector/Providers/PresentMonProvider.cs#L336-L340)). **2** Fallback: `FrameStats.FgRatio` = displayed frames ÷ non-generated frames over the whole 60 s window. **3** Published at the 40 Hz drain. **4** Rendered as `FG 2.0×` in the FPS panel's DLSS badge. | [presentmon](#p-presentmon) | **~2-87 ms** · as `fps.displayed` · *decaying avg: hundreds of frames to settle* |
| *(frametime graph)* | 188-bar sparkline | ms | **raw per-frame, no aggregation** | MAHM sampled 1/s | **1** The collector appends every `FrameEntry` to an append-only ring in shared memory (`Writer.AppendFrames`) and **sets the `Local\Halo.FramesReady.v1` event** — the tap does this per present, the resolved lane once per 40 Hz drain. **2** `MetricCache.Tick` pulls everything appended since its own cursor. **3** `GraphEl.FrameSample` adds **one bar per actual frame** — no sampling, no decimation ([Elements.cs:178-190](../src/Halo.Widgets/Render/Elements.cs#L178-L190)). **4** `FrameFilter` picks the lane: the presented graph takes `Provisional` entries when `fps.tap.active ≥ 1` and non-`Provisional` otherwise; the displayed graph always takes non-`Provisional` plus `FrameDisplayedOnly` ([FpsPanel.cs:156-162](../src/Halo.Widgets/PanelDefs/FpsPanel.cs#L156-L162)). **5** Repaint is **event-driven**, coalesced to **16 ms** — hard-coded, matched to the 60 Hz widget monitor; repainting faster than the panel's own display can show is pure waste ([App.cs:163-179](../src/Halo.Widgets/App.cs#L163-L179)). The boundary is anchored to *now* and only advanced when a wake is granted, because advancing it per event let a >143 Hz stream push it into the future until the graphs silently degraded to the 5 Hz fallback (commit `82abab1`). **6** Clipped at `FixedMax = 50` ms, matching the original skin. | [present-tap](#p-tap) / [presentmon](#p-presentmon) → [widget-side](#p-widget) | **~3-47 ms** tap lane / **~2-87 ms** resolved · **raw per-frame, zero aggregation** · *for a hitch the coalesce is already expired, so ~2-31 ms* |
| *(DLSS badge)* | `DLSS 310.3.0 SR FG · FG 2.0×` | — | latest + calc | NVIDIA App | Composed widget-side from the `dlss.*` metrics plus `fps.fgratio` ([FpsPanel.cs:188-198](../src/Halo.Widgets/PanelDefs/FpsPanel.cs#L188-L198)). The `dlss.*` metrics themselves are documented in [§4b](#4b-latency--dlss-panel). | [presentmon](#p-presentmon) | **~2-10035 ms** · NGX scan wait 0-10000 + **Tf** 2-35 · *rescans immediately on app switch* |

> The original screenshot's "2 FPS 1% low / 618 ms frametime" desktop artifacts are structurally
> impossible now: lows are a true rolling window, and the panel drops to a dimmed **NO 3D APP** state
> when `fps.presented` has no sample newer than 3 s
> ([FpsPanel.cs:177](../src/Halo.Widgets/PanelDefs/FpsPanel.cs#L177)).

<a id="4b-latency--dlss-panel"></a>
## 4b. LATENCY / DLSS panel

Added 2026-07-19 (commit `71b19a9`). No old-stack equivalent existed short of the NVIDIA App overlay;
the point of this panel is replacing that overlay without the App. Idle-dims like the FPS panels.

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `latency.queue.ms` | N/A (no game) | ms | rolling 1.5 s avg | — | **1** Halo opens its own ETW session on NVIDIA's `PCLStatsTraceLoggingProvider`, using the **literal GUID from NVIDIA's reference `pclstats.h`** (`0d216f06-82a6-4d49-bc4f-8f38ae56efab`), not the name hash ([Program.cs:142-146](../src/Halo.Collector/Program.cs#L142-L146)) — **needs admin**. **2** No ping broadcast is needed: enabling the provider flips the game's internal `g_PCLStatsEnable` and it self-pings ([PclStatsProvider.cs:91-92](../src/Halo.Collector/Providers/PclStatsProvider.cs#L91-L92)). **3** A `PCLStatsInput` event marks when the game's ping thread **posts** the synthetic input; marker `PC_LATENCY_PING`(8) marks when that input is **consumed** at frame start. Queue wait = consume − post, correlated by time and rejected past 200 ms ([PclStatsProvider.cs:154-158](../src/Halo.Collector/Providers/PclStatsProvider.cs#L154-L158)). **4** The sample is only banked when that frame's `PRESENT_END`(5) arrives, keyed by FrameID. **5** Averaged over a **1.5 s time window** (`LatencyWindowMs`) — time-windowed, not sample-decayed, so it tracks changes in ~1 s like the FPS headline. **6** Poll publishes at **5 Hz** (`DefaultRateHz = 5`, cap 20, [PclStatsProvider.cs:28-29](../src/Halo.Collector/Providers/PclStatsProvider.cs#L28-L29)). **Not configurable** — markers arrive at only ~5-10 pings/s, so a faster publish would republish the same average. **7** Goes **stale** (not zero) if no PCL event has arrived in 2 s. **8** → **tail** T1, T3-T5. | [pclstats](#p-pclstats) | **~2-619 ms** · marker arrival 0-200 + publish wait 0-200 + **T** 2-219 · *+1.5 s window settle* |
| `latency.render.ms` | N/A | ms | rolling 1.5 s avg | — | Same session and the same FrameID bookkeeping: `PRESENT_END` timestamp − `PC_LATENCY_PING` consume timestamp, rejected outside 0-500 ms ([PclStatsProvider.cs:166-171](../src/Halo.Collector/Providers/PclStatsProvider.cs#L166-L171)). Same 1.5 s window, same 5 Hz publish, same staleness rule. | [pclstats](#p-pclstats) | **~2-619 ms** · as `latency.queue.ms` |
| `render.rate.hz` | N/A | Hz | rolling ≥0.5 s | — | Counts `SIMULATION_START`(0) markers — one per **game-rendered** frame, before frame generation — and divides by the elapsed ETW-relative window, which is only closed once it is ≥ 500 ms long ([PclStatsProvider.cs:202-208](../src/Halo.Collector/Providers/PclStatsProvider.cs#L202-L208)). This is the only true pre-FG rate in the system. 5 Hz publish, not configurable. | [pclstats](#p-pclstats) | **~2-1119 ms** · window close wait 0-500 + publish wait 0-200 + **T** 2-219 |
| `fps.displaylatency.ms` | N/A | ms | decaying avg | — | **1** PresentMon's `UntilDisplayed` (present→photon, P2D) per frame, accepted only in the 0-200 ms range. **2** Accumulated as sum/count across the drain and **halved when the count passes 200**, so it behaves like a rolling average over the last few hundred frames without keeping a buffer ([PresentMonProvider.cs:330-340](../src/Halo.Collector/Providers/PresentMonProvider.cs#L330-L340)). **3** Published at the **40 Hz** drain, not configurable. | [presentmon](#p-presentmon) | **~2-254 ms** · service flush 0-10 + drain 0-25 + **T** 2-219 · *decaying avg* |
| `latency.click.ms` | N/A | ms | decaying avg | — | PresentMon's `ClickToPhotonLatency` per frame, same decaying accumulator, same 40 Hz drain. Only populated for frames where a real click was correlated, so it updates sporadically. | [presentmon](#p-presentmon) | **~2-254 ms** · as above · *updates only when a click is correlated* |
| `latency.allinput.ms` | N/A | ms | decaying avg | PresentMon latency | PresentMon's all-input-to-photon, same accumulator and cadence. | [presentmon](#p-presentmon) | **~2-254 ms** · as above |
| *(PC LAT headline)* | — | ms | calc | NVIDIA App overlay only | **1** Widget-side sum: `latency.queue.ms + latency.render.ms + fps.displaylatency.ms` ([LatencyPanel.cs:190-193](../src/Halo.Widgets/PanelDefs/LatencyPanel.cs#L190-L193)) — the same span the NVIDIA overlay reports, starting where the input enters the game. **2** Requires the render component to be fresh within 3 s or it returns 0; queue and display add on only if they are also fresh. **3** Recomputed on the 5 Hz widget tick, warn-colored. | [widget-side](#p-widget) | **~2-619 ms** · dominated by its slowest component (`latency.render.ms`); the sum is computed at paint time |
| *(FRAME GEN multiplier)* | — | × | calc | — | **1** Preferred: `fps.displayed ÷ render.rate.hz`, clamped 0.25-8 — displayed frames over true pre-FG rendered frames ([LatencyPanel.cs:198-204](../src/Halo.Widgets/PanelDefs/LatencyPanel.cs#L198-L204)). **2** Falls back to `fps.fgratio` when the PCL render rate is missing or stale. **3** Widget tick, 5 Hz. | [widget-side](#p-widget) | **~2-1119 ms** · dominated by `render.rate.hz`; falls back to `fps.fgratio` (~2-254 ms) |
| `dlss.sr.present` · `dlss.fg.present` · `dlss.rr.present` | 0 | — | latest | NVIDIA App | **1** `Process.GetProcessById(targetPid).Modules` is enumerated and matched by prefix: `nvngx_dlssg` → FG, `nvngx_dlssd` → RR, `nvngx_dlss` → SR ([PresentMonProvider.cs:516-535](../src/Halo.Collector/Providers/PresentMonProvider.cs#L516-L535)). Fails silently on 32-bit or protected processes. **2** The scan runs **every 10 s**, plus immediately on a target switch (`_nextNgxScan = MinValue`). Hard-coded, not configurable — a loaded DLL set does not change mid-session, and module enumeration is the single most expensive thing this provider does. **3** Zeroed when there is no target. **4** → **tail** T1, T3-T5. | [presentmon](#p-presentmon) | **~2-10219 ms** · NGX scan wait 0-10000 + **T** 2-219 |
| `dlss.version` | "" (no game) | — | latest | NVIDIA App | Same 10 s scan: `FileVersionInfo.FileVersion` of the SR DLL, written as a seqlock string. | [presentmon](#p-presentmon) | **~2-10219 ms** · as above |
| `dlss.model` | "" | — | latest | NVIDIA App | Same scan, composed from two heuristics: DLL major version ≥ **310** → `Transformer`, else `CNN`; and a path under `\DriverStore\` or `\FileRepository\` → ` · override`, else ` · game DLL` ([PresentMonProvider.cs:546-548](../src/Halo.Collector/Providers/PresentMonProvider.cs#L546-L548)). The exact runtime preset without an override would need NGX hooking — out of scope. | [presentmon](#p-presentmon) | **~2-10219 ms** · as above |
| *(PCL sparkline)* | 188-px line | ms | 5 Hz samples of a calc | — | `GraphEl` with `SampleRateHz = 5` sampling the PC LAT sum above, autoscaled (`FixedMax = null`) ([LatencyPanel.cs:161-176](../src/Halo.Widgets/PanelDefs/LatencyPanel.cs#L161-L176)). Not configurable — see tail. | [widget-side](#p-widget) | same as the PC LAT sum it samples |

## 5. GPU panel (RTX 5070 Ti)

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `gpu.name` | "NVIDIA GeForce RTX 5070 Ti" | — | latest | HWiNFO | `nvmlDeviceGetName` read **once at init** and written as a seqlock string ([NvmlProvider.cs:50-52](../src/Halo.Collector/Providers/NvmlProvider.cs#L50-L52)). Never re-read; registered at 1 Hz nominal but actually written once. | [nvml](#p-nvml) | **~2-219 ms** after startup · read once at init, then static |
| `gpu.temp.c` | 38 | °C | latest | HWiNFO | **1** `nvmlInit_v2` → `nvmlDeviceGetHandleByIndex_v2(0)` at init; `nvml.dll` ships with the NVIDIA driver, not with Halo. **Works unelevated.** **2** `nvmlDeviceGetTemperature(dev, GPU)` returns a whole degree ([NvmlProvider.cs:87-88](../src/Halo.Collector/Providers/NvmlProvider.cs#L87-L88)). **3** Poll at **`defaultRateHz` = 5 Hz**, cap **20 Hz** — **configurable** via `defaultRateHz`; the cap is 20 because one poll makes 8 NVML calls at ~0.2-1 ms each. **4** → **tail** T1, T3-T5. **5** `WarnColor(v, 50, 60, 70, 80)` stages the color; the whole row hides itself via `VisibleWhen` if the metric never arrives (no NVIDIA GPU). | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 |
| `gpu.usage.pct` | 20 | % | latest (NVML's own window) | HWiNFO | Same chain, `nvmlDeviceGetUtilizationRates().gpu`. Worth knowing: NVML computes this over **its own internal sampling window** (roughly 1/6 s to 1 s depending on driver), so polling at 20 Hz cannot make it more responsive — it just re-reads the same figure. | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 · *+ NVML's own sampling window on top* |
| `gpu.vram.used.mb` | 2313.086 | MB | latest | HWiNFO | Same chain, `nvmlDeviceGetMemoryInfo().used ÷ 1048576` ([NvmlProvider.cs:93-99](../src/Halo.Collector/Providers/NvmlProvider.cs#L93-L99)). | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 |
| `gpu.vram.total.mb` | 16303 | MB | latest | HWiNFO | The same single `nvmlDeviceGetMemoryInfo` call as `used.mb`. | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 · *static* |
| `gpu.vram.pct` | 14.188 | % | latest | derived | Computed **collector-side** from the same call (`used ÷ total × 100`) rather than widget-side, so the graph series can sample one metric instead of two. | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 |
| `gpu.fan.pct` | 34 | % | latest | HWiNFO | Same chain, `nvmlDeviceGetFanSpeed_v2(dev, fan 0)`. | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 |
| `gpu.fan.rpm` | 1115 | rpm | latest | HWiNFO | ⚠️ **Two providers write this one slot.** **1** At init NVML probes for `nvmlDeviceGetFanSpeedRPM` via `NativeLibrary.TryGetExport` — the API only exists on newer drivers ([NvmlProvider.cs:78](../src/Halo.Collector/Providers/NvmlProvider.cs#L78)). On this machine it exists, so NVML registers the metric first (the live dump shows the registry rate as 20 Hz = NVML's cap) and writes it at **5 Hz**. **2** `lhm-gpu` also registers it — `MetricsWriter.Register` returns the existing index for a name already present ([MetricsWriter.cs:42](../src/Halo.Shared/Metrics/MetricsWriter.cs#L42)) — and writes the NVAPI fan value into the same slot at **1 Hz**. **3** Last writer wins per write; the two agree, so this is redundant rather than wrong, and it is what keeps the row alive on older drivers where NVML lacks the export. **4** → **tail** T1, T3-T5. | [nvml](#p-nvml) + [lhm-gpu](#p-lhm-gpu) | **~2-419 ms** via nvml (5 Hz) / **~56-1273 ms** via lhm-gpu (1 Hz) — last writer wins, so it varies |
| `gpu.clock.core.mhz` | 1087 | MHz | latest | HWiNFO | Same NVML chain, `nvmlDeviceGetClockInfo(dev, GRAPHICS)`. | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 |
| `gpu.clock.mem.mhz` | 405 | MHz | latest | HWiNFO | Same NVML chain, `nvmlDeviceGetClockInfo(dev, MEM)`. | [nvml](#p-nvml) | **~2-419 ms** · poll wait 0-200 + 8 NVML calls 0.37 + **T** 2-219 |
| *(usage / temp / VRAM / fan graphs)* | up to 4 sparkline series | — | 5 Hz samples of latest | same | One `GraphEl` with up to four series, each `FixedMax = 100` so they share a 0-100 scale ([GpuPanel.cs:140-149](../src/Halo.Widgets/PanelDefs/GpuPanel.cs#L140-L149)). `SampleRateHz = 5`, 188 samples ≈ 38 s of history. Individual lines toggle via `graphGpuTemp` / `graphGpuMem` / `graphGpuFan` — all three are **off** in the current [widgets.json](../config/widgets.json), leaving only the usage line. | [nvml](#p-nvml) → [widget-side](#p-widget) | same as the metric each series samples |

> Dual-GPU machine, but everything here is `nvmlDeviceGetHandleByIndex_v2(0)` — the second GPU has
> no metrics and no panel.

<a id="6-fans-panel"></a>
## 6. FANS panel

Channels come from LHM's SuperIO enumeration, ordered by sensor identifier, capped at 8. Live names
on this board (NCT6687D): 0 `CPU Fan` · 1 `Pump Fan` · 2-7 `System Fan #1-#6`. The panel renders
**only the channels named in `fanNames`** — today `{"2": "BACK", "3": "FRONT"}`
([FansPanel.cs:32-36](../src/Halo.Widgets/PanelDefs/FansPanel.cs#L32-L36)).

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `fan.<n>.rpm` | ch2 = 1304 | rpm | latest | HWiNFO (mobo SIO/EC) | **1** The same `lhm-superio` sweep as `cpu.vcore.v` — one `hw.Update()` reads the whole NCT6687D over ISA port I/O behind the shared mutex, **elevation required**. **2** `PollSuperIo` takes all `SensorType.Fan` sensors **ordered by `Identifier`** and assigns them channel indexes 0-7 ([LhmProvider.cs:169-178](../src/Halo.Collector/Providers/LhmProvider.cs#L169-L178)) — so the channel number is a positional index into LHM's enumeration, **not** a board header number. NaN → 0. **3** Poll **1 Hz**, cap 2 Hz, **not configurable** (see §2). **4** → **tail** T1, T3-T5. **5** The panel prints `N/A` rather than 0 when `TryValue` fails, so an unelevated collector reads as "unknown" instead of "stopped" ([FansPanel.cs:55](../src/Halo.Widgets/PanelDefs/FansPanel.cs#L55)). | [lhm-superio](#p-lhm-superio) | **~5-1222 ms** · poll wait 0-1000 + ISA sweep 2.7 + **T** 2-219 · *+ >=1 fan revolution (~30-60 ms at 1000-2000 rpm) + unknown EC scan* |
| `fan.<n>.pct` | ch2 = 65.2 | % | latest (calc) | HWiNFO | **1** Computed **collector-side** in the same poll: `rpm ÷ MaxRpmFor(channel) × 100`, clamped 0-100 ([LhmProvider.cs:175-176](../src/Halo.Collector/Providers/LhmProvider.cs#L175-L176)). **2** `MaxRpmFor` reads `settings.fanMaxRpm[channel]` live from the `ConfigStore`, **defaulting to 2000 rpm** when the channel is not listed ([LhmProvider.cs:187-191](../src/Halo.Collector/Providers/LhmProvider.cs#L187-L191)). Today: ch0 = 1600, ch2 = 2000, ch3 = 2000 — everything else silently uses the 2000 default, which is why `fan.4.pct` reads 67% off 1340 rpm. **Configurable and hot-reloaded** (no restart: providers hold the store, not a snapshot). **3** Same 1 Hz cadence → **tail**. | [lhm-superio](#p-lhm-superio) | same as `fan.<n>.rpm` — the % is computed in the same poll |
| `fan.<n>.name` | "System Fan #1" | — | latest | — | LHM's own sensor name, written as a seqlock string at 1 Hz ([LhmProvider.cs:174](../src/Halo.Collector/Providers/LhmProvider.cs#L174)). ⚠️ **Published but never displayed** — the panel shows the `fanNames` nickname instead, so all 8 string slots go unread. Genuinely useful in `--dump` when working out which channel is which header. | [lhm-superio](#p-lhm-superio) | n/a — published but never drawn |

## 7. NETWORK panel

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `net.ip.internal` | 192.168.x.x *(redacted)* | — | latest | Rainmeter `SysInfo` | **1** A UDP socket is "connected" to 8.8.8.8:65530 — **no packet is sent**; the connect just makes the stack pick the interface with the default route, and `LocalEndPoint` then reveals its address ([BuiltinProvider.cs:126-135](../src/Halo.Collector/Providers/BuiltinProvider.cs#L126-L135)). Any failure returns `"N/A"`. **2** Poll **1 Hz**, cap 4, **not configurable**. **3** Seqlock string → **tail** T3-T5. | [builtin](#p-builtin) | **~3-1220 ms** · poll wait 0-1000 + read 1.1 + **T** 2-219 |
| `net.ip.external` | *(redacted — real public IP)* | — | latest | WebParser | **1** An `HttpClient` GET to **`settings.externalIpUrl`** (`https://api.ipify.org`), 5 s timeout, result trimmed and length-sanity-checked to 7-45 chars ([BuiltinProvider.cs:111-124](../src/Halo.Collector/Providers/BuiltinProvider.cs#L111-L124)). **This is the only outbound network call Halo makes.** **2** Fired when either **`settings.externalIpRefreshMinutes` = 5 min** has elapsed **or** the OS raises `NetworkChange.NetworkAddressChanged`, which flips a flag consumed on the next 1 Hz poll ([BuiltinProvider.cs:102-107](../src/Halo.Collector/Providers/BuiltinProvider.cs#L102-L107)). **Both URL and interval are configurable**; the interval is floored at 1 minute. **3** Fire-and-forget async — the poll thread never blocks on it; the last known value is republished every second meanwhile. Failure sets `"N/A"` and logs a warning. **4** Seqlock string → **tail**. | [builtin](#p-builtin) | **~2 ms - 300 s** · refresh wait 0-300000 (5 min) + HTTP fetch + **T** 2-219 · *or immediately on a network-change event* |
| `net.down.bps` | 571.708 | B/s | interval avg | Rainmeter `NetIn` | **1** The adapter is picked once per 30 s: operational, not loopback/tunnel, preferring one with a default gateway; `settings.networkInterface` = `"Best"` means auto, anything else matches by name ([NetworkProvider.cs:44-75](../src/Halo.Collector/Providers/NetworkProvider.cs#L44-L75)). **Configurable.** **2** `nic.GetIPStatistics().BytesReceived` — a cumulative 64-bit octet counter maintained by the OS. **3** Rate = `max(0, rx − prevRx) ÷ dt`, where `dt` is a `Stopwatch` measurement of the **actual** gap, not the nominal period, so a late poll does not inflate the rate ([NetworkProvider.cs:100-107](../src/Halo.Collector/Providers/NetworkProvider.cs#L100-L107)). The first sample after a NIC change is discarded (it becomes the new baseline). **4** Poll at **5 Hz** (`DefaultRateHz = 5`, cap 64, [NetworkProvider.cs:21-22](../src/Halo.Collector/Providers/NetworkProvider.cs#L21-L22)) — **rate not configurable**, only the interface is. ⚠️ Previously annotated as "driver-paced, so sub-200 ms differencing measures jitter" — **that was never verified and is wrong**: the octet counters move on every 100 ms sample under load (measured 2026-09-13, see [Counter-granularity measurement](#counter-granularity-measurement)), so the poll rate sets the resolution here too. **5** → **tail** T1 + **T2** (`RegisterWithMax`), T3-T5. **6** `AutoScale` → `571.7 ` B/s. | [network](#p-network) | **~2-419 ms** · poll wait 0-200 + read 0.16 + **T** 2-219 · *counter updates per packet batch — measured* |
| `net.up.bps` | 0 | B/s | interval avg | Rainmeter `NetOut` | Identical, over `BytesSent`. | [network](#p-network) | **~2-419 ms** · poll wait 0-200 + read 0.16 + **T** 2-219 · *as down.bps* |
| `net.down.bps.max` | 98748810.405 (94.2 MB/s) | B/s | running max | skin-side max | **tail T2** over `net.down.bps`, at its 5 Hz cadence. Also cleared by `reset-net`, which calls `sink.ResetMax("net.")` alongside zeroing the totals ([NetworkProvider.cs:97](../src/Halo.Collector/Providers/NetworkProvider.cs#L97)). | [`.max` sink](#p-max) | same as `net.down.bps` |
| `net.up.bps.max` | 3874650.608 (3.7 MB/s) | B/s | running max | skin-side max | Same. | [`.max` sink](#p-max) | same as `net.up.bps` |
| `net.down.total.b` | 46951872 | B | cumulative | Rainmeter cumulative | **1** `rx − _startRx`, where `_startRx` is the counter value at the first sample for this NIC ([NetworkProvider.cs:113](../src/Halo.Collector/Providers/NetworkProvider.cs#L113)). So "session" means *since the collector started or the adapter last changed*, not since boot. **2** The `reset-net` pipe command re-anchors `_startRx` to the current counter ([NetworkProvider.cs:90-98](../src/Halo.Collector/Providers/NetworkProvider.cs#L90-L98)); the widget context menu sends it for the network panel ([App.cs:474](../src/Halo.Widgets/App.cs#L474)). **3** Republished every 5 Hz poll → **tail**. | [network](#p-network) | **~2-419 ms** · poll wait 0-200 + read 0.16 + **T** 2-219 |
| `net.up.total.b` | 18531166 | B | cumulative | Rainmeter cumulative | Same, over `BytesSent`. | [network](#p-network) | **~2-419 ms** · poll wait 0-200 + read 0.16 + **T** 2-219 |
| *(traffic graphs)* | two half-width sparklines | — | 5 Hz samples of an interval avg | same | Two `GraphEl`s (down left, up right), one series each, `SampleRateHz = 5`, autoscaled ([NetworkPanel.cs:156-176](../src/Halo.Widgets/PanelDefs/NetworkPanel.cs#L156-L176)). | [network](#p-network) → [widget-side](#p-widget) | same as the rate each series samples |

## 8. CPU / RAM panel (i5-14600K, DDR5 6400)

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `cpu.name` | "Intel Core i5-14600K" | — | latest | HWiNFO | LHM's CPU hardware name, written as a seqlock string on every `lhm-cpu` poll ([LhmProvider.cs:140](../src/Halo.Collector/Providers/LhmProvider.cs#L140)). The panel only uses it when the widget has no `title` option; today `cpu-ram-1` sets `title` explicitly, so it is a fallback only. | [lhm-cpu](#p-lhm-cpu) | **~28-445 ms** · poll wait 0-200 + MSR sweep 26 (p90 52) + **T** 2-219 · *static in practice* |
| `cpu.package.temp.c` | 69 | °C | latest | HWiNFO | **1** The LHM CPU part reads the package DTS via MSR through the ring0 driver — **elevation required**. **2** `PollCpu` accepts a `SensorType.Temperature` sensor named `CPU Package` (Intel) **or** `Core (Tctl/Tdie)` (AMD), value > 0 — that dual match is what makes the provider vendor-portable ([LhmProvider.cs:147-149](../src/Halo.Collector/Providers/LhmProvider.cs#L147-L149)). **3** Poll at **`defaultRateHz` = 5 Hz**, cap 20 — **configurable**. **4** → **tail** T1, T3-T5. **5** Warn-staged at 50/60/70/80 °C ([CpuRamPanel.cs:35](../src/Halo.Widgets/PanelDefs/CpuRamPanel.cs#L35)); the row hides entirely if the metric never arrives. | [lhm-cpu](#p-lhm-cpu) | **~28-445 ms** · poll wait 0-200 + MSR sweep 26 (p90 52) + **T** 2-219 |
| `cpu.clock.mhz` | 5291.515 | MHz | latest | HWiNFO | The same `lhm-cpu` sweep. `PollCpu` takes the **maximum** of every `SensorType.Clock` sensor whose name does not contain "Bus" ([LhmProvider.cs:153-158](../src/Halo.Collector/Providers/LhmProvider.cs#L153-L158)) — so it reports the fastest boosting core, matching what HWiNFO's "CPU Clock" row showed, not a package average. Name-agnostic because LHM versions disagree between `CPU Core #N` and `Core #N`. Same 5 Hz, configurable. | [lhm-cpu](#p-lhm-cpu) | **~28-445 ms** · poll wait 0-200 + MSR sweep 26 (p90 52) + **T** 2-219 |
| `cpu.total.pct` | 8.097 | % | interval avg (~200 ms) | UsageMonitor | **1** One `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` into a **stack-allocated** buffer — no heap traffic, no elevation needed ([CpuKernelProvider.cs:37-41](../src/Halo.Collector/Providers/CpuKernelProvider.cs#L37-L41)). **2** Per logical CPU it reads `IdleTime`, `KernelTime`, `UserTime` (100 ns units). **Kernel time includes idle**, so busy = kernel + user − idle ([CpuKernelProvider.cs:49](../src/Halo.Collector/Providers/CpuKernelProvider.cs#L49)). **3** Deltas against the previous poll are summed across all 20 logical CPUs; total% = `100 × Σbusy ÷ Σ(busy+idle)`, clamped 0-100. The denominator is the **measured** span, so a late poll self-corrects. **4** `Initialize` calls `Poll` once to prime the deltas, so the first published value is never a since-boot average. **5** Poll at **`defaultRateHz` = 5 Hz**, cap **64 Hz** ([CpuKernelProvider.cs:14-15](../src/Halo.Collector/Providers/CpuKernelProvider.cs#L14-L15)). **Configurable** via `defaultRateHz`; the 64 Hz cap exists because these counters only advance on the ~15.6 ms kernel tick, so anything past ~64 Hz reads duplicates. **6** → **tail** T1, T3-T5. **7** The bar turns `barWarn` above 75%. | [cpu-kernel](#p-cpu-kernel) | **~2-435 ms** · counter tick 0-15.6 + poll wait 0-200 + read 0.08 + **T** 2-219 · *value is a ~200 ms average, so a spike is diluted across it* |
| `cpu.core.<0-19>.pct` | core 2 = 41.667 | % | interval avg (~200 ms) | UsageMonitor | The same single syscall and the same per-CPU delta math — all 20 values come out of **one** `NtQuerySystemInformation` call, which is why 20 cores cost the same as 1. The quantised-looking values (16.667, 41.667, 33.333) are the ~15.6 ms tick granularity showing through a 200 ms window. The panel maps them to 20 rows at a fixed 12-unit pitch with the P/E split preserved (logical 0-11 = P → rows 1-12, 12-19 = E → rows 13-20). ⚠️ The row count is a **hard-coded `const int Cores = 20`** ([CpuRamPanel.cs:13](../src/Halo.Widgets/PanelDefs/CpuRamPanel.cs#L13)) — the collector publishes `Environment.ProcessorCount` cores, so a different CPU would under- or over-draw. | [cpu-kernel](#p-cpu-kernel) | **~2-435 ms** · counter tick 0-15.6 + poll wait 0-200 + read 0.08 + **T** 2-219 · *all 20 cores land in the same poll* |
| `fan.0.rpm` (CPU fan) | 1012 | rpm | latest | HWiNFO | Channel 0 of the SuperIO fan enumeration — see [§6](#6-fans-panel) for the full chain. Aliased as `MetricNames.CpuFanRpm`. The panel prints `FAN: N/A` when unavailable rather than hiding the row ([CpuRamPanel.cs:108](../src/Halo.Widgets/PanelDefs/CpuRamPanel.cs#L108)). | [lhm-superio](#p-lhm-superio) | same as `fan.<n>.rpm` |
| `ram.used.gb` | 15.369 | GB | latest | Rainmeter `PhysicalMemory` | **1** `GlobalMemoryStatusEx` fills a `MEMORYSTATUSEX`; used = `ullTotalPhys − ullAvailPhys`, ÷ 1073741824 for GiB ([BuiltinProvider.cs:72-80](../src/Halo.Collector/Providers/BuiltinProvider.cs#L72-L80)). Note "available" includes the standby list, so this tracks Task Manager's *In use*, not committed memory. **2** Poll **1 Hz**, cap 4, **not configurable**. **3** → **tail** T1, T3-T5. | [builtin](#p-builtin) | **~3-1220 ms** · poll wait 0-1000 + read 1.1 + **T** 2-219 |
| `ram.total.gb` | 31.725 | GB | latest | Rainmeter `PhysicalMemory` | The same single call, `ullTotalPhys`. Constant, republished every second. | [builtin](#p-builtin) | **~3-1220 ms** · poll wait 0-1000 + read 1.1 + **T** 2-219 · *static* |
| `ram.pct` | 48.445 | % | latest (calc) | derived | Computed **collector-side** in the same poll (`used ÷ total × 100`) so the graph samples one metric. | [builtin](#p-builtin) | **~3-1220 ms** · poll wait 0-1000 + read 1.1 + **T** 2-219 |
| *(CPU/RAM graphs)* | up to 3 series | — | 5 Hz samples of latest / interval avg | same | One `GraphEl`, `SampleRateHz = 5`, series for `cpu.package.temp.c`, `cpu.total.pct`, `ram.pct`, each `FixedMax = 100` ([CpuRamPanel.cs:154-161](../src/Halo.Widgets/PanelDefs/CpuRamPanel.cs#L154-L161)). `graphCpuTemp` is **off** in the current config, leaving usage + RAM. Each point carries the semantics of the metric it sampled — the CPU line is a 5 Hz sample of a 200 ms interval average (so it covers the timeline continuously), while the temp line is a 5 Hz sample of a 5 Hz sensor. | [multiple](#source-providers) → [widget-side](#p-widget) | same as the metric each series samples (CPU ~2-435 ms, RAM ~3-1220 ms) |

<a id="9-top-cpu-panel"></a>
## 9. TOP CPU panel

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `proc.count` | 314 | count | latest | UsageMonitor | **1** One `NtQuerySystemInformation(SystemProcessInformation)` into a reusable 1 MB buffer that **doubles on `STATUS_INFO_LENGTH_MISMATCH`** rather than reallocating per poll ([ProcessProvider.cs:61-71](../src/Halo.Collector/Providers/ProcessProvider.cs#L61-L71)). Works unelevated. **2** The linked chain is walked with **hard-coded 64-bit struct offsets** (pid 0x50, user 0x28, kernel 0x30, private working set 0x08, name UNICODE_STRING 0x38) plus a corrupt-chain guard. **3** PID 0 (Idle) is skipped, matching the perf-counter "Processes" definition. **4** Poll **1 Hz** (`DefaultRateHz = 1`, cap **2**, [ProcessProvider.cs:17-18](../src/Halo.Collector/Providers/ProcessProvider.cs#L17-L18)). **Not configurable** — the snapshot is the most allocation-heavy poll in the collector (a ~400-entry `List<Proc>` plus two LINQ sorts per ranking), and a top-5 list is unreadable above ~1 Hz anyway. **5** → **tail** T1, T3-T5. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 |
| `proc.topcpu.<0-4>.name` | "Discord" | — | latest | UsageMonitor | **1** The same snapshot. `.exe` is stripped and two perf-counter-style renames applied (`MemCompression` → `Memory Compression`, empty → `System Idle`) ([ProcessProvider.cs:27-32](../src/Halo.Collector/Providers/ProcessProvider.cs#L27-L32)). **2** Sorted by CPU%, top 5 taken, written to 5 seqlock string slots. **3** 1 Hz → **tail**. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 |
| `proc.topcpu.<0-4>.cpu.pct` | 1.014 | % | interval avg (~1 s) | UsageMonitor | **1** Per-PID `user + kernel` 100 ns time, differenced against the previous snapshot held in a `Dictionary<pid, long>`. **2** The denominator is `wallSeconds × 1e7 × ProcessorCount` ([ProcessProvider.cs:72-74](../src/Halo.Collector/Providers/ProcessProvider.cs#L72-L74)) — so this is **percent of the whole 20-thread machine**, matching Task Manager, *not* percent of one core. A fully pegged single thread reads 5%, not 100%. **3** `wallSeconds` is a measured QPC delta, so a late poll self-corrects. **4** 1 Hz, not configurable. **5** → **tail**. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 · *value is a ~1 s delta across two snapshots* |
| `proc.topcpu.<0-4>.ram.b` | 449273856 | B | latest | UsageMonitor | Private working set (offset 0x08) of the same process — deliberately **not** the full working set at 0x90, which double-counts shared pages; this matches UsageMonitor's `Alias=RAM` ([ProcessProvider.cs:89-91](../src/Halo.Collector/Providers/ProcessProvider.cs#L89-L91)). Same snapshot, same 1 Hz. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 |
| `proc.topcpu.agg.<0-4>.*` | "Discord" 1.404% | — | same as above | — | **A second, parallel ranking** published from the same snapshot: processes grouped by name, CPU% and working set **summed** ([ProcessProvider.cs:123-127](../src/Halo.Collector/Providers/ProcessProvider.cs#L123-L127)). Task-Manager style. Both rankings are always published; each Top widget picks one via its `aggregate` option. This is why `Discord` appears as 1.014% per-instance and 1.404% aggregated in the same dump. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 · *same snapshot as the per-instance ranking* |

## 10. TOP RAM panel

| Metric | Example | Unit | Value semantics | Old source | Halo source | Provider | Est. latency |
|---|---|---|---|---|---|---|---|
| `proc.topram.<0-4>.name` | "Memory Compression" | — | latest | UsageMonitor | The same snapshot as [§9](#9-top-cpu-panel), sorted by working set instead of CPU%. `topram-1` has `aggregate = true` in [widgets.json](../config/widgets.json), so it actually renders the `proc.topram.agg.*` slots — where the top entry is `"Code"` at 1.47 GB rather than `"Memory Compression"` at 495 MB. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 |
| `proc.topram.<0-4>.ram.b` | 494981120 | B | latest | UsageMonitor | Private working set, same field and same 1 Hz snapshot as §9. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 |
| `proc.topram.<0-4>.cpu.pct` | 0 | % | interval avg (~1 s) | UsageMonitor | The same whole-machine CPU% as §9, carried along so the RAM panel can show a CPU column without a second query. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 · *~1 s delta* |
| `proc.topram.agg.<0-4>.*` | "Code" 1469644800 | — | same | — | Name-aggregated ranking, summed working sets — the variant this panel actually displays today. | [process](#p-process) | **~7-1224 ms** · poll wait 0-1000 + snapshot 5.3 + **T** 2-219 |

---

<a id="source-providers"></a>
## Source Providers

Every provider runs on its **own thread** at `min(requested, MaxRateHz)`, with init backoff
1/5/30/60 s and a forced re-init after 10 consecutive poll failures; one provider crashing never
touches another ([ProviderHost.cs:55-137](../src/Halo.Collector/ProviderHost.cs#L55-L137)).
"Configurable" below means a `config\settings.json` key changes it at runtime — the `ConfigStore`
file watcher hot-reloads and providers read `config.Settings` live, so no restart is needed.

| Name | Installation source | OS support | Model support | Poll / push | Value semantics | Rate · cap · worth raising? |
|---|---|---|---|---|---|---|
| <a id="p-builtin"></a>**builtin** | **Inherent to Windows** — `kernel32` (`GlobalMemoryStatusEx`, `GetDiskFreeSpaceExW`, `GetVolumeInformationW`) plus .NET BCL sockets. The external-IP step is the one exception: an HTTPS GET to a **third-party service** (`api.ipify.org`, configurable). | Win7+ in principle; targets **Win10/11 x64**. No elevation. | **Vendor-agnostic** — no hardware dependency at all. Any CPU, any drive, any NIC. | poll | latest | **Now 1 Hz** · cap **4 Hz** ([BuiltinProvider.cs:18](../src/Halo.Collector/Providers/BuiltinProvider.cs#L18)) · **wall occupancy 0.11% → 0.43%** of one core (wall occupancy only — this provider was not in the isolated benchmark, so its CPU share is unmeasured). **Not worth it** — uptime advances 1 s per second, and RAM, free space and volume labels don't move meaningfully sub-second. The external IP is a separate 5-minute cadence and is the one thing here you'd ever want faster. |
| <a id="p-cpu-kernel"></a>**cpu-kernel** | **Inherent to Windows** — `ntdll!NtQuerySystemInformation`, info class 8. Undocumented but stable since NT. | **Win10/11 x64.** No elevation. | **Vendor- and count-agnostic** — sizes itself off `Environment.ProcessorCount`. ⚠️ The *display* side hard-codes 20 rows and the 14600K's P/E split ([CpuRamPanel.cs:13](../src/Halo.Widgets/PanelDefs/CpuRamPanel.cs#L13)); the collector does not. | poll | interval avg | **Now 5 Hz** · cap **64 Hz** ([CpuKernelProvider.cs:14](../src/Halo.Collector/Providers/CpuKernelProvider.cs#L14)) · **CPU 0.003% now → 0.03% at cap**; wall occupancy 0.002% → 0.03% (measured 2026-09-13, see [Provider cost measurement](#provider-cost-measurement)). **The best headroom in the collector.** Counters advance on the ~15.6 ms kernel tick, so every step up to 64 Hz is genuinely new data — and at 0.08 ms it is the cheapest poll measured. At 5 Hz each value is a delta across ~13 ticks, which is why the numbers land on multiples of 8.33%. |
| <a id="p-process"></a>**process** | **Inherent to Windows** — `ntdll!NtQuerySystemInformation`, info class 5. | **Win10/11 x64 only** — the 64-bit `SYSTEM_PROCESS_INFORMATION` field offsets are hard-coded ([ProcessProvider.cs:84-93](../src/Halo.Collector/Providers/ProcessProvider.cs#L84-L93)); a layout change in a future Windows would break it silently. No elevation (protected processes just read as low usage). | **Vendor-agnostic.** | poll | interval avg (CPU%), latest (RAM) | **Now 1 Hz** · cap **2 Hz** ([ProcessProvider.cs:17](../src/Halo.Collector/Providers/ProcessProvider.cs#L17)) · **wall occupancy 0.53% → 1.07%** of one core (wall occupancy only — this provider was not in the isolated benchmark, so its CPU share is unmeasured). **Marginal.** The process table changes continuously, so there is real data to be had, but CPU% needs a delta window to mean anything and a top-5 list isn't readable faster than ~1 Hz. It is also the heaviest allocating poll in the collector (~400-entry list + four LINQ sorts). |
| <a id="p-disk-io"></a>**disk-io** | **Inherent to Windows** — `pdh.dll`, LogicalDisk counter set. | **Win2000+**; locale-safe via `PdhAddEnglishCounterW`. No elevation. | **Vendor-agnostic** — any volume Windows exposes as a LogicalDisk (SATA, NVMe, USB, network-mapped). | poll | interval avg | **Now 5 Hz** · cap **64 Hz** ([DiskIoProvider.cs:22](../src/Halo.Collector/Providers/DiskIoProvider.cs#L22)) · **CPU 0.10% now → 1.29% at cap**; wall occupancy 0.10% → 1.27% (measured 2026-09-13, see [Provider cost measurement](#provider-cost-measurement)). Essentially all CPU (102% of wall). **Meaningful and cheap.** The raw counters update per I/O completion (measured — see [Counter-granularity measurement](#counter-granularity-measurement)), so the poll rate sets the resolution: at 5 Hz a 50 ms burst is smeared across 200 ms and under-reported ~4×. 10 Hz costs **+0.10 points**. |
| <a id="p-network"></a>**network** | **Inherent to Windows** — .NET `System.Net.NetworkInformation` over the IP Helper API. | **Win10/11.** No elevation. | **Vendor-agnostic** — any adapter that is Up and is not loopback/tunnel. | poll | interval avg (rates), cumulative (totals), running max (peaks) | **Now 5 Hz** · cap **64 Hz** ([NetworkProvider.cs:21](../src/Halo.Collector/Providers/NetworkProvider.cs#L21)) · **CPU 0.06% now → 0.75% at cap**; wall occupancy 0.10% → 1.30% (measured 2026-09-13, see [Provider cost measurement](#provider-cost-measurement)). **Meaningful and cheap**, same as disk-io — octet counters update per packet batch (measured), so short bursts are under-reported in proportion to the period. 10 Hz costs **+0.08 points**. |
| <a id="p-nvml"></a>**nvml** | **Ships with the NVIDIA display driver** (`nvml.dll`), not bundled by Halo. Loaded by name via P/Invoke. | **Windows with an NVIDIA driver installed.** No elevation. | **NVIDIA only**, and only **device index 0** ([NvmlProvider.cs:48](../src/Halo.Collector/Providers/NvmlProvider.cs#L48)) — the second GPU on this machine is invisible. AMD/Intel GPUs need a replacement module (plan §13). `nvmlDeviceGetFanSpeedRPM` is **newer-driver only** and probed at init. The power sanity ceiling is derived from the card's own limit, so it scales to any NVIDIA card. | poll | latest | **Now 5 Hz** · cap **20 Hz** ([NvmlProvider.cs:16](../src/Halo.Collector/Providers/NvmlProvider.cs#L16)) · **CPU 0.16% now → 0.63% at cap**; wall occupancy 0.18% → 0.70% (measured 2026-09-13, see [Provider cost measurement](#provider-cost-measurement)). **Partly worth it.** Temp, clocks and power are read on demand and would get fresher; `gpu.usage.pct` would not — NVML computes it inside its own sampling window, so polling faster re-reads the same figure. Cap is 20 because one poll issues 8 calls at ~0.2-1 ms each. |
| <a id="p-lhm-cpu"></a>**lhm-cpu** | **Downloaded package** — `LibreHardwareMonitorLib` **0.9.6** NuGet (MPL-2.0), restored into the project-local cache `tools\nuget-cache` ([Halo.Collector.csproj:19](../src/Halo.Collector/Halo.Collector.csproj#L19)). Needs a **ring0 driver** (PawnIO / WinRing0) for MSR access — PawnIO at `C:\Program Files\PawnIO` belongs to FanControl and must never be uninstalled with Halo. | **Win10/11 x64. Requires elevation** — unelevated, `Initialize` finds no hardware, logs a warning and the host retries with backoff; every CPU metric stays N/A and the panel renders "N/A". | **Intel and AMD.** The temperature match accepts `CPU Package` (Intel) or `Core (Tctl/Tdie)` (AMD), and the clock logic is a name-agnostic max-of-non-bus, so both vendors work. Verified on **i5-14600K (Raptor Lake)**. | poll | latest | **Now 5 Hz** · cap **20 Hz** ([LhmProvider.cs:33](../src/Halo.Collector/Providers/LhmProvider.cs#L33)) · **CPU only 1.22% now → 4.89% at cap** — but **wall occupancy 108% now → 433% at cap** (measured 2026-09-13, see [Provider cost measurement](#provider-cost-measurement)). **Don't — and the reason is occupancy, not CPU.** Just **1.1% of its wall time is CPU**; the rest is waiting on ring0 round-trips. But wall time is what blocks the period: the collector logs a 26.4 ms median with a p99 of **153 ms**, so at 10 Hz **2.78% of polls overrun their 100 ms period** and the thread re-polls immediately ([ProviderHost.cs:127-133](../src/Halo.Collector/ProviderHost.cs#L127-L133) doesn't catch up). ⚠️ Its wall cost is also **machine-state dependent**: 216 ms/sweep measured on an idle machine vs 24-31 ms in the live collector. The cap is nominally 20 Hz but unreachable in practice. |
| <a id="p-lhm-superio"></a>**lhm-superio** | The same **LibreHardwareMonitorLib 0.9.6** package; talks to the SuperIO chip over **ISA port I/O** behind a global mutex that FanControl also contends for. | **Win10/11 x64. Requires elevation.** | Whatever SuperIO chip LHM has a driver for. This board: **MSI NCT6687D**, verified working (the one pre-build unknown). Channel numbers are positional indexes into LHM's enumeration, **not** board header numbers — which is exactly why `fanNames` / `fanMaxRpm` exist as per-board config. | poll | latest | **Now 1 Hz** · cap **2 Hz** ([LhmProvider.cs:34](../src/Halo.Collector/Providers/LhmProvider.cs#L34)) · **wall occupancy 0.27% → 0.53%** of one core (wall occupancy only — this provider was not in the isolated benchmark, so its CPU share is unmeasured). **Cheap, but the gain is unknown** — a fan tachometer needs at least one revolution (~30-60 ms at 1000-2000 rpm) and the NCT6687D's own register scan cadence has never been measured. The real cost isn't CPU, it's the global ISA mutex shared with FanControl. |
| <a id="p-lhm-storage"></a>**lhm-storage** | The same **LibreHardwareMonitorLib 0.9.6** package; SMART / NVMe identify IOCTLs. | **Win10/11 x64. Requires elevation.** | Any SATA or NVMe drive that reports a temperature **and** a non-empty vendor/product descriptor — drives with blank descriptors cannot be mapped to a volume letter, log a warning once, and read N/A. | poll | latest | **Now 1/30 Hz (every 30 s)** · cap **0.2 Hz (every 5 s)** ([LhmProvider.cs:35](../src/Halo.Collector/Providers/LhmProvider.cs#L35)) · **CPU only 0.15% now → 0.87% at cap**; wall occupancy 0.85% → 5.10% (measured 2026-09-13, see [Provider cost measurement](#provider-cost-measurement)) — just **17% of the sweep is CPU**. **Arguably the one that's under-polled.** The 30 s was chosen on wall cost (32 ms per drive × 8 = 255 ms measured, matching the original estimate), not on how fast a drive updates its temperature — which nobody has established. An NVMe drive can climb to its throttle point in well under 30 s, so the ramp is likely being missed. **10 s costs only +0.23 CPU points** and per-sweep cost is identical at both spacings. |
| <a id="p-lhm-gpu"></a>**lhm-gpu** | The same **LibreHardwareMonitorLib 0.9.6** package → **NVAPI**. Exists only to cover what NVML cannot: GPU voltage has no public NVML API. | **Win10/11. Works unelevated** (unlike the other three LHM parts). | **NVIDIA only** — filtered on `HardwareType.GpuNvidia` ([LhmProvider.cs:262](../src/Halo.Collector/Providers/LhmProvider.cs#L262)); AMD/Intel would need the filter widened. First NVIDIA GPU only. | poll | latest | **Now 1 Hz** · cap **2 Hz** ([LhmProvider.cs:36](../src/Halo.Collector/Providers/LhmProvider.cs#L36)) · **CPU 2.22% now → 4.45% at cap**; wall occupancy 7.77% → 15.4% (measured 2026-09-13, see [Provider cost measurement](#provider-cost-measurement)). **Still the worst value in the collector.** A ~78 ms NVAPI sweep (29% of it CPU) yields two metrics, and `gpu.fan.rpm` is already published by NVML at 5 Hz on this driver — so 2 Hz would cost **+1.65 CPU points** to refresh GPU voltage alone. That is still more than `cpu-kernel`, `disk-io`, `network` and `nvml` combined (+0.58). The interesting move here is *slowing it down*. |
| <a id="p-presentmon"></a>**presentmon** (resolved lane) | **Bundled download** — Intel **PresentMon 2** service + `PresentMonAPI2.dll` under `tools\presentmon\sdk\` (MIT, redistribution allowed), spawned as a **console-mode child**, never SCM-registered. The console capture app is the fallback transport. | **Win10 1709+** (ETW present tracking). **Elevation to own the ETW session**; attaching to an already-installed running PresentMon service works unelevated. | **GPU-vendor agnostic and graphics-API agnostic** — it observes the DXGK/present pipeline, so DX9/11/12, Vulkan and OpenGL all resolve. This is why it is the fallback lane for titles the tap cannot see. | poll (drain) | rolling 1 s (fps, worst), rolling 100 ms (frametime mean), rolling 60 s (lows), decaying avg (latencies) | **Now 40 Hz** · cap **120 Hz** ([PresentMonProvider.cs:29-30](../src/Halo.Collector/Providers/PresentMonProvider.cs#L29-L30)) · **wall occupancy 0.72% → 2.16%** of one core (wall occupancy only — this provider was not in the isolated benchmark, so its CPU share is unmeasured). **No gain — it is a drain cadence, not a sample rate.** Frames carry their own `PRESENT_START_QPC`, so draining faster just splits the same frames into smaller batches. What actually moves the latency is `presentMonEtwFlushMs` (10 ms). Lows recompute at 2 Hz behind their own cache. |
| <a id="p-tap"></a>**present-tap** (door-1 lane) | **No download** — Windows **inbox ETW providers** `Microsoft-Windows-DXGI` and `Microsoft-Windows-Direct3D9`, consumed through the `Microsoft.Diagnostics.Tracing.TraceEvent` **3.2.5** NuGet ([Halo.Collector.csproj:21](../src/Halo.Collector/Halo.Collector.csproj#L21)). Owned by the presentmon provider, not a separate `ProviderHost` entry. | **Win10/11. Requires admin** (owns a real-time ETW session). Silently skipped when unelevated — the presented panel just rides the resolved lane. | **GPU-vendor agnostic but API-limited**: DXGI (D3D10/11/12) and D3D9(Ex) only. **Vulkan and OpenGL titles emit no runtime present event** and fall back to the resolved lane (`fps.tap.active` = 0). | **push** (per present) | rolling 1 s (fps, lows, worst), rolling 100 ms (frametime mean), raw per-frame (graph) | **Push — already per present**, there is no rate to raise. The only knob is the manual ETW flush, **`presentMonEtwFlushMs` = 10 ms** (clamped 1-100, [PresentTap.cs:91-105](../src/Halo.Collector/Providers/PresentTap.cs#L91-L105)); lowering it cuts the 0-11 ms buffer dwell but costs a kernel buffer sweep per flush — it ran 200 sweeps/s at 5 ms. Relaxes to 250 ms when idle. |
| <a id="p-pclstats"></a>**pclstats** | **No download** — the markers are emitted by the **game's own NVIDIA Reflex SDK**; Halo just enables `PCLStatsTraceLoggingProvider` (GUID `0d216f06-…` taken literally from NVIDIA's reference `pclstats.h`, MIT) and consumes it via the TraceEvent NuGet. | **Win10/11. Requires admin.** Returns false from `Initialize` unelevated and the host retries with backoff ([PclStatsProvider.cs:64](../src/Halo.Collector/Providers/PclStatsProvider.cs#L64)). | **Reflex-instrumented games only.** Nothing about it is GPU-specific — it reads game instrumentation, not hardware — but Reflex ships with NVIDIA titles in practice. No markers → the three metrics stay stale and the panel dims. No game-specific integration and no NVIDIA App needed. | **push** (ETW markers) → periodic publish | rolling 1.5 s avg (queue, render), rolling ≥0.5 s (render rate) | **Now 5 Hz** · cap **20 Hz** ([PclStatsProvider.cs:28-29](../src/Halo.Collector/Providers/PclStatsProvider.cs#L28-L29)) · **wall occupancy ~0.00% → ~0.01%** of one core (wall occupancy only — this provider was not in the isolated benchmark, so its CPU share is unmeasured). **Free but pointless.** The poll does no syscall at all (0.00 ms measured) — but the markers it averages arrive at only ~5-10 pings/s, and the values are 1.5 s rolling means, so a faster publish republishes the same number. |
| <a id="p-max"></a>**`.max` session maxima** | **Internal** — the `MetricSink` running-max latch plus the `Halo.Control.v1` named pipe (`reset-max`). | Any; pure arithmetic. | N/A. | calc (inline with the base metric's `Set`) | running max | **No rate of its own** — the max compare runs inline inside the base metric's `Set`, so it is exactly as fast as whatever it tracks and costs one predicted branch. Nothing to raise. |
| <a id="p-widget"></a>**widget-side** | **Internal** — `Halo.Widgets`: the system clock, plus arithmetic over metrics already in shared memory. | Win10/11 x64 (DirectComposition + `WS_EX_NOREDIRECTIONBITMAP`). | N/A. | pull (per tick) / event (frame graphs) | latest (clock), calc (derived), 5 Hz samples (graphs), raw per-frame (frame graphs) | **Text now 5 Hz** · cap **100 Hz** ([WidgetWindow.cs:30](../src/Halo.Widgets/WidgetWindow.cs#L30)) · **CPU 1.9% → ~38%** of one core — this one really is CPU, measured process-wide at idle ([perf-usage-breakdown.md:103](perf-usage-breakdown.md#L103)), tick-driven portion. **This is the expensive half.** Raising it is the only way extra collector resolution reaches the screen — sampled graphs draw one point per tick regardless of their own `SampleRateHz`, so nothing beats 5 points/s today. Frame graphs are exempt: event-driven, coalesced to 16 ms. |

---

<a id="provider-cost-measurement"></a>
## Provider cost measurement (2026-09-13)

**Why this exists.** Every cost figure in this doc used to be `median lastPoll × rate`, and `lastPoll`
is a `Stopwatch` reading ([ProviderHost.cs:122](../src/Halo.Collector/ProviderHost.cs#L122)) — i.e.
**wall time**. For providers whose polls mostly *wait* on hardware, that massively overstates CPU.
This was measured properly and the two are now reported separately throughout.

**Method.** A standalone harness replicated each provider's exact per-poll work — the same
`NtQuerySystemInformation` class, the same 24 PDH counters, the same 8 NVML calls, the same LHM
`Computer` parts and `Update()` sweep — and ran each at its current rate and then at a proposed
higher rate. CPU was taken with `QueryThreadCycleTime` (calibrated at 3.46M cycles per CPU-ms, so
microsecond resolution instead of the 15.6 ms `GetThreadTimes` tick); I/O with
`GetProcessIoCounters`. **Halo was fully stopped** so nothing contended.

### Per sweep

| Provider | wall ms | **CPU ms** | **CPU as % of wall** | IO ops | IO bytes |
|---|---|---|---|---|---|
| `cpu-kernel` | 0.004 | 0.005 | — *(4 µs, below resolution)* | 0 | 0 |
| `network` | 0.203 | 0.117 | 58% | 3 | 264 |
| `disk-io` | 0.199 | 0.202 | 102% | 1 | 6,616 |
| `nvml` | 0.350 | 0.316 | 90% | 0 | 0 |
| `lhm-gpu` | 77.7 | 22.2 | **29%** | 4 | 0 |
| `lhm-storage` | 255.0 | 43.6 | **17%** | 1,194 | 51,668 |
| `lhm-cpu` | 216.6 | 2.4 | **1.1%** | 48 | 384 |

### Cost of raising the rate

| Provider | Change | **CPU Δ** (points of one core) | IO ops Δ | IO bytes Δ |
|---|---|---|---|---|
| `cpu-kernel` | 5 → 10 Hz | **+0.001** | 0 | 0 |
| `network` | 5 → 10 Hz | **+0.037** | +15/s | +1.3 KB/s |
| `lhm-storage` | 30 s → 10 s | **+0.233** | +80/s | +3.4 KB/s |
| `disk-io` | 5 → 10 Hz | **+0.255** | +5/s | +33 KB/s |
| `nvml` | 5 → 10 Hz | **+0.283** | 0 | 0 |
| `lhm-gpu` | 1 → 2 Hz | **+1.652** | +4/s | 0 |
| `lhm-cpu` | 5 → 10 Hz | **+1.759** | +240/s | +1.9 KB/s |
| **all seven** | | **+4.22 points of one core** = **+0.21% of the 20-thread machine** | +344/s | +39 KB/s |

### What this changes

- **The LHM parts are not CPU-expensive, they are *occupancy*-expensive.** `lhm-cpu` spends 1.1% of
  its poll computing. The old "13.21% of a core" was thread occupancy; real CPU is 1.22%.
- **Occupancy still matters** — it is what blocks a provider's period and causes overruns. The
  argument against `lhm-cpu` at 10 Hz stands unchanged, just for the right reason.
- ⚠️ **`lhm-cpu` wall time is machine-state dependent.** 216 ms/sweep here (idle machine, Halo
  stopped) versus 24–31 ms in the live collector, with CPU ~2.4 ms in both. Hypothesis —
  **unverified** — is core-wake latency: LHM reads per-core MSRs by switching thread affinity across
  all 20 logical CPUs, and on an idle machine those cores sit in deep C-states. If so, overrun risk
  is *worse* when the machine is idle, which also explains the 2.78% of collector polls over 100 ms.

### Drives: SMART polling does not wake them

This machine has **three spinning HDDs** — E `TOSHIBA DT01ACA050`, G `WDC WD10PURZ-85U8XY0`,
I `HGST HTS541010A9E680` — alongside four SSDs (C, D, J NVMe; F SATA). Windows' spin-down timeout
is **20 min on AC** (`DISKIDLE` = 0x4b0), 10 min on DC.

All 11 measured sweeps completed in **218–277 ms**, at both 30 s and 10 s spacing, with no
multi-second outliers. A 3.5" HDD spin-up takes 5–10 s, so **no drive is being woken by the poll** —
at either interval. Per-sweep cost was also identical at both spacings, so nothing is being cached
or woken differently.

**Open question:** whether the SMART IOCTL resets Windows' disk idle timer. If it does, a 30 s poll
against a 20-minute timeout means those three HDDs never park at all. Unverified — checking
`StartStopCycleCount` against `PowerOnHours` (elevated) would settle it. Note that no polling rate
within the 0.2 Hz cap would change this either way.

---

<a id="counter-granularity-measurement"></a>
## Counter-granularity measurement (2026-09-13)

Two provider comments claimed their source counters updated too slowly for the poll rate to
matter. Both were assumptions, never measured, and **both are wrong.** Recorded here so nobody
re-derives them.

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
- **The poll rate sets the resolution.** At 5 Hz a 50 ms burst is smeared across 200 ms and
  under-reported roughly 4×; at 10 Hz it would be smeared across 100 ms.
- Raising either to 10 Hz costs **+0.10%** (disk-io) and **+0.08%** (network) of one core — but
  the widget graph still samples at 5 Hz on the widget tick, so half the extra values would never
  be drawn unless the widget tick rises too.

**Still unverified** for other providers: how stale an NVML reading, a SMART temperature or a
SuperIO fan register already is before Halo reads it.

## Published but never displayed

Registered in shared memory, costing a slot and a write each poll, with no panel reading them:

| Metric(s) | Count | Why it exists |
|---|---|---|
| `drive.<x>.activity.pct` | 8 | `% Disk Time` was collected alongside read/write when the PDH query was built; no panel row was ever designed for it. |
| `fan.<n>.name` | 8 | LHM's own sensor names. The FANS panel shows `fanNames` nicknames instead. Genuinely useful in `--dump` for working out which channel is which header. |
| `fps.app.pid` | 1 | Published for diagnostics; the panel shows `fps.app.name` only. |
| `cpu.name` | 1 (conditional) | Only used when a widget has no `title` option; `cpu-ram-1` sets one. |

Config keys with no consumer: **`graphHistoryS`** — read and written by the Settings UI
([GeneralPage.xaml.cs:56](../src/Halo.Settings/Pages/GeneralPage.xaml.cs#L56)) but never read by
`GraphEl` or anything else.

---

## Changes since the 2026-07-19 audit

| Was | Now |
|---|---|
| 7 volumes (C-I) | **8** (C-J) — `driveLetters` in [settings.json](../config/settings.json) |
| Graphs sample at **1 Hz** (2 Hz for the PCL sparkline) | **5 Hz everywhere**, commit `b783a43` |
| Frame-graph coalesce **7 ms** | **16 ms**, commit `e9c6164` — sized for the 60 Hz widget monitor, not a 144 Hz display |
| Fan channels **4 / 5** = BACK / FRONT | **2 / 3**; `fanMaxRpm` now also sets channel 0 (CPU fan) to 1600 |
| `drive.<x>.activity.pct` not listed | Exists, published for all 8 volumes, **unused** |
| GPU power taken as-is | **Sanity-gated at 4× the card's own limit**, commit `781ea97` — an S3 resume once published 371,940 W and poisoned `gpu.power.w.max` |
| Drive list required a collector restart | **Hot-reloaded** across builtin / disk-io / lhm-storage, commit `a559eba` |
| ⚠️ "Known cost hotspot: lhm-cpu poll ~240 ms, ≈1 core busy" | **Retired.** The saturation did not reproduce under measurement — no collector thread exceeded 0.5% of a core ([perf-usage-breakdown.md](perf-usage-breakdown.md)). Still a watch item in the `status:` log lines; possibly intermittent under SuperIO mutex contention with FanControl. |
| Both FPS panels enabled | `fps-displayed` **disabled** in [widgets.json](../config/widgets.json) — worth ~9 points of widget CPU, commit `6c844f7` |
| — | `topram-1` now runs with `aggregate = true`, so it displays the `proc.topram.agg.*` ranking |
| ⚠️ All provider costs quoted as `lastPoll x rate` | **That is wall-clock thread occupancy, not CPU** — measured 2026-09-13. `lhm-cpu` spends 1.1% of its poll computing, `lhm-storage` 17%, `lhm-gpu` 29%; the PDH/NVML/kernel providers really are 90-100% CPU. Both figures are now reported separately; see [Provider cost measurement](#provider-cost-measurement). |
| ⚠️ "SMART reads can spin up or stall a sleeping drive" | **Unsourced and contradicted by measurement** — 11 sweeps at 30 s and 10 s spacing all ran 218-277 ms, far too fast for the 5-10 s spin-up of the three HDDs (E, G, I) on this machine. Claim removed. |
| ⚠️ disk-io and network annotated "~1 s counters / driver-paced, faster polling measures jitter" | **Both measured wrong 2026-09-13** — counters update per I/O and per packet batch. The poll rate really does set the resolution; see [Counter-granularity measurement](#counter-granularity-measurement). The `DiskIoProvider` code comment has been corrected and `NetworkProvider` now records the measurement. |

**All 10 panel types are built and the old stack is fully retired** — HWiNFO, MSI Afterburner, RTSS
and the NVIDIA App overlay are all replaced. 221 metrics registered, 159 on screen as configured
(8 drives × 6 and 20 core rows dominate the count).
