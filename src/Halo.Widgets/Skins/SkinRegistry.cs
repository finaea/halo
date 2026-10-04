using Halo.Shared;
using Halo.Shared.Skins;
using Halo.Widgets.Skins.AzurArchive;
using Halo.Widgets.Skins.Rainformer;

namespace Halo.Widgets.Skins;

/// <summary>
/// Skin id → renderer. An id this Halo does not know (a config written by a newer version) draws
/// as Rainformer; the id itself stays in the file, so going back to that newer Halo restores it.
/// </summary>
public static class SkinRegistry
{
    private static readonly ComponentLog Log2 = Log.For("skins");

    private static readonly Dictionary<string, ISkin> Skins = new(StringComparer.Ordinal)
    {
        [SkinCatalog.RainformerId] = RainformerSkin.Instance,
        [AzurArchiveSkinInfo.Id] = AzurArchiveSkin.Instance,
    };

    // Every widget resolves its skin on every rebuild, so the fallback would otherwise log once per
    // widget per config change.
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    public static ISkin Default => RainformerSkin.Instance;

    public static ISkin For(string? id)
    {
        if (id != null && Skins.TryGetValue(id, out var skin)) return skin;
        lock (Warned)
            if (Warned.Add(id ?? ""))
                Log2.Warn($"unknown skin '{id}' — drawing Rainformer instead");
        return Default;
    }
}
