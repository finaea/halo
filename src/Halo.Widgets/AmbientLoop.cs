using System.Diagnostics;
using System.Numerics;
using Halo.Metrics;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Halo.Widgets;

public enum AmbientKind
{
    /// <summary>A tilted halo ring turning, at one of three speeds by CPU load.</summary>
    Spin,
    /// <summary>A cached picture rising and settling a couple of pixels.</summary>
    Bob,
}

/// <summary>
/// A looping thing on a widget (design rule 9), as the element that owns it declares it while
/// drawing — at most <see cref="MaxPerWidget"/> per widget, so the network card's two seats can both
/// turn their halos. The window bakes each one's content onto its own DirectComposition visual and
/// moves only that visual's transform, so the panel is never redrawn for it — see
/// <see cref="AmbientVisual"/> for why it is moved by Halo at 30 Hz rather than by a DWM animation.
/// </summary>
public sealed class AmbientLoop
{
    /// <summary>Loops one widget may run. Two: the network card seats two students, and only one
    /// turning halo read as a bug (Jack, 2026-10-04). Each costs its own small surface and one
    /// transform write per frame; the commit is shared.</summary>
    public const int MaxPerWidget = 2;

    public required AmbientKind Kind { get; init; }

    /// <summary>Everything the content depends on (slot, colour, opacity); a new key re-bakes it.</summary>
    public required string Key { get; init; }

    /// <summary>Where the content sits, in the panel's logical units. For a spin this is the ring's
    /// ellipse box; for a bob, the picture's rectangle.</summary>
    public required Rect Bounds { get; init; }

    /// <summary>Bob only: paints the picture at <see cref="Bounds"/>.</summary>
    public Action<RenderContext>? Draw { get; init; }

    /// <summary>Spin only: the ring's colour and stroke (logical units).</summary>
    public Color4 Color { get; init; }
    public float Stroke { get; init; }

    /// <summary>Spin only: paints the halo flat-on, centred on the point with the given radius, in
    /// place of the generic ring and its notches. Null = the generic ring. Its identity must be in
    /// <see cref="Key"/>: the delegate itself is never compared.</summary>
    public Action<RenderContext, Vector2, float>? Flat { get; init; }

    /// <summary>Spin with <see cref="Flat"/>: where the glint sits, in units of the radius from the
    /// centre (y down). Null = straight up, on the rim.</summary>
    public Vector2? GlintAt { get; init; }

    /// <summary>Spin only: the halo's pose — its centre and flat-on radius (logical units), its
    /// pitch (radians, 0 = facing the viewer), its roll about the line of sight, and the
    /// perspective distance in radii (0 = none). <see cref="Radius"/> 0 derives an orthographic
    /// pose from <see cref="Bounds"/> as a flattened ellipse — the plain ring.</summary>
    public Vector2 Centre { get; init; }
    public float Radius { get; init; }
    public float Pitch { get; init; }
    public float Roll { get; init; }
    public float Depth { get; init; }

    /// <summary>The spin's pose with the <see cref="Bounds"/> fallback applied.</summary>
    public (Vector2 Centre, float Radius, float Pitch, float Roll, float Depth) SpinPose => Radius > 0
        ? (Centre, Radius, Pitch, Roll, Depth)
        : (new Vector2(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2), Bounds.Width / 2,
           MathF.Acos(Math.Clamp(Bounds.Height / Math.Max(1e-3f, Bounds.Width), 0, 1)), 0, 0);

    /// <summary>
    /// The halo's pose as one matrix: a point of the flat-on halo (offset from its centre, in the
    /// same units as <paramref name="radius"/>) to its offset on screen from the centre. Pitched
    /// about the horizontal axis so the top half leans away, rolled about the line of sight, then
    /// projected with the far side smaller — the depth cue a flat ellipse lacks. Shared by the
    /// still draw (D2D <c>DrawBitmap</c>) and the spin (a DComp 3D transform), which use the same
    /// row-vector, divide-by-w convention, so the two poses match exactly.
    /// </summary>
    public static Matrix4x4 Pose(float pitch, float roll, float depth, float radius)
    {
        var flat = Matrix4x4.Identity;
        flat.M33 = 0;                                       // the picture lands on z = 0 …
        if (depth > 0) flat.M34 = -1 / (depth * radius);    // … after z sets the perspective
        return Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationZ(roll) * flat;
    }

    public bool SameContent(AmbientLoop? o) => o != null && o.Kind == Kind && o.Key == Key && o.Bounds == Bounds;

    /// <summary>
    /// Whether loops run at all right now: motion full, and the machine neither asleep nor running
    /// a game. A game is exactly when DWM's extra work would cost frames, and asleep means the
    /// character is out cold.
    /// </summary>
    public static bool On(PanelContext c) => Gate(c.Motion, c.Stale, c.Mood);

