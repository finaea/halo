using Halo.Shared;
using Halo.Widgets.Render;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace Halo.Widgets.Skins;

/// <summary>
/// A skin's images. A <b>slot</b> is a name the skin draws by ("arona", "manjuu"); it resolves along
/// one chain:
/// <list type="number">
/// <item>the user's override, <c>&lt;data dir&gt;\skins\&lt;skin id&gt;\user-art\&lt;slot&gt;.png</c> —
/// a bring-your-own-art folder that survives updates and reinstalls;</item>
/// <item>the bundled file the skin names, relative to <c>assets\skins\&lt;skin id&gt;\</c>;</item>
/// <item>nothing. <see cref="Draw"/> returns false and draws nothing, so a skin built without its
/// art folder simply has no picture there — never a placeholder square.</item>
/// </list>
/// A file that is present but will not decode falls through to the next link, the same as a
/// missing one.
///
/// <para><b>Cached per D2D device, not per window.</b> Every widget's context comes from the one
/// shared device (<see cref="Dx"/>), and a bitmap made on one context of a device draws on all of
/// them, so ten widgets showing the same portrait hold one copy. <see cref="Dx"/> calls
/// <see cref="InvalidateAll"/> before it lets go of a device, so every skin's cache — including a
/// skin no widget shows any more, which would never draw again to notice — releases its bitmaps
/// along with the device. As a second guard the cache also remembers which device it was filled
/// on and drops everything when a draw arrives on another. Comparing native pointers is safe: the
/// cached bitmaps hold a reference to their device, so while there is anything to drop, the old
/// device cannot be freed and its address cannot be handed to the new one.</para>
///
/// <para>Decoded once at twice the largest size it has been drawn at (WIC high-quality cubic on
/// the way down, never up), then drawn with D2D high-quality cubic. A bigger draw later — a
/// window moved to a higher-DPI monitor — decodes again at the new size.</para>
///
/// <para>User files are read when first drawn; replacing one takes a restart of the widgets.</para>
/// </summary>
public sealed class SkinAssets
{
    private static readonly ComponentLog Log2 = Log.For("skin-assets");
    private static readonly Dictionary<string, SkinAssets> BySkin = new(StringComparer.OrdinalIgnoreCase);
    private static IWICImagingFactory? _wic;

    private sealed class Entry
    {
        public ID2D1Bitmap1? Bitmap;
        public int SourceW, SourceH;
        public bool Failed;
    }

    private readonly string _bundledDir, _userDir;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private nint _device;

    /// <summary>The shared instance for a skin. Widgets run on one UI thread (the D2D device is
    /// single-threaded), so the registry needs no lock.</summary>
    public static SkinAssets For(string skinId)
    {
        if (!BySkin.TryGetValue(skinId, out var a))
        {
            a = new SkinAssets(
                Path.Combine(Paths.AssetsDir, "skins", skinId),
                Path.Combine(Paths.DataDir, "skins", skinId, "user-art"));
            BySkin[skinId] = a;
        }
        return a;
    }

    internal SkinAssets(string bundledDir, string userDir)
    {
        _bundledDir = bundledDir;
        _userDir = userDir;
    }

    /// <summary>Release every skin's bitmaps and forget the device they were made on. Called by
    /// <see cref="Dx"/> before its device goes away (recreate and final dispose).</summary>
    public static void InvalidateAll()
    {
        foreach (var a in BySkin.Values)
        {
            a.Clear();
            a._device = 0;
        }
    }

    /// <summary>Decoded bitmaps currently held (tests).</summary>
    internal int CachedBitmaps => _cache.Values.Count(e => e.Bitmap != null);

    /// <summary>The files on disk for a slot, in chain order. Looked up once, then remembered, so a
    /// repaint does not stat the disk.</summary>
    public IReadOnlyList<string> Resolve(string slot, string? bundled = null)
    {
        var key = (slot, bundled);
        if (_resolved.TryGetValue(key, out var paths)) return paths;
        var found = new List<string>(2);
        string user = Path.Combine(_userDir, slot + ".png");
        if (File.Exists(user)) found.Add(user);
        if (bundled != null && Path.Combine(_bundledDir, bundled) is var b && File.Exists(b)) found.Add(b);
        _resolved[key] = found;
        return found;
    }

    private readonly Dictionary<(string, string?), List<string>> _resolved = new();

    /// <summary>
    /// Draw a slot fitted inside <paramref name="dest"/> (logical units; aspect kept, centred).
    /// False when no link of the chain produced an image — the caller may draw a vector stand-in,
    /// or nothing.
    /// </summary>
    public bool Draw(RenderContext rc, string slot, string? bundled, Rect dest, float opacity = 1f)
    {
        CheckDevice(rc);
        foreach (var path in Resolve(slot, bundled))
        {
            var bmp = Get(rc, path, dest);
            if (bmp == null) continue;
            var e = _cache[path];
            rc.DC.DrawBitmap(bmp, Fit(dest, e.SourceW, e.SourceH), opacity,
                Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);
            return true;
        }
        return false;
    }

    private void CheckDevice(RenderContext rc)
    {
        using var device = rc.DC.Device;
        if (device.NativePointer == _device) return;
        Clear();
        _device = device.NativePointer;
    }

    private static Rect Fit(Rect box, int w, int h)
    {
        float s = Math.Min(box.Width / w, box.Height / h);
        float fw = w * s, fh = h * s;
        return new Rect(box.X + (box.Width - fw) / 2, box.Y + (box.Height - fh) / 2, fw, fh);
    }

    private ID2D1Bitmap1? Get(RenderContext rc, string path, Rect dest)
    {
        if (!_cache.TryGetValue(path, out var e)) _cache[path] = e = new Entry();
        if (e.Failed) return null;
        // the steady state: decoded before, big enough — no file access on a repaint
        if (e.Bitmap != null && e.Bitmap.PixelSize.Width >= TargetSize(e, dest, rc).W) return e.Bitmap;
        try
        {
            _wic ??= new IWICImagingFactory();
            using var decoder = _wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
            using var frame = decoder.GetFrame(0);
            var size = frame.Size;
            e.SourceW = size.Width;
            e.SourceH = size.Height;
            var (tw, th) = TargetSize(e, dest, rc);

            using var scaler = _wic.CreateBitmapScaler();
            scaler.Initialize(frame, (uint)tw, (uint)th, Vortice.WIC.BitmapInterpolationMode.HighQualityCubic);
            using var converter = _wic.CreateFormatConverter();
            converter.Initialize(scaler, Vortice.WIC.PixelFormat.Format32bppPBGRA);
            var bmp = rc.DC.CreateBitmapFromWicBitmap(converter,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
            e.Bitmap?.Dispose();
            e.Bitmap = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            e.Failed = true;
            Log2.Warn($"could not load {path} ({ex.GetType().Name}: {ex.Message}); falling through");
            return null;
        }
    }

    /// <summary>Twice the device pixels this draw covers, capped at the source.</summary>
    private static (int W, int H) TargetSize(Entry e, Rect dest, RenderContext rc)
    {
        var fit = Fit(dest, e.SourceW, e.SourceH);
        float want = Math.Max(fit.Width, fit.Height) * rc.PixelScale * 2;
        float scale = Math.Min(1f, want / Math.Max(e.SourceW, e.SourceH));
        return (Math.Max(1, (int)Math.Ceiling(e.SourceW * scale)), Math.Max(1, (int)Math.Ceiling(e.SourceH * scale)));
    }

    private void Clear()
    {
        foreach (var e in _cache.Values) e.Bitmap?.Dispose();
        _cache.Clear();
        _resolved.Clear();
    }
}
