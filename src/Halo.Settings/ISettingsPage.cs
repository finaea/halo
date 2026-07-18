namespace Halo.Settings;

/// <summary>A left-nav page. OnEnter loads fresh state; OnLeave stops any timers.</summary>
public interface ISettingsPage
{
    void OnEnter();
    void OnLeave();
}
