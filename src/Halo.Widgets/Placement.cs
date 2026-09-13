namespace Halo.Widgets;

/// <summary>One monitor's work area and DPI, as the widgets see it.</summary>
public readonly record struct MonitorInfo(string Device, int X, int Y, int W, int H, double Dpi, bool Primary)
{
    /// <summary>Used only when Windows reports no monitors at all (session switching).</summary>
    public static readonly MonitorInfo None = new("", 0, 0, 1920, 1080, 96, true);
}

/// <summary>
/// The scale an <c>"auto"</c> appearance resolves to on a given monitor (hardware plan H7).
///
/// <code>auto(monitor) = round0.05( clamp( 1.7 × min(workW, workH) / (dpi/96) / 1080, 1.0, 3.5 ) )</code>
///
/// A panel then takes the same fraction of the screen everywhere — 32 % of the short side, the
/// reference being a 1080-wide portrait monitor at 1.7. A fixed 1.7 is 18 % of a 1080p width but
/// 9 % of 4K, which is why it cannot be the shipped default.
/// </summary>
public static class AutoScale
{
    public const double Reference = 1.7;
    public const double ReferenceShortSideDip = 1080;
    public const double Min = 1.0;
    public const double Max = 3.5;

    public static double For(MonitorInfo m)
    {
        double shortSideDip = Math.Min(m.W, m.H) / (m.Dpi <= 0 ? 1 : m.Dpi / 96.0);
        double s = Math.Clamp(Reference * shortSideDip / ReferenceShortSideDip, Min, Max);
        return Math.Round(s * 20) / 20;     // to the nearest 0.05
    }
}

/// <summary>
/// The column packer behind the first-run layout and the displaced-widget layout (hardware plan
/// H6). Columns fill right to left, widgets stack top to bottom with an 8 px gap, and rectangles
/// that are already occupied (widgets whose own monitor is present) are stepped over.
///
/// This is what replaces the old behaviour where a missing monitor sent every widget through
/// <c>ResolveMonitorWorkArea</c>'s monitor-0 fallback and then <c>KeepOnScreen</c>'s clamp, which
/// stacked the whole layout in one corner on top of itself.
/// </summary>
public static class ColumnPacker
{
    public const int Gap = 8;

    public readonly record struct Item(string Id, int W, int H);
    public readonly record struct Placed(string Id, int X, int Y);
    public readonly record struct Box(int X, int Y, int W, int H)
    {
        public bool Intersects(int x, int y, int w, int h)
            => x < X + W && X < x + w && y < Y + H && Y < y + h;
    }

    /// <param name="occupied">Screen rects already taken (widgets that are not being packed).</param>
    public static List<Placed> Pack(MonitorInfo monitor, IReadOnlyList<Item> items, IReadOnlyList<Box> occupied)
    {
        var result = new List<Placed>(items.Count);
        int top = monitor.Y + Gap;
        // Inset from the bottom to match the top and right. Flush with the work area a widget sits
        // against the taskbar, and KeepOnScreen then pulls it back up into the one above it.
        int bottom = monitor.Y + monitor.H - Gap;
        int left = monitor.X + Gap;
        int colRight = monitor.X + monitor.W - Gap;
        int colWidth = 0;
        int y = top;

        // Is there room for another column of width w to the left of the current one? When there
        // is not, the pass keeps stacking downward instead: a column running past the bottom of
        // the work area is recoverable (drag it back), two panels drawn on top of each other are
        // not, and marching columns off the left edge only feeds them back to KeepOnScreen, which
        // clamps them all to the same edge — overlap again.
        bool CanWrap(int w) => colRight - colWidth - Gap - w >= left;

        foreach (var item in items)
        {
            int w = Math.Max(1, item.W);
            int h = Math.Max(1, item.H);

            // no room left in this column → start a new one to the left
            if (y + h > bottom && y > top && CanWrap(w))
            {
                colRight -= colWidth + Gap;
                colWidth = 0;
                y = top;
            }

            int x = colRight - w;
            // step past anything already sitting there; bounded so a pathological set of
            // occupied rects can never spin
            for (int guard = 0; guard < 64; guard++)
            {
                Box? hit = null;
                foreach (var o in occupied)
                    if (o.Intersects(x, y, w, h)) { hit = o; break; }
                if (hit == null) break;
                y = hit.Value.Y + hit.Value.H + Gap;
                if (y + h > bottom)
                {
                    if (!CanWrap(w)) break;   // out of monitor: stack past the bottom, never overlap
                    colRight -= Math.Max(colWidth, w) + Gap;
                    colWidth = 0;
                    y = top;
                    x = colRight - w;
                }
            }

            result.Add(new Placed(item.Id, x, y));
            y += h + Gap;
            if (w > colWidth) colWidth = w;
        }
        return result;
    }
}
