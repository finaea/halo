# Halo — Hardware Analytics & Live Overlay

Native Windows 11 desktop widget suite replacing the Rainmeter + Rainformer + HWiNFO +
Afterburner + RTSS + NVIDIA App stack. Design doc: [native-widget-implementation-plan.md](docs/native-widget-implementation-plan.md).
Docs live in `docs/`; personal/machine-specific notes in `docs/private/` (git-ignored).

## Build & run

```powershell
dotnet build Halo.sln -c Debug            # all projects (SDK pinned in global.json, targets net10.0)
# run (order matters only for data availability):
src\Halo.Collector\bin\Debug\net10.0\win-x64\Halo.Collector.exe   # data process (full sensors need admin)
src\Halo.Widgets\bin\Debug\net10.0\win-x64\Halo.Widgets.exe        # widget windows + tray
src\Halo.Settings\bin\Debug\net10.0-windows\win-x64\Halo.Settings.exe
# diagnostics: dump every metric in shared memory (--json for the documented shape)
Halo.Collector.exe --dump [--json]
# one-time upgrade of a pre-v2 config folder:
Halo.Collector.exe --migrate-config <old config dir> [--to <dir>]
# publish self-contained into bin\: tools\publish-halo.ps1
# autostart registration (elevates): tools\install-halo.ps1 · removal: tools\uninstall-halo.ps1
```

**Paths** come from `Halo.Shared.Paths`, never from walking up to `Halo.sln`. Assets, fonts and the
bundled PresentMon SDK are read from the exe's own folder (the build copies them there); config and
logs live in `%LOCALAPPDATA%\Halo`, or `<app root>\data` when a `portable.marker` file sits next to
the exe. The repo's `config\` is the reference layout, not what a running Halo reads.

Portable-first policy (docs/global-installs.md): the NuGet cache is project-local
(`tools\nuget-cache`). Only global footprints: scheduled task `\Halo\Collector`, HKCU Run
`HaloWidgets` — both created only by `tools\install-halo.ps1`, removed by uninstall.

## Architecture (three processes)

- **Halo.Collector** (elevated at install; degrades gracefully unelevated): `ISensorProvider`
  implementations polled on per-class cadences by `ProviderHost` (rate caps per plan §5).
  Publishes to shared memory `Local\Halo.Metrics.v2` via `MetricsWriter` (lock-free: atomic
  8-byte value slots, seqlock strings, append-only frame ring, provider health table). Session
  maxima = `.max` metrics via `MetricSink`. Control channel: named pipe `Halo.Control.v2`
  (`reset-max`, `reset-net`, `rescan`, `reload-config`, `ping`).
- **Halo.Widgets**: one WS_EX_NOREDIRECTIONBITMAP HWND per widget, DirectComposition +
  D2D on a shared D3D11 device (`Dx`). Panels are element trees (`Render\Elements.cs`)
  in a Rainmeter-like flow layout (`Render\Panel.cs`), built per type in `PanelDefs\`.
  Dirty-driven redraw. Drag/snap(8px)/z-modes/click-through/opacity/context menu in
  `WidgetWindow`; desktop parenting (WorkerW/Progman) + tray + watchdog in `App`.
- **Halo.Settings**: WPF config editor writing the data folder's `config\*.json`; both other
  processes hot-reload via `ConfigStore` file watcher.
- **Halo.Metrics**: the public client package — section layout, reader, writer, `CollectorSession`
  and the control-pipe client. Dependency-free and AOT-friendly so anyone can read Halo's metrics
  (`docs\metrics-protocol.md`). Halo.Shared depends on it, never the other way round.

## Conventions

- Layout coordinates are **logical units** (panel = 206 wide); theme scale (1.7) is applied
  once in the renderer. 8-pt text rows use `FixedH = 11` (Rainformer's effective line height).
- Colors only via `Theme` tokens (extracted from the Rainformer skin's `@Resources\Variables.inc` —
  values only, no GPL code; reference copy lives at
  `C:\Users\final\Documents\Rainmeter\Skins\RainformerHWi`, no longer vendored in this repo).
  Staged warn colors: `CpuRamPanelImpl.WarnColor`.
- Metric names: `Halo.Metrics\MetricNames.cs`; `.max` suffix = session maximum. Indexed families
  (`gpu.<i>.*`, `cpu.core.<i>.*`, `fan.<n>.*`, `drive.<x>.*`) are discovered at runtime — read
  `gpu.count` / `cpu.logical.count` / `fan.count`, never assume one of anything.
- What each panel shows, which options it takes and which theme tokens it paints with is declared
  once in `Halo.Shared\Panels\PanelCatalog.cs`; the renderer and the Settings app both read it.
  Per-widget overrides live in `widgets.json` (`metrics`, `options`, `appearance`).
- Panel visual specs live in `tools\extracted\*.json` (faithful transcriptions of the
  original Rainmeter skins) — treat them as the source of truth for 1:1 parity.
- Frame data is two lanes. **Resolved lane** (plan D7): bundled PresentMon 2 service
  (copied to `<app root>\presentmon\`) spawned as a console-mode child — no SCM registration — and
  P/Invoked `PresentMonAPI2.dll`; ETW flush via `settings.json > collector.presentMonEtwFlushMs` (10 ms),
  40 Hz provider poll with stats published every poll (lows cached at 2 Hz); feeds the
  DISPLAYED panel + all fate-dependent metrics. The fps pipeline is idle-aware: 10 s
  without frames → service flush 100 ms + tap providers muted; frames re-arm it (~150 ms). FRAMETIME means on both panels are rolling
  100 ms; WORST is the 1 s max. Console capture app is the fallback
  (`collector.presentMonTransport`). **Tap lane** (`PresentTap`, `collector.presentedTap`):
  own ETW session on the DXGI/D3D9 present-start events — no fate wait — feeding the
  PRESENTED panel live (1 s FPS, 100 ms frametime mean, `FrameFlags.Provisional` ring
  entries); Vulkan/OpenGL titles fall back to the resolved lane (`fps.tap.active`).
  Widgets repaint frame graphs on the `Local\Halo.FramesReady.v2` event (16 ms coalesce —
  matched to the 60 Hz widget monitor) with their tick as fallback. Smoketests: `--pm-smoketest [pid]`, `--tap-smoketest <pid>`.

## Gotchas

- Defender ML false-positive (2026-07-19): quarantined `Halo.Collector.csproj` as
  `Trojan:Win32/Bearfoos.A!ml`. If a file vanishes, check `Get-MpThreatDetection`,
  restore from git. Folder exclusion is available via `install-halo.ps1 -AddDefenderExclusion`
  (off by default — user decision).
- LHM CPU/SuperIO/Storage parts and PresentMon (ETW) need elevation; unelevated they mark
  metrics N/A and the widgets render "N/A"/idle states.
- PawnIO (`C:\Program Files\PawnIO`) belongs to FanControl — never uninstall with Halo.
- The 3 icon fonts in `assets\fonts` are copied from the Rainformer skin (personal use).
