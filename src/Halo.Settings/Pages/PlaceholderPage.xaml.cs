using System.Windows.Controls;

namespace Halo.Settings.Pages;

public partial class PlaceholderPage : UserControl, ISettingsPage, ISearchableSettingsPage
{
    public PlaceholderPage(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
    }

    public void OnEnter() { }
    public void OnLeave() { }
    public void ApplyFilter(string query) { }
}
