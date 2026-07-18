using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Halo.Settings;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // This machine is dual-GPU; WPF's HW path has been observed to compose a blank
        // (white) window here. A settings window doesn't need GPU rendering — force software.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Halo.Shared.Log.Error("settings unhandled", args.Exception);
                MessageBox.Show(args.Exception.ToString(), "Halo Settings error");
            }
            catch { }
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
