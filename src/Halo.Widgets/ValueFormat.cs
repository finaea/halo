using System.Globalization;

namespace Halo.Widgets;

/// <summary>Rainmeter-compatible number formatting (AutoScale, decimals, percent).</summary>
public static class ValueFormat
{
    /// <summary>
    /// Rainmeter AutoScale=1: scale by 1024 steps; suffix " k"/" M"/" G"/" T" appended with
    /// leading space (the skins then append "B" or "B/s" in the template).
    /// </summary>
    public static string AutoScale(double value, int decimals = 1)
    {
        string suffix = "";
        double v = Math.Abs(value);
        double sign = value < 0 ? -1 : 1;
        if (v >= 1024L * 1024 * 1024 * 1024) { v /= 1024L * 1024 * 1024 * 1024; suffix = " T"; }
        else if (v >= 1024 * 1024 * 1024) { v /= 1024 * 1024 * 1024; suffix = " G"; }
        else if (v >= 1024 * 1024) { v /= 1024 * 1024; suffix = " M"; }
        else if (v >= 1024) { v /= 1024; suffix = " k"; }
        else suffix = " ";
        return (sign * v).ToString("F" + decimals, CultureInfo.InvariantCulture) + suffix;
    }

    public static string Fixed(double value, int decimals)
        => value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    public static string Int0(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);

    /// <summary>Uptime "0d 3h 44m 20" (Rainformer format "%4!i!d %3!i!h %2!i!m %1!i!").</summary>
    public static string Uptime(double seconds)
    {
        long s = (long)seconds;
        long d = s / 86400, h = s % 86400 / 3600, m = s % 3600 / 60, sec = s % 60;
        return $"{d}d {h}h {m}m {sec}";
    }
}