    /// <summary>The same gate from live process state — App checks it every pass, so a loop stops
    /// the moment motion goes off or a game starts, not at its window's next data tick.</summary>
    public static bool Gate(MotionLevel motion, bool stale, SystemMood mood)
        => motion == MotionLevel.Full && !stale && mood.Known
           && mood.State != MoodState.Asleep && !mood.Presenting;

    /// <summary>Claim one of the widget's loops. False when loops are off, while the window's
    /// entrance is still fading (a child visual cannot fade with the swapchain, so the content stays
    /// on the card until it settles), or when <see cref="MaxPerWidget"/> are already taken — the
    /// caller then draws its content itself, still.</summary>
    public static bool Offer(PanelContext c, AmbientLoop loop)
    {
        if (!On(c) || c.Entering || c.Ambients.Count >= MaxPerWidget) return false;
        c.Ambients.Add(loop);
        return true;
    }

    /// <summary>Spin speed step from total CPU load, with 5 points of hysteresis so a load sitting
    /// on a boundary does not restart the animation every tick. N/A keeps the current step.</summary>
    public static int SpinStep(IMetricSource m, int current)
    {
        if (!m.TryValue(MetricNames.CpuTotalPct, out double cpu)) return current;
        int raw = cpu >= 70 ? 2 : cpu >= 30 ? 1 : 0;
        if (raw == current) return current;
        double edge = raw > current ? (raw == 2 ? 70 : 30) : (current == 2 ? 70 : 30);
        return Math.Abs(cpu - edge) >= 5 ? raw : current;
    }
}

/// <summary>
/// The window side of one <see cref="AmbientLoop"/> (a window holds one per loop slot): a child
/// visual above the swapchain's, holding a DComp surface baked once. Halo moves it itself, <c>appearance.motionFps</c> times a second
/// (30 by default; App reads it live, so a change needs no restart): App sets
/// every live loop's angle or offset and commits the device once (<see cref="Frame"/>). The panel is
/// never redrawn for it.
///
/// <para><b>Why not a DComp animation, which costs Halo nothing at all:</b> measured for ticket 09
/// (2026-10-04, standalone window with Halo's NOREDIRECTIONBITMAP + premultiplied-swapchain tree, one
/// turning ring, dwm.exe sampled 1 s, 20 s phases interleaved with still ones). A composition
/// animation makes DWM recompose every display refresh: dwm.exe went from ~8–10 % to 16–29 % of a
/// core on the 1080×1920 144 Hz monitor (and ~5 % → 20–45 % on the 5120-wide one). Setting the angle
/// and committing 30 times a second instead cost dwm.exe +0–2.5 % — inside the noise — and the
/// probe's own process 0.1–0.4 %. Still only at motion <see cref="MotionLevel.Full"/>, and paused
/// while a game presents.</para>
/// </summary>
internal sealed class AmbientVisual : IDisposable
{
    // seconds per turn for each CPU step; slow enough at rest to read as drift, not a spinner
    private static readonly double[] SpinPeriodS = [12, 6, 2.5];
    private const double BobPeriodS = 4;
    private const float BobRise = 2.5f;     // logical units

    private IDCompositionVisual? _visual;
    private IDCompositionSurface? _surface;
    // the spin: to the surface's centre, turn about the halo's own axis, then its pose
    private IDCompositionMatrixTransform3D? _toCentre, _pose;
    private IDCompositionRotateTransform3D? _rotate;
    private IDCompositionTransform3D? _group;
    private IDCompositionEffectGroup? _effect;
    private AmbientLoop? _loop;
    private double _scale;
    private float _pad;
    private float _opacity = 1;
    private float _top;
    private int _step;
    private double _phase;          // degrees for a spin, cycles for a bob
    private long _lastQpc;

    public bool Active => _visual != null;

    /// <summary>Make the visual match <paramref name="loop"/> (null = none): re-bake when the
    /// content, scale or opacity changed, otherwise leave it be. A new CPU step only changes the
    /// speed the next <see cref="Frame"/>s advance at, so the ring never jumps.</summary>
    public void Reconcile(Dx dx, IDCompositionVisual root, ID2D1DeviceContext dc, RenderContext rc,
        AmbientLoop? loop, double scale, float opacity, int step)
    {
        _step = Math.Clamp(step, 0, SpinPeriodS.Length - 1);
        if (loop == null) { Clear(root, dx); return; }
        bool rebake = !loop.SameContent(_loop) || Math.Abs(scale - _scale) > 1e-9 || Math.Abs(opacity - _opacity) > 1e-3;
        if (!rebake) return;
        // the old visual goes and the new one comes in the same commit: committing the removal on
        // its own let DWM compose a frame with neither, and the ring blinked out on every face change
        if (_visual != null) { root.RemoveVisual(_visual); Release(); }
        Bake(dx, root, dc, rc, loop, scale, opacity);
        SetUp(dx, loop);
        Frame(Stopwatch.GetTimestamp());
        dx.CompDevice.Commit();
    }

