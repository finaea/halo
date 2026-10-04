using System.Globalization;
using Halo.Shared;
using Halo.Shared.Skins;

namespace Halo.Widgets.Harness;

/// <summary>
/// <c>Halo.Widgets.exe --render &lt;out.png&gt; --type &lt;id&gt; [--fixture &lt;name|path.json&gt;]
/// [--scale 1.7] [--width 206] [--backdrop dark|light|busy|none] [--warp]
/// [--config &lt;dir&gt; --widget &lt;id&gt;] [--skin &lt;id&gt;] [--preset &lt;id&gt;] [--art-free]</c> — with <c>--config</c>,
/// the widget (and its theme) come from that folder's settings.json + widgets.json, read-only, and
/// <c>--type</c> is optional. <c>--skin</c>/<c>--preset</c> set the global look on top of either.
///
/// <para><c>--render &lt;assets dir&gt; --previews [--warp]</c> renders the Settings gallery's
/// pictures: every skin × preset in <c>SkinCatalog</c>, one CPU/RAM card with no backdrop (the
/// card's own alpha, so the gallery shows it the way a desktop would), to
/// <c>&lt;assets dir&gt;\skins\&lt;skin&gt;\previews\&lt;preset&gt;.png</c> — and, for a skin that has a
/// <c>game-art</c> folder, an art-bearing set into <c>game-art\previews\</c>. The catalogue is walked
/// here rather than in build.ps1 so a new preset needs no script edit. The <c>previews\</c> set is
/// art-free: rendered with the
/// skin option <c>gameArt</c> off, because they live outside <c>game-art\</c> and must survive its
/// removal.</para>
///
/// <para>Program.cs dispatches here before the fatal-dialog hook and the single-instance mutex, and
/// this path never reaches either: a render while the real widgets are running would otherwise
/// exit 1 on the mutex, and a render failure would put a modal dialog on the desktop. It also
/// never starts the log, the session record, a window, the tray, a config write or the collector.
/// Errors go to stderr with exit code 2; bad arguments exit 1.</para>
/// </summary>
public static class RenderCli
{
    public const string Verb = "--render";

    public static int Run(string[] args)
    {
        string? output = null, type = null, configDir = null, widgetId = null, skin = null, preset = null;
        string fixture = "idle", backdrop = "none";
        double scale = 1.7;
        double? width = null;
        bool warp = false, previews = false, artFree = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case Verb: output = Next(); break;
                    case "--type": type = Next(); break;
                    case "--fixture": fixture = Next(); break;
                    case "--scale": scale = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--width": width = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--backdrop": backdrop = Next(); break;
                    case "--warp": warp = true; break;
                    case "--config": configDir = Next(); break;
                    case "--widget": widgetId = Next(); break;
                    case "--skin": skin = Next(); break;
                    case "--preset": preset = Next(); break;
                    case "--previews": previews = true; break;
                    case "--art-free": artFree = true; break;
                    default: throw new ArgumentException($"unknown argument '{a}'");
                }
            }
            if (output == null || (type == null && configDir == null && !previews))
                throw new ArgumentException("--render <out.png> and --type <id> (or --config <dir> --widget <id>) are required");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"halo --render: {ex.Message}");
            return 1;
        }

        try
        {
            using var dx = new Dx(Paths.FontsDir, warp);
            if (previews) return RenderPreviews(dx, output);
            if (type == CapabilitySheet.Type)
            {
                var (sheet, survived) = CapabilitySheet.Render(dx);
                sheet.SavePng(output);
                Console.WriteLine($"{output} ({sheet.Width}x{sheet.Height}) · after Dx.Recreate: {(survived ? "identical" : "DIFFERENT")}");
                return survived ? 0 : 2;
            }
            var image = PanelRenderer.Render(dx, new RenderRequest(type, fixture, scale, width, backdrop, configDir, widgetId, skin, preset, artFree));
            image.SavePng(output);
            Console.WriteLine($"{output} ({image.Width}x{image.Height})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"halo --render: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int RenderPreviews(Dx dx, string assetsDir)
    {
        foreach (var skin in SkinCatalog.All)
        {
            string dir = Path.Combine(assetsDir, "skins", skin.Id, "previews");
            Directory.CreateDirectory(dir);
            foreach (var preset in skin.Presets)
            {
                string file = Path.Combine(dir, preset.Id + ".png");
                PanelRenderer.Render(dx, new RenderRequest("cpu-ram", Skin: skin.Id, Preset: preset.Id, ArtFree: true)).SavePng(file);
                Console.WriteLine(file);
            }

            // A render that contains game art is game art (tech plan §6): the art-bearing set lives
            // inside game-art\, so removing that one folder takes these with it, and a tree without
            // the folder never grows one back.
            string art = Path.Combine(assetsDir, "skins", skin.Id, "game-art");
            if (!Directory.Exists(art)) continue;
            string artDir = Path.Combine(art, "previews");
            Directory.CreateDirectory(artDir);
            foreach (var preset in skin.Presets)
            {
                string file = Path.Combine(artDir, preset.Id + ".png");
                PanelRenderer.Render(dx, new RenderRequest("cpu-ram", Skin: skin.Id, Preset: preset.Id)).SavePng(file);
                Console.WriteLine(file);
            }
        }
        return 0;
    }
}
