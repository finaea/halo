using Halo.Settings.Services;
using Halo.Shared.Config;

namespace Halo.Settings.ViewModels;

public sealed record ChoiceItem(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The General page: desktop behaviour and whole-config actions. The look itself lives on
/// the Appearance page (<see cref="AppearanceViewModel"/>).</summary>
public sealed class GeneralViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlySet<string> NoDirtyPaths = new HashSet<string>(StringComparer.Ordinal);

    private readonly LiveConfigService _config;
    private bool _applying;
    private bool _lockAll;
    private bool _snap;

    public bool LockAll
    {
        get => _lockAll;
        set
        {
            if (!Set(ref _lockAll, value) || _applying) return;
            _config.QueueSettings("lockAll", s => s.LockAll = value);
        }
    }

    public bool Snap
    {
        get => _snap;
        set
        {
            if (!Set(ref _snap, value) || _applying) return;
            _config.QueueSettings("snap", s => s.Snap = value);
        }
    }

    public GeneralViewModel(LiveConfigService config)
    {
        _config = config;
        ApplyFromStore(NoDirtyPaths);
        _config.ExternalChanged += Config_ExternalChanged;
    }

    public void RefreshFromCurrent() => ApplyFromStore(NoDirtyPaths);

    public void ResetEverything()
    {
        var defaults = new AppSettings();
        _applying = true;
        try { ApplySettings(defaults, NoDirtyPaths); }
        finally { _applying = false; }
        _config.QueueSettings("$", s => CopySettings(defaults, s), flushImmediately: true);
        _config.QueueWidgets("$", widgets =>
        {
            widgets.SchemaVersion = AppSettings.CurrentSchemaVersion;
            widgets.Widgets.Clear();
        }, flushImmediately: true);
    }

    private void Config_ExternalChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.File == ConfigFileKind.Settings) ApplyFromStore(e.DirtyPaths);
    }

    private void ApplyFromStore(IReadOnlySet<string> dirty)
    {
        _applying = true;
        try { ApplySettings(_config.Settings, dirty); }
        finally { _applying = false; }
    }

    private void ApplySettings(AppSettings settings, IReadOnlySet<string> dirty)
    {
        if (!Conflicts("lockAll", dirty)) LockAll = settings.LockAll;
        if (!Conflicts("snap", dirty)) Snap = settings.Snap;
    }

    private static bool Conflicts(string path, IReadOnlySet<string> dirty)
        => dirty.Any(candidate => candidate == "$" || candidate == path ||
            candidate.StartsWith(path + ".", StringComparison.Ordinal) || path.StartsWith(candidate + ".", StringComparison.Ordinal));

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        destination.SchemaVersion = source.SchemaVersion;
        destination.LockAll = source.LockAll;
        destination.Snap = source.Snap;
        destination.Appearance = source.Appearance;
        destination.Collector = source.Collector;
    }

    public void Dispose() => _config.ExternalChanged -= Config_ExternalChanged;
}
