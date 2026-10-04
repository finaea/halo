using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Widgets.Skins;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Halo.Widgets.Harness;

/// <summary>What to draw. <see cref="Width"/> null = the panel's default (206 logical units).
/// With <see cref="ConfigDir"/>, the widget <see cref="WidgetId"/> is taken from that folder's
/// widgets.json and its theme resolved against its settings.json — type, options, metrics and
/// appearance all come from the file, and <see cref="Type"/> may be null. <see cref="Scale"/> is
/// then only what an <c>"auto"</c> scale resolves to.</summary>
public sealed record RenderRequest(
    string? Type,
    string Fixture = "idle",
    double Scale = 1.7,
    double? Width = null,
    string Backdrop = "none",
    string? ConfigDir = null,
    string? WidgetId = null,
    string? Skin = null,
    string? Preset = null,
    bool ArtFree = false);

/// <summary>
/// Draws one panel off-screen: no window, no swapchain, no collector. A twin of
/// <c>WidgetWindow.Tick</c> + <c>WidgetWindow.Render</c> (WidgetWindow.cs:192-263) that targets a
/// D2D bitmap instead of a swapchain buffer — <see cref="Panel.Draw"/> has no HWND dependency, so
/// nothing else has to change for a render to match what the desktop shows.
///
/// <para>Everything that would make two renders differ is pinned: wall time (the clock panel
/// formats it), the QPC the graphs bucket by, culture and DPI. Graphs need history, so the panel
/// is driven through <see cref="Ticks"/> synthetic ticks before it is drawn.</para>
/// </summary>
public static class PanelRenderer
{
    /// <summary>Wall time every render shows. A Saturday afternoon with a two-digit day of year.</summary>
    public static readonly DateTime PinnedNow = new(2026, 3, 14, 13, 37, 42, DateTimeKind.Local);

    /// <summary>Ticks driven before the draw, and their spacing. 220 × 0.2 s = 44 s, a little more
    /// than the default 40 s graph history, so every graph column has a sample behind it.</summary>
    public const int Ticks = 220;
    public const double TickS = 0.2;

    /// <summary>First tick's QPC. Fixed, because <see cref="GraphEl"/> buckets by absolute QPC
    /// (<c>NowQpc / colTicks</c>) and a real timestamp would shift every column boundary.</summary>
    private static readonly long StartQpc = 1000L * Stopwatch.Frequency;

    public static PanelImage Render(Dx dx, RenderRequest req)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            return RenderCore(dx, req);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    private static PanelImage RenderCore(Dx dx, RenderRequest req)
    {
        var metrics = FixtureMetrics.Load(req.Fixture);
        var (settings, widget, all) = req.ConfigDir != null
            ? LoadConfig(req.ConfigDir, req.WidgetId ?? throw new ArgumentException("--config needs --widget <id>"))
            : (new AppSettings(), new WidgetInstance { Id = "render-" + req.Type, Type = req.Type ?? "" }, null);
        if (req.Width is { } w) widget.Appearance.Width = w;
        // --skin / --preset pick the global look, the way the Appearance page writes it.
        if (req.Skin != null) settings.Appearance.Skin = req.Skin;
        if (req.Preset != null) settings.Appearance.SkinFor(settings.Appearance.Skin).Preset = req.Preset;
        // "auto" scale resolves to the requested one; DPI is pinned at 100 % so a render is the same
        // number of pixels on every monitor setup.
        var theme = Theme.Resolve(settings.Appearance, widget, req.Scale);
        theme.Dpi = 96;
        // A render that contains game art is game art (tech plan §6): anything written outside a
        // skin's game-art\ folder is drawn with the gameArt option off. Forced on the resolved
        // theme, not the global options, so a widget drawn with another skin than the global one
        // or a widget's own gameArt=true cannot bring the art back. A skin that does not declare
        // the option never has the key (Theme.Resolve keeps declared keys only).
        if (req.ArtFree && theme.SkinOptions.ContainsKey("gameArt")) theme.SkinOptions["gameArt"] = "false";

        // the mood is the process-wide one in a running Halo, reading every widget's thresholds;
        // a config render hands it the same list, so it answers as the live one would. It is
        // ticked with the fixture, so a fixture can drive any state.
        IReadOnlyList<WidgetInstance> widgets = all ?? [widget];
        var mood = new SystemMood(() => widgets, Skins.AzurArchive.AzurArchiveSkin.Birthdays);
        var ctx = new PanelContext
        {
            Metrics = metrics,
            Mood = mood,
            Theme = theme,
            Settings = settings,
            Widget = widget,
            Type = PanelCatalog.Find(widget.Type),
        };
        using var panel = PanelFactory.Create(widget.Type, ctx)
            ?? throw new ArgumentException($"unknown panel type '{widget.Type}' (known: {string.Join(", ", PanelFactory.KnownTypes)})");

        // A fixture may run longer than the default (mood events such as sustained zero traffic
        // need a minute or more), and pin another wall time (night, for host = auto).
        long tickQpc = (long)(TickS * Stopwatch.Frequency);
        int ticks = metrics.DurationS is { } d ? Math.Max(Ticks, (int)Math.Ceiling(d / TickS)) : Ticks;
        var now = metrics.Now ?? PinnedNow;
        long qpc = StartQpc;
        for (int k = 0; k < ticks; k++)
        {
            qpc = StartQpc + k * tickQpc;
            metrics.Advance((double)k / (ticks - 1), qpc);
            mood.Update(metrics, qpc, now);
            // the last of PokeCount clicks lands on the last tick, each 11 ticks (2.2 s) after the
            // one before — just past the poke cooldown, well inside a streak
            int back = ticks - 1 - k;
            if (metrics.Poke is { } who && back % 11 == 0 && back / 11 < metrics.PokeCount) mood.Poke(who, qpc);
            ctx.Now = now;
            ctx.NowQpc = qpc;
            ctx.TickIndex++;
            ctx.Stale = metrics.Stale;
            panel.Update(ctx);
        }

        using var dc = dx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        using var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, theme);

