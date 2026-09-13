namespace Halo.Settings;

/// <summary>
/// Vestigial. The "Find a setting" box and all of its filtering were removed; nothing calls this
/// any more and nothing should implement it. It survives only so <c>WidgetsPage</c> — owned by a
/// different branch — keeps compiling until that page drops the interface and its ApplyFilter.
/// Delete this file at that point.
/// </summary>
public interface ISearchableSettingsPage;
