# Reference config (schema v2)

**Halo never reads this folder.** It is here to show what the two settings files look like without
installing anything.

The real files live in `%LOCALAPPDATA%\Halo\config\`, or in `<app folder>\data\config\` when a
`portable.marker` file sits next to the programs (`src/Halo.Shared/Paths.cs`). Halo creates them on
first run and the Settings app updates them with every change, so editing them by hand is
optional. Either way, the file watcher picks up an outside edit within about 200 ms.

| File | What it holds |
| --- | --- |
| `settings.json` | Global appearance defaults, snapping and locking, collector options, and the log level |
| `widgets.json` | One entry per widget: type, monitor, position, z-order, refresh rate, and per-widget metric, option and appearance overrides |

There is no `theme.json` any more. Colours live under `settings.json > appearance.colors` (global)
and under `widgets[].appearance` for each widget.

## What these files are

They were produced by running the v1 → v2 migration over an existing layout:

```powershell
Halo.Collector.exe --migrate-config <old config dir> --to <new dir>
```

So the monitor device names (`\\.\DISPLAY5`), the pixel positions and the set of twelve widgets
belong to one particular PC and are not defaults. They are a worked example of the schema, not a
layout to copy. `externalIp.enabled` is shown as `false`, which is what a fresh install gets.

A real config will look different, and that is intended: nothing here is tied to specific hardware.
Drive letters, fan channels, GPU indexes and core counts are all discovered at run time and never
written into the settings.

## Keys worth knowing

| Key | Notes |
| --- | --- |
| `appearance.scale` | `"auto"` (follows each monitor's DPI) or a number. The example uses `1.7`. |
| `widgets[].rateHz` | From 0.5 up to the widget's limit. Raising it beyond a metric's real update rate gains nothing; the "?" help next to the Refresh rate setting explains each widget's limit. |
| `widgets[].graph.historyS` | Seconds of history in the widget's graph |
| `widgets[].metrics` | Per-row overrides: `show`, `label`, `graph`, `color`, `warn`, `max` |
| `collector.externalIp.enabled` | The only outbound network request Halo can make. `false` on a fresh install. |
| `collector.presentMonTransport` | `auto` or `sdk`; both mean the bundled PresentMon SDK |
| `diagnostics.logLevel` | `"info"` by default; System check's **Verbose logging** switch sets `"debug"`. The `HALO_LOG_LEVEL` environment variable overrides it. |
| `schemaVersion` | `2`. A v1 config folder is upgraded automatically the first time Halo starts. |

The complete list is in `src/Halo.Shared/Config/Models.cs`, and what each widget accepts is
declared in `src/Halo.Shared/Panels/PanelCatalog.cs`.
