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
- PresentMon frame data uses the SDK transport by default (plan D7): bundled PresentMon 2
  service (`tools\presentmon\sdk\`) spawned as a console-mode child — no SCM registration —
  and P/Invoked `PresentMonAPI2.dll`; ETW flush tuned via `settings.PresentMonEtwFlushMs`
  (default 5 ms). The provider polls at 60 Hz (frame ring + stats; 1%/0.1% lows cached at
  2 Hz inside `FrameStats`), fps widgets tick at 100 Hz — the near-live fps-counter path.
  The console capture app (`tools\presentmon\`) is the fallback
  (`settings.PresentMonTransport`: auto | sdk | console). `Halo.Collector.exe --pm-smoketest [pid]`
  verifies the SDK path end-to-end.

## Gotchas

- Defender ML false-positive (2026-07-19): quarantined `Halo.Collector.csproj` as
  `Trojan:Win32/Bearfoos.A!ml`. If a file vanishes, check `Get-MpThreatDetection`,
  restore from git. Folder exclusion is available via `install-halo.ps1 -AddDefenderExclusion`
  (off by default — user decision).
- LHM CPU/SuperIO/Storage parts and PresentMon (ETW) need elevation; unelevated they mark
  metrics N/A and the widgets render "N/A"/idle states.
- PawnIO (`C:\Program Files\PawnIO`) belongs to FanControl — never uninstall with Halo.
- The 3 icon fonts in `assets\fonts` are copied from the Rainformer skin (personal use).
