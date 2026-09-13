# Halo metrics protocol v2

Everything the Halo collector measures is published into one shared-memory section that any
process on the machine can read — no Halo code required, no permission beyond being logged in as
the same user. This document is the contract. It is written from the implementation in
[`src/Halo.Metrics`](../src/Halo.Metrics); if the two ever disagree, the code is right and this
file is a bug.

**The catalogue of metrics** (what each name means, where the number comes from, how fresh it is)
lives in [current-metrics-inventory.md](current-metrics-inventory.md). This file is the layout.

## Quick start

Three ways to consume it, easiest first:

| Way | For |
| --- | --- |
| `Halo.Collector.exe --dump --json` | scripts, one-shot reads, debugging |
| `Halo.Metrics.dll` (.NET, no dependencies) | .NET widgets and tools — `CollectorSession` implements every rule below |
| Map the section yourself | any language; ~60 lines, see the Python example at the end |

## Names

| Object | Name |
| --- | --- |
| Shared section | `Local\Halo.Metrics.v2` |
| Frames-ready event (auto-reset) | `Local\Halo.FramesReady.v2` |
| Control pipe | `\\.\pipe\Halo.Control.v2` |

All three are versioned together. A major-version change means a new section name, so a v1
consumer can never accidentally read a v2 section (or wake on its event).

The section's DACL grants `GENERIC_READ` to Everyone with a medium mandatory label, which is what
lets a normal-integrity process read a section created by the elevated collector.

## Layout

Little-endian, x64. **Every offset and capacity below is also written into the header — read them
from there, never from these numbers.** That is the whole point of the header: a minor version may
move or grow regions, and a reader that trusts the header keeps working.

```
+0        Header            256 B
+256      Provider table     32 × 64 B
+2304     Registry         1024 × 128 B
+133376   Values           1024 × 16 B
+149760   Strings           256 × 72 B
+168192   Frame ring       8192 × 24 B
= 364800 B total
```

### Header (256 B)

| Off | Type | Field | Notes |
| --- | --- | --- | --- |
| 0 | u32 | `magic` | `0x4F4C4148` = "HALO" |
| 4 | u16 | `versionMajor` | 2 — **reject anything else** |
| 6 | u16 | `versionMinor` | 0 — additive only |
| 8 | i64 | `heartbeatQpc` | QPC stamp, refreshed ~1 Hz |
| 16 | i64 | `qpcFrequency` | ticks per second |
| 24 | i32 | `registryCount` | append-only, release-published |
| 28 | i32 | `collectorPid` |  |
| 32 | u64 | `frameCursor` | monotonic count of frames ever written |
| 40 | i64 | `collectorStartQpc` | **changes ⇒ drop every cached name→index** |
| 48 | u32 | `ready` | 1 once the section is usable |
| 52 | u32 | `headerSize` | 256 |
| 56 | u32 | `providersOffset` |  |
| 60 | u32 | `providerCapacity` | 32 |
| 64 | u32 | `providerEntrySize` | 64 |
| 68 | u32 | `registryOffset` |  |
| 72 | u32 | `registryCapacity` | 1024 |
| 76 | u32 | `registryEntrySize` | 128 |
| 80 | u32 | `valuesOffset` |  |
| 84 | u32 | `valueEntrySize` | 16 |
| 88 | u32 | `stringsOffset` |  |
| 92 | u32 | `stringCapacity` | 256 |
| 96 | u32 | `stringEntrySize` | 72 |
| 100 | u32 | `stringValueBytes` | 64 |
| 104 | u32 | `frameRingOffset` |  |
| 108 | u32 | `frameRingCapacity` | 8192 |
| 112 | u32 | `frameEntrySize` | 24 |
| 116 | u32 | `totalSize` | 364800 |
| 120 | byte[16] | `collectorVersion` | UTF-8 semver, NUL-padded |
| 136–255 | — | reserved, zero | minor versions claim from here |

### Registry entry (128 B) — one per metric, index-parallel to Values

| Off | Type | Field | Notes |
| --- | --- | --- | --- |
| 0 | u64 | `idHash` | FNV-1a 64 of the ASCII name |
| 8 | byte[64] | `name` | UTF-8, NUL-padded. Names longer than 63 bytes are **rejected at registration**, never truncated |
| 72 | u8 | `type` | 0 double · 1 string |
| 73 | u8 | `unit` | see enum |
| 74 | u8 | `semantics` | see enum |
| 75 | u8 | `flags` | bit0 has-max-companion · bit1 is-max-companion · bit2 needs-elevation · bit3 derived |
| 76 | u16 | `stringSlot` | index into Strings, `0xFFFF` = none |
| 78 | u16 | `providerIndex` | index into the provider table, `0xFFFF` = none |
| 80 | f32 | `nominalRateHz` | the cadence this number really changes at |
| 84 | f32 | `effectiveRateHz` | live measured rate (starts equal to nominal) |
| 88 | u32 | `windowMs` | averaging window for semantics 1 and 2, else 0 |
| 92–127 | — | reserved |  |