    private void SetUp(Dx dx, AmbientLoop loop)
    {
        float s = (float)_scale;
        _lastQpc = Stopwatch.GetTimestamp();
        if (loop.Kind == AmbientKind.Spin)
        {
            // the halo is baked flat-on, centred in its surface; a 3D transform turns it in its own
            // plane and then poses it exactly as the still draw does (AmbientLoop.Pose), and the
            // visual's offset puts its centre on the card
            var (c, r, pitch, roll, depth) = loop.SpinPose;
            float centre = r * s + _pad;
            _toCentre = dx.CompDevice.CreateMatrixTransform3D();
            var toCentre = Matrix4x4.CreateTranslation(-centre, -centre, 0);
            _toCentre.SetMatrix(ref toCentre);
            _rotate = dx.CompDevice.CreateRotateTransform3D();
            _rotate.SetAxisX(0); _rotate.SetAxisY(0); _rotate.SetAxisZ(1);
            _pose = dx.CompDevice.CreateMatrixTransform3D();
            var pose = AmbientLoop.Pose(pitch, roll, depth, r * s);
            _pose.SetMatrix(ref pose);
            _group = dx.CompDevice.CreateTransform3DGroup([_toCentre, _rotate, _pose], 3);
            _effect = dx.CompDevice.CreateEffectGroup();
            _effect.SetTransform3D(_group);
            _visual!.SetEffect(_effect);
            _visual.SetOffsetX(c.X * s);
            _visual.SetOffsetY(c.Y * s);
        }
        else _top = MathF.Round(loop.Bounds.Top * s - _pad);
    }

    /// <summary>Advance to <paramref name="nowQpc"/>: the spin's angle at its step's speed, or the
    /// bob's height. Sets values only — the caller commits once for every window.</summary>
    public void Frame(long nowQpc)
    {
        if (_visual == null || _loop == null) return;
        double dt = Math.Max(0, (nowQpc - _lastQpc) / (double)Stopwatch.Frequency);
        _lastQpc = nowQpc;
        if (_loop.Kind == AmbientKind.Spin)
        {
            _phase = (_phase + dt * 360 / SpinPeriodS[_step]) % 360;
            _rotate?.SetAngle((float)_phase);
        }
        else
        {
            // up and back, never below where the picture is drawn still
            _phase = (_phase + dt / BobPeriodS) % 1;
            float rise = BobRise * (float)_scale;
            _visual.SetOffsetY(_top - rise / 2 + rise / 2 * MathF.Sin((float)(_phase * 2 * Math.PI) + MathF.PI / 2));
        }
    }

    private void Bake(Dx dx, IDCompositionVisual root, ID2D1DeviceContext dc, RenderContext rc, AmbientLoop loop, double scale, float opacity)
    {
        _loop = loop;
        _scale = scale;
        _opacity = opacity;
        float s = (float)scale;
        var b = loop.Bounds;
        // room for the ring's stroke and notches, which sit outside its radius
        float pad = _pad = MathF.Ceiling(loop.Stroke * 2 * s) + 2;
        // a spin is baked as a flat-on circle and posed by the visual's 3D transform (SetUp), so
        // turning it reads as a tilted halo turning
        float wL = loop.Kind == AmbientKind.Spin ? loop.SpinPose.Radius * 2 : b.Width, hL = loop.Kind == AmbientKind.Spin ? wL : b.Height;
        uint w = (uint)Math.Ceiling(wL * s + 2 * pad), h = (uint)Math.Ceiling(hL * s + 2 * pad);

        dx.CompDevice.CreateSurface(w, h, Format.B8G8R8A8_UNorm, Vortice.DXGI.AlphaMode.Premultiplied, out _surface).CheckError();
        using (var dxgi = _surface!.BeginDraw<IDXGISurface>(null, out Int2 off))
        using (var bmp = dc.CreateBitmapFromDxgiSurface(dxgi, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw)))
        {
            dc.Target = bmp;
            dc.SetDpi(96, 96);
            dc.BeginDraw();
            Paint(dc, rc, loop, off, w, h, s, pad, opacity);
            dc.EndDraw();
            dc.Target = null;
        }
        _surface.EndDraw();

