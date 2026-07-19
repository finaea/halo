# Halo — Hardware Analytics & Live Overlay

Native Windows 11 desktop widget suite replacing the Rainmeter + Rainformer + HWiNFO +
Afterburner + RTSS + NVIDIA App stack. Design doc: [native-widget-implementation-plan.md](native-widget-implementation-plan.md).

## Build & run

```powershell
dotnet build Halo.sln -c Debug            # all projects (SDK 9, targets net9.0)
# run (order matters only for data availability):
src\Halo.Collector\bin\Debug\net9.0\win-x64\Halo.Collector.exe   # data process (full sensors need admin)
src\Halo.Widgets\bin\Debug\net9.0\win-x64\Halo.Widgets.exe        # widget windows + tray
src\Halo.Settings\bin\Debug\net9.0-windows\win-x64\Halo.Settings.exe
# diagnostics: dump every metric in shared memory
Halo.Collector.exe --dump
# publish self-contained into bin\: tools\publish-halo.ps1
# autostart registration (elevates): tools\install-halo.ps1 · removal: tools\uninstall-halo.ps1
```

Portable-first policy (global-installs.md): NuGet cache is project-local (`tools\nuget-cache`),
config in `config\`, logs in `logs\`. Only global footprints: scheduled task `\Halo\Collector`,
HKCU Run `HaloWidgets` — both created only by `tools\install-halo.ps1`, removed by uninstall.

## Architecture (three processes)

- **Halo.Collector** (elevated at install; degrades gracefully unelevated): `ISensorProvider`
  implementations polled on per-class cadences by `ProviderHost` (rate caps per plan §5).
  Publishes to shared memory `Local\Halo.Metrics.v1` via `MetricsWriter` (lock-free: atomic
  8-byte value slots, seqlock strings, append-only frame ring). Session maxima = `.max`
  metrics via `MetricSink`. Control channel: named pipe `Halo.Control.v1` (`reset-max`,
  `reset-net`).
- **Halo.Widgets**: one WS_EX_NOREDIRECTIONBITMAP HWND per widget, DirectComposition +
  D2D on a shared D3D11 device (`Dx`). Panels are element trees (`Render\Elements.cs`)
  in a Rainmeter-like flow layout (`Render\Panel.cs`), built per type in `PanelDefs\`.
  Dirty-driven redraw. Drag/snap(8px)/z-modes/click-through/opacity/context menu in
  `WidgetWindow`; desktop parenting (WorkerW/Progman) + tray + watchdog in `App`.
- **Halo.Settings**: WPF config editor writing `config\*.json`; both other processes
  hot-reload via `ConfigStore` file watcher.

## Conventions

- Layout coordinates are **logical units** (panel = 206 wide); theme scale (1.7) is applied
  once in the renderer. 8-pt text rows use `FixedH = 11` (Rainformer's effective line height).
- Colors only via `Theme` tokens (extracted from `RainformerHWiv2\@Resources\Variables.inc` —
  values only, no GPL code). Staged warn colors: `CpuRamPanelImpl.WarnColor`.
- Metric names: `Halo.Shared\Metrics\MetricNames.cs`; `.max` suffix = session maximum.
- Panel visual specs live in `tools\extracted\*.json` (faithful transcriptions of the
  original Rainmeter skins) — treat them as the source of truth for 1:1 parity.
- Frame data is two lanes. **Resolved lane** (plan D7): bundled PresentMon 2 service
  (`tools\presentmon\sdk\`) spawned as a console-mode child — no SCM registration — and
  P/Invoked `PresentMonAPI2.dll`; ETW flush via `settings.PresentMonEtwFlushMs` (10 ms),
  40 Hz provider poll with stats published every poll (lows cached at 2 Hz); feeds the
  DISPLAYED panel + all fate-dependent metrics. The fps pipeline is idle-aware: 10 s
  without frames → service flush 100 ms + tap providers muted; frames re-arm it (~150 ms). FRAMETIME means on both panels are rolling
  100 ms; WORST is the 1 s max. Console capture app is the fallback
  (`settings.PresentMonTransport`). **Tap lane** (`PresentTap`, `settings.PresentedTap`):
  own ETW session on the DXGI/D3D9 present-start events — no fate wait — feeding the
  PRESENTED panel live (1 s FPS, 100 ms frametime mean, `FrameFlags.Provisional` ring
  entries); Vulkan/OpenGL titles fall back to the resolved lane (`fps.tap.active`).
  Widgets repaint frame graphs on the `Local\Halo.FramesReady.v1` event (16 ms coalesce —
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
