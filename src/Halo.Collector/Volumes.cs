namespace Halo.Collector;

/// <summary>
/// The volumes the collector publishes. Discovery replaces the v1 <c>driveLetters</c> config
/// list: the collector publishes every local volume it finds and widgets pick which ones to
/// show (hardware plan H1). Network and optical drives are excluded — they have no SMART
/// temperature and their "free space" is somebody else's business.
/// </summary>
internal static class Volumes
{
    private static readonly Halo.Shared.ComponentLog Log2 = Halo.Shared.Log.For("volumes");

    public static List<char> Local()
    {
        var letters = new List<char>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!d.IsReady) continue;                       // card reader with no card
                if (d.Name.Length == 0) continue;
                char c = char.ToUpperInvariant(d.Name[0]);
                if (c is >= 'A' and <= 'Z' && !letters.Contains(c)) letters.Add(c);
            }
        }
        catch (Exception ex)
        {
            Log2.Warn($"volume enumeration failed: {ex.Message}");
        }
        letters.Sort();
        return letters;
    }

    /// <summary>Stable key for change detection ("CDEF").</summary>
    public static string Key(IEnumerable<char> letters) => string.Concat(letters);
}
