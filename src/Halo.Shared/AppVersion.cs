using System.Reflection;

namespace Halo.Shared;

/// <summary>
/// The product version, from <c>Directory.Build.props &lt;Version&gt;</c> via the assembly's
/// informational version. Stamped into the metrics section header, published as
/// <c>sys.collector.version</c>, and shown on the Settings About page.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // the SDK appends "+<commit sha>" when the repo is a git checkout
        if (!string.IsNullOrEmpty(v))
        {
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
