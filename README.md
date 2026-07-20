# Halo — Hardware Analytics & Live Overlay

A native Windows 11 desktop widget suite that renders a live hardware dashboard directly on
the wallpaper — CPU, GPU, RAM, drives, fans, power, network, latency, and a real-time FPS/
frametime counter. It was built to replace a stack of **Rainmeter + Rainformer + HWiNFO +
MSI Afterburner + RTSS + the NVIDIA App** with a single, self-contained, low-overhead app.

![Halo widget suite](preview.png)

---

## ⚠️ This repo is a reference, not a product

**This is an architecture reference, not a maintainable or reusable application.** The build is
hard-wired to one specific machine (the author's PC): sensor mappings, core counts, drive
letters, fan channels, monitor layout, panel geometry, and theme values are all tuned to that
exact hardware. It is published so others can read how the pieces fit together — a lock-free
shared-memory metrics bus, DirectComposition per-widget windows, an ETW/PresentMon frame
pipeline, graceful degradation without elevation — **not** so it can be cloned and run.

Expect it to *not* work out of the box on your hardware. There is no configuration abstraction
layer, no installer for arbitrary systems, and no support. Read it for the ideas; don't depend
on it.

Third-party binaries (Intel PresentMon redistributables) are **not** included — only the P/Invoke
header and license are kept for reference — so the collector will not build/run as-is.

---

## What it looks like

Every panel above is an independent, always-on-top-or-desktop-glued widget:

- **CPU & RAM** — package temp, per-core load (20 cores), clocks, RAM usage, overlay graph
- **GPU** — temp, load, VRAM, fan, clocks, overlay graph
- **Drives** — per-volume temp / used / total / activity, read & write history graphs
- **FPS counter** — presented & displayed lanes, 1%/0.1% lows, frametime mean + worst, live graph
- **Latency & DLSS** — PC latency breakdown, DLSS/frame-gen state
- **Power / Fans / Network / Top processes** — draw, RPMs, throughput, per-process CPU/RAM

Graph lines are individually toggleable per widget, and non-FPS graphs sample at 5 Hz to match
the widget repaint rate.

## Architecture (three processes)

```
┌─────────────────┐   shared memory        ┌──────────────────┐
│ Halo.Collector  │  Local\Halo.Metrics.v1 │  Halo.Widgets    │
│ (sensors, ETW)  │ ─────────────────────► │  (DComp + D2D)   │
│  elevated       │   named-pipe control   │  one HWND/widget │
└─────────────────┘ ◄───────────────────── └──────────────────┘
        ▲                                            ▲
        │            config\*.json (hot-reload)      │
        └───────────────┬────────────────────────────┘
                        │
                ┌───────────────┐
                │ Halo.Settings │  WPF editor
                └───────────────┘
```

- **Halo.Collector** — polls `ISensorProvider` implementations on per-class cadences and publishes
  to a shared-memory block (`Local\Halo.Metrics.v1`) via a lock-free writer: atomic 8-byte value
  slots, seqlock-protected strings, and an append-only frame ring. Elevated at install for full
  sensor access; degrades gracefully (marks metrics N/A) when unelevated. Control channel is a
  named pipe.
- **Halo.Widgets** — one `WS_EX_NOREDIRECTIONBITMAP` window per widget, drawn with
  DirectComposition + Direct2D on a shared D3D11 device. Panels are element trees laid out in a
  Rainmeter-like flow layout, redrawn dirty-driven. Handles drag/snap, z-order modes,
  click-through, opacity, desktop parenting (WorkerW/Progman), a tray icon, and a watchdog.
- **Halo.Settings** — a WPF configuration editor. All three processes hot-reload `config\*.json`
  via a file watcher.

### Frame pipeline (FPS/frametime)

Two lanes feed the counters:

- **Resolved lane** — a bundled PresentMon 2 service (console-mode child, no SCM registration)
  read through `PresentMonAPI2.dll`; ETW-flushed, fate-resolved, frame-generation aware. Feeds the
  DISPLAYED panel and all fate-dependent metrics. Idle-aware: it winds down after ~10 s without
  frames and re-arms in ~150 ms.
- **Tap lane** — an independent ETW session on the DXGI/D3D9 present-start events (no fate wait),
  feeding the PRESENTED panel live. Vulkan/OpenGL titles fall back to the resolved lane.

Widgets repaint frame graphs on a shared `FramesReady` event (coalesced to the 60 Hz monitor).

## Tech

- .NET 9 (`net9.0` / `net9.0-windows`), C#
- Direct3D 11 / Direct2D / DirectComposition via [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) for sensors
- [Intel PresentMon](https://github.com/GameTechDev/PresentMon) for frame data (not vendored)
- WPF for the settings editor

## Layout

```
src/Halo.Collector   sensor providers, shared-memory writer, frame pipeline
src/Halo.Widgets     rendering engine, panel definitions, window management
src/Halo.Settings    WPF config editor
src/Halo.Shared      config models, metric names, shared-memory layout
tools/extracted      faithful JSON transcriptions of the original Rainmeter skins (spec source)
```

## Credits & licensing note

The visual design derives from the **Rainformer** Rainmeter skin: color tokens and panel geometry
were transcribed from its resources as *values only* (no GPL code is vendored here), and three
icon fonts are reused for personal use. This repository carries no license grant for reuse; it is
shared for reference only.