        double scale = theme.EffectiveScale;
        double logicalH = panel.Layout(rc, ctx);
        int pxW = (int)Math.Ceiling(theme.BgWidth * scale);
        int pxH = Math.Max(8, (int)Math.Ceiling(logicalH * scale));

        var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = dc.CreateBitmap(new SizeI(pxW, pxH), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        dc.Target = target;
        dc.SetDpi(96, 96);

        void Draw()
        {
            dc.BeginDraw();
            dc.Clear(new Color4(0, 0, 0, 0));
            DrawBackdrop(dc, req.Backdrop, pxW, pxH);
            dc.Transform = Matrix3x2.CreateScale((float)theme.BaseScale);
            panel.Draw(rc, ctx);
            dc.EndDraw();
        }
        Draw();
        // A wheel turn needs a laid-out zone to land on: draw, turn it at the first point down the
        // middle of the panel that takes it, then update and draw again.
        if (metrics.Wheel != 0)
        {
            for (double y = 0; y < logicalH; y += 2)
                if (panel.Wheel(ctx, theme.BgWidth / 2, y, metrics.Wheel)) break;
            panel.Update(ctx);
            Draw();
        }
        dc.Target = null;

        return Read(dc, target, pxW, pxH);
    }

    /// <summary>
    /// Read a config folder the way <c>ConfigStore</c> does — same serializer context, same v2 → v3
    /// upgrade — but without constructing one: its constructor can run the v1 migration, which
    /// writes, and a render must never touch the config it was pointed at.
    /// </summary>
    private static (AppSettings, WidgetInstance, IReadOnlyList<WidgetInstance>) LoadConfig(string dir, string widgetId)
    {
        AppSettings settings;
        using (var f = File.OpenRead(Path.Combine(dir, ConfigStore.SettingsFile)))
            settings = JsonSerializer.Deserialize(f, ConfigJsonContext.Default.AppSettings) ?? new AppSettings();
        WidgetsConfig widgets;
        using (var f = File.OpenRead(Path.Combine(dir, ConfigStore.WidgetsFile)))
            widgets = JsonSerializer.Deserialize(f, ConfigJsonContext.Default.WidgetsConfig) ?? new WidgetsConfig();
        SchemaV3.Upgrade(settings);
        SchemaV3.Upgrade(widgets);
        var widget = widgets.Widgets.FirstOrDefault(x => x.Id == widgetId)
            ?? throw new ArgumentException($"no widget '{widgetId}' in {dir} (have: {string.Join(", ", widgets.Widgets.Select(x => x.Id))})");
        return (settings, widget, widgets.Widgets);
    }

    /// <summary>Copy the target to a CPU-readable bitmap and lift the premultiplied BGRA rows out.</summary>
    internal static PanelImage Read(ID2D1DeviceContext dc, ID2D1Bitmap1 target, int w, int h)
    {
        using var cpu = dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(target.PixelFormat, 96, 96, BitmapOptions.CannotDraw | BitmapOptions.CpuRead));
        cpu.CopyFromBitmap(target);
        var map = cpu.Map(MapOptions.Read);
        try
        {
            var pixels = new byte[w * h * 4];
            unsafe
            {
                for (int y = 0; y < h; y++)
                    new ReadOnlySpan<byte>((byte*)map.Bits + y * map.Pitch, w * 4).CopyTo(pixels.AsSpan(y * w * 4));
            }
            return new PanelImage(w, h, pixels);
        }
        finally
        {
            cpu.Unmap();
        }
    }

    /// <summary>
    /// Stand-in wallpapers. Every card is translucent, so a render over transparent black says
    /// nothing about how it reads on a real desktop. Drawn rather than shipped as images: three
    /// flat looks are all the check needs, and nothing has to be licensed or copied next to the exe.
    /// </summary>
    private static void DrawBackdrop(ID2D1DeviceContext dc, string backdrop, int w, int h)
    {
        switch (backdrop)
        {
            case "none":
                return;
            case "dark":
                Fill(dc, 0, 0, w, h, new Color4(0.07f, 0.08f, 0.11f, 1));
                return;
            case "light":
                Fill(dc, 0, 0, w, h, new Color4(0.90f, 0.91f, 0.93f, 1));
                return;
            case "busy":
                // high-contrast blocks under every row, so a card's legibility over a photo-like
                // wallpaper is what gets judged, not over a flat colour
                Color4[] palette =
                [
                    new(0.95f, 0.75f, 0.20f, 1), new(0.10f, 0.45f, 0.80f, 1),
                    new(0.98f, 0.98f, 0.98f, 1), new(0.75f, 0.15f, 0.25f, 1),
                    new(0.15f, 0.60f, 0.35f, 1), new(0.05f, 0.05f, 0.08f, 1),
                ];
                const int cell = 37;
                for (int y = 0, row = 0; y < h; y += cell, row++)
                    for (int x = 0, col = 0; x < w; x += cell, col++)
                        Fill(dc, x, y, cell, cell, palette[(row * 4 + col * 3) % palette.Length]);
                return;
            default:
                throw new ArgumentException($"unknown backdrop '{backdrop}' (dark, light, busy, none)");
        }
    }

    private static void Fill(ID2D1DeviceContext dc, float x, float y, float w, float h, Color4 color)
    {
        using var brush = dc.CreateSolidColorBrush(color);
        dc.FillRectangle(new Rect(x, y, w, h), brush);
    }
}
