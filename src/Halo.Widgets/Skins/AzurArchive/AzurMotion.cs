using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// A crossfade between two looks of one thing on a card — a face and its next expression, a face
/// and Manjuu's sign (design §9: "a mood change swaps the image, a state transition, so it may
/// animate"). <typeparamref name="T"/> is a record naming everything the look depends on; its text is
/// the transition key, so it must hold state only, never a reading.
/// </summary>
internal sealed class Fade<T> where T : struct
{
    private readonly Transition _t = new();
    private T _old;

    public T Current { get; private set; }

    /// <summary>True while the old look is still on its way out.</summary>
    public bool Moving(PanelContext c) => !_t.Done(c);

    public void Step(PanelContext c, T look)
    {
        if (_t.Step(c, look.ToString()!)) _old = Current;
        Current = look;
    }

    /// <summary>Draw the current look, or both mid-fade. <paramref name="draw"/> gets whether it is
    /// the current look, so only that one claims a loop or answers a click.</summary>
    public void Draw(RenderContext rc, PanelContext c, Action<T, bool> draw)
    {
        float p = _t.Progress(c);
        if (p >= 1) { draw(Current, true); return; }
        Layer(rc, 1 - p, () => draw(_old, false));
        Layer(rc, p, () => draw(Current, true));
    }

    public static void Layer(RenderContext rc, float opacity, Action draw)
    {
        if (opacity <= 0.001f) return;
        // settled is the common case, and a layer per repaint would cost more than the drawing
        if (opacity >= 0.999f) { draw(); return; }
        rc.DC.PushLayer(new LayerParameters1 { ContentBounds = new Rect(-1e5f, -1e5f, 2e5f, 2e5f), Opacity = opacity }, null!);
        draw();
        rc.DC.PopLayer();
    }
}
