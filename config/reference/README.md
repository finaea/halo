# Reference config (schema v2)

**Halo never reads this folder.** It is here so you can see what the two config files look like
without installing anything.

The real ones live in `%LOCALAPPDATA%\Halo\config\` — or `<app folder>\data\config\` when a
`portable.marker` file sits next to the exes (`src/Halo.Shared/Paths.cs`). Halo generates them on
first run, and the Settings app rewrites them as you click, so hand-editing is optional; the file
watcher picks up external edits within ~200 ms either way.

| File | What it holds |
| --- | --- |
| `settings.json` | global appearance defaults, snap/lock, and the collector knobs |
| `widgets.json` | one entry per widget: type, monitor, position, z-mode, refresh rate, and per-widget metric / option / appearance overrides |

There is no `theme.json` any more — colours live under `settings.json > appearance.colors`
(global) and per widget under `widgets[].appearance`.

## What you are looking at

These files were produced by running the v1 → v2 migrator over the author's own layout:

```powershell
Halo.Collector.exe --migrate-config <old config dir> --to <new dir>
```

So the monitor device names (`\\.\DISPLAY5`), the pixel positions and the set of twelve widgets are
one particular PC's, not defaults. Treat them as a worked example of the schema, not a layout to
copy. `externalIp.enabled` is shown as `false`, which is what a fresh install gets.

Yours will look different, and that is the point: nothing here is hardware-specific by design —
drive letters, fan channels, GPU indexes and core counts are all discovered at runtime and never
written into config.

## Keys worth knowing

| Key | Notes |
| --- | --- |
| `appearance.scale` | `"auto"` (per-monitor DPI) or a number. The example uses `1.7`. |
| `widgets[].rateHz` | 0.5 up to the panel's ceiling. Raising it past a metric's real cadence buys nothing — Settings' "?" popover lists each metric's rate. |
| `widgets[].graph.historyS` | seconds of history in the widget's graph |
| `widgets[].metrics` | per-row overrides: `show`, `label`, `graph`, `color`, `warn`, `max` |
| `collector.externalIp.enabled` | the only outbound network call Halo can make. `false` on a fresh install. |
| `collector.presentMonTransport` | `auto` or `sdk`; both mean the bundled PresentMon SDK |
| `schemaVersion` | `2`. A v1 folder is migrated automatically on first start. |

The authoritative list is `src/Halo.Shared/Config/Models.cs`; what each panel accepts is declared in
`src/Halo.Shared/Panels/PanelCatalog.cs`.