`nominalRateHz` is not always the provider's poll rate: `fps.app.name` is refreshed once a second
inside a 40 Hz drain, `net.ip.external` every few minutes. Use it to bound your own refresh rate —
polling a 1 Hz metric at 10 Hz just burns CPU.

### Value entry (16 B)

| Off | Type | Field |
| --- | --- | --- |
| 0 | f64 | `value` |
| 8 | i64 | `timestampQpc` — **0 means N/A** |

The writer stores the value first and the timestamp second with a release store, so a reader that
loads the timestamp first (acquire) and then the value can never see a torn pair on x64.

### String entry (72 B)

| Off | Type | Field |
| --- | --- | --- |
| 0 | u32 | `seq` — odd while the writer is inside |
| 4 | u32 | `len` — bytes in use |
| 8 | byte[64] | UTF-8 |

Seqlock: read `seq`, bail if odd, copy, re-read `seq`, retry if it changed.

### Provider entry (64 B)

| Off | Type | Field | Notes |
| --- | --- | --- | --- |
| 0 | byte[24] | `name` | e.g. `lhm-superio`; empty = unused slot |
| 24 | u8 | `state` | 0 unavailable · 1 ok · 2 degraded |
| 25 | u8 | `needsElevation` |  |
| 26 | u16 | reserved |  |
| 28 | f32 | `rateHz` | poll cadence |
| 32 | i64 | `lastPollQpc` |  |
| 40 | f32 | `lastPollMs` | duration of the last poll |
| 44 | byte[16] | `lastError` | short code, see below |
| 60–63 | — | reserved |  |

`lastError` codes: `unelevated`, `no-driver` (PawnIO missing), `no-hw`, `no-nvml`, `no-sdk`,
`failed`. Empty when healthy.

### Frame ring entry (24 B)

| Off | Type | Field |
| --- | --- | --- |
| 0 | i64 | `qpc` — present start |
| 8 | f32 | `frametimeMs` — present-to-present |
| 12 | f32 | `displayedFtMs` — display-to-display, 0 if never displayed |
| 16 | u32 | `flags` |
| 20 | u32 | `pid` |

Frame flags: `1` displayed · `2` dropped · `4` application frame · `8` generated (DLSS-G etc.) ·
`16` repeated · `32` provisional (seen at present time by the low-latency tap; its fate is never
revised, so never mix provisional and resolved entries in one graph).

Entries are written, then `frameCursor` is published. Read `[yourCursor, frameCursor)`; if you fell
behind by more than the capacity, the oldest frames are gone — start from `frameCursor - capacity`.

## Enums

```
type:      0 double | 1 string

unit:      0 none · 1 percent · 2 celsius · 3 volts · 4 watts · 5 rpm · 6 MHz · 7 bytes
           8 bytes/s · 9 GB · 10 MB · 11 fps · 12 ms · 13 s · 14 count · 15 Hz · 16 text

semantics: 0 latest        instantaneous read at poll time
           1 intervalAvg   mean over the gap between two polls (Δcounter ÷ Δt)
           2 rollingWindow sliding window; see windowMs
           3 cumulative    since session start (or since a reset command)
           4 runningMax    session extremum, latched until reset-max
           5 calc          arithmetic over other metrics
           6 static        written once at discovery (names, counts, capabilities)

flags:     1 has-max-companion (a "<name>.max" metric exists)
           2 is-max-companion
           4 needs-elevation (N/A unless the collector runs elevated)
           8 derived (computed by the collector, not read from hardware)
```

## The four reader rules

1. **Check magic and major version.** A different major means a different layout. Refuse; do not
   guess.
2. **Watch `collectorStartQpc`.** A restarted collector reuses the same section name and rebuilds
   the registry from scratch, so every cached name→index mapping becomes wrong — and wrong here
   means silently reading a different metric. When the value changes, clear your cache and
   re-resolve. (This cost us a real corruption bug on 2026-07-19.)
3. **`timestampQpc == 0` is N/A**, and that is different from old: a metric with a timestamp has a
   real value, which may simply be stale. Compute the age as
   `(QueryPerformanceCounter() - timestampQpc) / qpcFrequency` and decide for yourself.
4. **Strings need the seqlock retry** above.

Plus one for liveness: the collector is gone if `heartbeatQpc` has not moved for several seconds
*and* `collectorPid` no longer exists. Heartbeat alone can lag on a busy machine.

## Control pipe

`\\.\pipe\Halo.Control.v2`, one-way, newline-delimited UTF-8, fire and forget. Write a line, close.

