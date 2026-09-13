using System.Security.Principal;

namespace Halo.Shared;

/// <summary>
/// Is this process running with administrator rights? The collector degrades gracefully without
/// them (LHM CPU/SuperIO/Storage and PresentMon's ETW session need elevation), and the answer is
/// published as <c>sys.elevated</c> so the UI can explain the N/A rows instead of just showing them.
/// </summary>
public static class Elevation
{
    public static bool IsElevated { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
