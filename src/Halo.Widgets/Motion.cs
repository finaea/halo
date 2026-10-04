using System.Diagnostics;
using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Widgets.Render;

namespace Halo.Widgets;

/// <summary>How much the widgets may move (tech plan §5 "Motion level").</summary>
public enum MotionLevel
{
    /// <summary>Nothing moves; not one repaint more than a Halo without motion.</summary>
    Off,
    /// <summary>State changes ease in on a short clock (entrances, warn steps, faces, N/A).</summary>
    Subtle,
    /// <summary>Subtle plus the one ambient loop per widget, which DWM animates.</summary>
    Full,
}

/// <summary>
/// The motion level and the curve every transition shares. <b>Data never animates</b> (tech plan
/// §5): numbers, bars and graph points snap to the latest reading, because a tween is a stretch of
/// values nobody measured — the stale-but-plausible the freshness contract forbids. Only chrome and
/// mood move.
/// </summary>
public static class Motion
{
    /// <summary>Transition length: inside the plan's 180–250 ms, long enough to read as a change and
    /// short enough to be over before the next 5 Hz tick lands on top of it.</summary>
    public const double TransitionS = 0.22;

    /// <summary>The transition clock's rate while something is moving: the widget monitor's 60 Hz.</summary>
    public const double FrameHz = 60;

    /// <summary><c>appearance.motion</c>, capped by Windows' "Animation effects": with them off
    /// nothing moves, whatever the setting says. An unknown value is the default, subtle.</summary>
    public static MotionLevel Resolve(string? setting, bool windowsAnimates)
    {
        if (!windowsAnimates) return MotionLevel.Off;
        return setting?.Trim().ToLowerInvariant() switch
        {
            "off" => MotionLevel.Off,
            "full" => MotionLevel.Full,
            _ => MotionLevel.Subtle,
        };
    }

    /// <summary>Windows' "Animation effects" (Settings › Accessibility › Visual effects). A call
    /// that fails reads as on — the setting is a preference, and the Halo default still applies.</summary>
    public static unsafe bool WindowsAnimates()
    {
        int on = 1;
        return !SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &on, 0) || on != 0;
    }

    /// <summary>Emphasised decelerate, cubic-bezier(0.05, 0.7, 0.1, 1): a quick start that settles
    /// gently, the curve the plan names for entrances.</summary>
    public static double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // solve x(u) = t for the curve parameter u (Newton, then bisection if it stalls), return y(u)
        const double x1 = 0.05, y1 = 0.7, x2 = 0.1, y2 = 1;
        static double B(double u, double a, double b) => 3 * a * u * (1 - u) * (1 - u) + 3 * b * u * u * (1 - u) + u * u * u;
        static double dB(double u, double a, double b) => 3 * a * (1 - u) * (1 - u) + 6 * (b - a) * u * (1 - u) + 3 * (1 - b) * u * u;
        double u = t;
        for (int i = 0; i < 8; i++)
        {
            double d = dB(u, x1, x2);
            if (Math.Abs(d) < 1e-6) break;
            u = Math.Clamp(u - (B(u, x1, x2) - t) / d, 0, 1);
        }
        if (Math.Abs(B(u, x1, x2) - t) > 1e-4)
        {
            double lo = 0, hi = 1;
            for (int i = 0; i < 30; i++) { u = (lo + hi) / 2; if (B(u, x1, x2) < t) lo = u; else hi = u; }
        }
        return B(u, y1, y2);
    }

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32", SetLastError = true)]
    private static extern unsafe bool SystemParametersInfoW(uint action, uint param, void* pvParam, uint winIni);
}

/// <summary>
/// One state an element eases between: a face, a warn step, a sign. <see cref="Step"/> is called
/// from <c>Update</c> with a key naming the state; a new key starts the clock and asks the window for
/// frames until it is over (<see cref="PanelContext.AnimateUntil"/>). The first key is not a change
/// — the window's own entrance covers it — and with motion off nothing is ever asked for, so a widget
/// repaints exactly as it did before motion existed.
/// <para>Only ever key this on state, never on a reading.</para>
/// </summary>
public sealed class Transition
{
    private string? _key;
    private long _startQpc;
    private long _durQpc;

    /// <summary>The key before the current one while the clock runs; null once it has finished.</summary>
    public string? From { get; private set; }

    /// <summary>Feed this tick's state. True when it changed (the caller keeps whatever it needs to
    /// draw the old state until <see cref="Done"/>).</summary>
    public bool Step(PanelContext ctx, string key)
    {
        if (_key == key) return false;
        string? from = _key;
        _key = key;
        if (from == null || ctx.Motion == MotionLevel.Off)
        {
            From = null;
            _startQpc = 0;
            return from != null;
        }
        From = from;
        ctx.TransitionStarts++;
        if (Log.Level <= LogLevel.Debug && ctx.TransitionKeys.Count < 8) ctx.TransitionKeys.Add($"{from}→{key}");
        _startQpc = ctx.NowQpc;
        _durQpc = (long)(Motion.TransitionS * Stopwatch.Frequency);
        ctx.AnimateUntil(_startQpc + _durQpc);
        return true;
    }

    /// <summary>Eased progress at the frame being drawn, 0 → 1; 1 when nothing is running.</summary>
    public float Progress(PanelContext ctx)
    {
        if (_startQpc == 0) return 1;
        double t = (double)(ctx.AnimQpc - _startQpc) / _durQpc;
        if (t >= 1) { _startQpc = 0; From = null; return 1; }
        return (float)Motion.Ease(t);
    }

    public bool Done(PanelContext ctx) => Progress(ctx) >= 1;
}