| Command | Effect |
| --- | --- |
| `reset-max [prefix]` | clear session maxima whose base metric starts with `prefix` (empty = all) |
| `reset-net` | restart the network session counters |
| `rescan` | re-enumerate hardware (GPUs, fans, volumes) |
| `reload-config` | re-read the config files now instead of waiting for the file watcher |
| `ping` | no-op liveness check |

Unknown lines are logged and ignored, so adding commands is backwards-compatible.

## Diagnostics

```powershell
Halo.Collector.exe --dump          # human table: providers, then every metric with value and age
Halo.Collector.exe --dump --json   # the same as JSON
```

The JSON shape:

```jsonc
{
  "header":  { "versionMajor": 2, "versionMinor": 0, "section": "Local\\Halo.Metrics.v2",
               "collectorVersion": "0.1.0", "collectorPid": 1234, "heartbeatAgeS": 0.13,
               "qpcFrequency": 10000000, "metricCount": 246, "totalSize": 364800 },
  "providers": [ { "index": 0, "name": "builtin", "state": "ok", "needsElevation": false,
                   "rateHz": 1, "lastPollMs": 1.98, "lastError": "" } ],
  "metrics":   [ { "name": "gpu.0.temp.c", "type": "double", "unit": "Celsius",
                   "semantics": "Latest", "flags": "None", "provider": "nvml",
                   "nominalHz": 10, "effectiveHz": 10, "value": 50, "ageS": 0.08, "stale": false } ],
  "frames":    { "cursor": 0 }
}
```

String metrics carry `"text"` instead of `"value"`. A metric that has never been written (or is
marked N/A) has `"value": null, "stale": true`.

## Reading it from Python

No dependencies, ~60 lines. Lists every metric with its value and age.

```python
import ctypes, mmap, struct, time
from ctypes import wintypes

FILE_MAP_READ = 0x0004
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenFileMappingW.restype = wintypes.HANDLE
k32.MapViewOfFile.restype = ctypes.c_void_p

h = k32.OpenFileMappingW(FILE_MAP_READ, False, "Local\\Halo.Metrics.v2")
if not h:
    raise SystemExit("collector not running")
base = k32.MapViewOfFile(h, FILE_MAP_READ, 0, 0, 0)

def read(off, size):
    return ctypes.string_at(base + off, size)

magic, major, minor = struct.unpack_from("<IHH", read(0, 8))
assert magic == 0x4F4C4148 and major == 2, "not a Halo v2 section"

hdr = read(0, 256)
(qpc_freq,)      = struct.unpack_from("<q", hdr, 16)
(count,)         = struct.unpack_from("<i", hdr, 24)
(start_qpc,)     = struct.unpack_from("<q", hdr, 40)
reg_off, reg_cap, reg_size = struct.unpack_from("<III", hdr, 68)
val_off, val_size          = struct.unpack_from("<II", hdr, 80)
str_off, str_cap, str_size, str_bytes = struct.unpack_from("<IIII", hdr, 88)

now = ctypes.c_longlong()
ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(now))

for i in range(count):
    e = read(reg_off + i * reg_size, reg_size)
    name = e[8:72].split(b"\0", 1)[0].decode()
    mtype, unit, semantics, flags = e[72], e[73], e[74], e[75]
    (slot,) = struct.unpack_from("<H", e, 76)
    (nominal_hz,) = struct.unpack_from("<f", e, 80)

    value, ts = struct.unpack_from("<dq", read(val_off + i * val_size, val_size))
    if ts == 0:
        print(f"{name:<38} N/A")
        continue
    age = (now.value - ts) / qpc_freq

    if mtype == 1 and slot != 0xFFFF:                      # string: seqlock retry
        for _ in range(8):
            s = read(str_off + slot * str_size, str_size)
            seq1, length = struct.unpack_from("<II", s, 0)
            if seq1 & 1:
                continue
            text = s[8:8 + min(length, str_bytes)].decode("utf-8", "replace")
            (seq2,) = struct.unpack_from("<I", read(str_off + slot * str_size, 4))
            if seq1 == seq2:
                print(f'{name:<38} "{text}"  age={age:.2f}s @{nominal_hz:g}Hz')
                break
    else:
        print(f"{name:<38} {value:>14.3f}  age={age:.2f}s @{nominal_hz:g}Hz")

# Cache name->index if you poll repeatedly, and drop the cache whenever start_qpc changes.
```

## Compatibility promise

- **Same major version = same rules.** Regions may move and capacities may grow; the header tells
  you where everything is. New metrics, new providers, new enum values may appear at any time —
  ignore what you do not recognise.
- **A breaking change gets a new major version and a new section name**, so old consumers see
  "collector not running" instead of garbage.
- Metric *names* are not part of the layout contract, but renaming one is treated as a breaking
  change for consumers and will be called out in the release notes.
