using System.Net.NetworkInformation;

namespace Halo.Settings.Services;

/// <summary>
/// The machine's real network adapters, for anything editing
/// <c>settings.json &gt; collector.networkInterface</c>.
/// </summary>
/// <remarks>
/// This used to live on the General page, which offered a picker while the per-widget Data source
/// tab only had a free-text box. The General-page card is gone (the same four keys are edited
/// per-widget), so the enumeration lives here for the per-widget editor to use.
/// </remarks>
public static class NetworkAdapters
{
    /// <summary>Sentinel the collector understands as "follow the default route".</summary>
    public const string BestRoute = "Best";

    /// <summary>
    /// <see cref="BestRoute"/> followed by every adapter the collector will actually bind to,
    /// most useful first. Never throws: if Windows fails us the caller still gets the sentinel,
    /// and the editable combo it feeds still round-trips whatever name is already saved.
    /// </summary>
    /// <remarks>
    /// Only offers what <c>NetworkProvider.PickNic</c> accepts (Up, and neither Loopback nor
    /// Tunnel — <c>NetworkProvider.cs:59-61</c>). Offering anything else is worse than useless:
    /// <c>PickNic</c> matches the saved name against that same filter, so a Down or Tunnel
    /// adapter leaves <c>best</c> null and the network widget reports nothing, with no error
    /// anywhere and only an Info-level "network interface: none" in the log. Measured on one
    /// machine: 14 of the 35 names a naive enumeration offers are such dead ends.
    /// </remarks>
    public static IReadOnlyList<string> List()
    {
        var names = new List<string> { BestRoute };
        try
        {
            NetworkInterface[] usable = [.. NetworkInterface.GetAllNetworkInterfaces().Where(Bindable)];
            names.AddRange(usable
                .Where(item => !IsFilterInstance(item.Name, usable))
                // Mirrors PickNic's own tie-break: it prefers the adapter holding a default route.
                .OrderByDescending(item => GatewayCount(item) > 0)
                .ThenBy(item => item.Name)
                .Select(item => item.Name)
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
        }
        catch { /* Leaves the sentinel alone; the caller's editable combo still round-trips. */ }
        return names;
    }

    /// <summary>The exact predicate <c>NetworkProvider.PickNic</c> applies. Keep the two in step.</summary>
    private static bool Bindable(NetworkInterface item)
        => item.OperationalStatus == OperationalStatus.Up &&
           item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel);

    /// <summary>
    /// True for an NDIS lightweight-filter instance — a shadow of a real adapter that Windows
    /// stacks QoS, Npcap, WFP and the like onto. They are named "&lt;adapter&gt;-&lt;filter&gt;-0000"
    /// and are indistinguishable from the real thing in this API, so match structurally rather
    /// than against a list of vendor strings that would rot.
    /// </summary>
    /// <remarks>
    /// .NET 10 surfaces these where .NET Framework did not, so a PowerShell probe will not show
    /// them — measured in-process, one machine went from 35 interfaces to 6 once they were gone.
    /// The cost of a structural rule is that a real adapter genuinely named "&lt;other&gt;-&lt;suffix&gt;"
    /// would be hidden too; the combo is editable, so it can still be typed.
    /// </remarks>
    private static bool IsFilterInstance(string name, IReadOnlyList<NetworkInterface> candidates)
        => candidates.Any(other => other.Name.Length < name.Length &&
            name.StartsWith(other.Name + "-", StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// Gateway count, or 0 when Windows will not answer for this adapter. Per-adapter rather than
    /// around the whole query so one awkward interface cannot empty the list.
    /// </summary>
    private static int GatewayCount(NetworkInterface item)
    {
        try { return item.GetIPProperties().GatewayAddresses.Count; }
        catch { return 0; }
    }
}