        _visual = dx.CompDevice.CreateVisual();
        _visual.SetContent(_surface);
        _visual.SetOffsetX(MathF.Round(b.Left * s - pad));    // a spin's is set by SetUp
        root.AddVisual(_visual, true, null);
    }

    /// <summary>
    /// Paint the loop's content into its <paramref name="w"/> × <paramref name="h"/> px region at
    /// <paramref name="off"/> of a target already in BeginDraw at 96 DPI.
    ///
    /// <para>A DComp surface is a region of a texture <b>shared by every surface on the device</b>
    /// (every widget's), so <paramref name="off"/> is rarely (0, 0) and nothing may land outside the
    /// region. The clip is pushed under the identity transform and held for the whole draw: the
    /// context still carries the panel's world transform from <c>Render</c> (base scale 1.7 and the
    /// entrance rise), and a clip pushed under it was scaled off the region — the clear missed part
    /// of this surface and wiped part of a neighbour's, so one card's ring showed another's leftover
    /// pixels and another card lost its ring altogether.</para>
    /// </summary>
    internal static void Paint(ID2D1DeviceContext dc, RenderContext rc, AmbientLoop loop, Int2 off, uint w, uint h,
        float s, float pad, float opacity)
    {
        var b = loop.Bounds;
        bool spin = loop.Kind == AmbientKind.Spin;
        float r = spin ? loop.SpinPose.Radius : 0;
        dc.Transform = Matrix3x2.Identity;
        dc.PushAxisAlignedClip(new Rect(off.X, off.Y, w, h), AntialiasMode.Aliased);
        dc.Clear(new Color4(0, 0, 0, 0));
        // a spin is painted flat-on about the origin, at the region's centre; a bob at its bounds
        dc.Transform = spin
            ? Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(off.X + pad + r * s, off.Y + pad + r * s)
            : Matrix3x2.CreateTranslation(-b.Left, -b.Top) * Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(off.X + pad, off.Y + pad);
        // the widget's opacity is a layer over the swapchain's drawing; this visual sits outside
        // it, so it is baked in here
        bool layer = opacity < 0.999f;
        if (layer) dc.PushLayer(new LayerParameters1 { ContentBounds = new Rect(-1e5f, -1e5f, 2e5f, 2e5f), Opacity = opacity }, null!);
        if (spin) DrawRing(rc, loop, r);
        else loop.Draw?.Invoke(rc);
        if (layer) dc.PopLayer();
        dc.Transform = Matrix3x2.Identity;
        dc.PopAxisAlignedClip();
    }

    /// <summary>The ring flat-on: a thin stroke, a few notches and one bright glint, so a turn is
    /// visible at all — a plain ring turning looks exactly like a ring standing still. The glint is
    /// the ring's own colour lifted toward white, not white, so it reads as part of the halo.</summary>
    private static void DrawRing(RenderContext rc, AmbientLoop loop, float r)
    {
        var c = Vector2.Zero;
        var glint = rc.Brush(Glint(loop.Color));
        if (loop.Flat != null)
        {
            loop.Flat(rc, c, r);
            var at = c + (loop.GlintAt ?? new Vector2(0, -1)) * r;
            rc.DC.FillEllipse(new Ellipse(at, loop.Stroke * 1.3f, loop.Stroke * 1.3f), glint);
            return;
        }
        rc.DC.DrawEllipse(new Ellipse(c, r, r), rc.Brush(loop.Color), loop.Stroke);
        var notch = rc.Brush(loop.Color);
        for (int i = 0; i < 6; i++)
        {
            float a = i * MathF.PI / 3;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            rc.DC.FillEllipse(new Ellipse(c + d * (r + loop.Stroke * 0.9f), loop.Stroke * 0.7f, loop.Stroke * 0.7f), notch);
        }
        rc.DC.FillEllipse(new Ellipse(c + new Vector2(0, -r), loop.Stroke * 1.3f, loop.Stroke * 1.3f), glint);
    }

    /// <summary>Share of the way from the halo colour to white that the glint sits at.</summary>
    internal const float GlintLift = 0.6f;

    internal static Color4 Glint(Color4 halo) => new(
        halo.R + (1 - halo.R) * GlintLift, halo.G + (1 - halo.G) * GlintLift, halo.B + (1 - halo.B) * GlintLift, halo.A);

    public void Clear(IDCompositionVisual? root, Dx dx)
    {
        if (_visual == null) return;
        root?.RemoveVisual(_visual);
        dx.CompDevice.Commit();
        Release();
    }

    private void Release()
    {
        _effect?.Dispose(); _effect = null;
        _group?.Dispose(); _group = null;
        _rotate?.Dispose(); _rotate = null;
        _toCentre?.Dispose(); _toCentre = null;
        _pose?.Dispose(); _pose = null;
        _visual?.Dispose(); _visual = null;
        _surface?.Dispose(); _surface = null;
        _loop = null;
        _phase = 0;
    }

    /// <summary>Drop everything without touching the tree (the window is releasing its graphics).</summary>
    public void Dispose() => Release();
}
