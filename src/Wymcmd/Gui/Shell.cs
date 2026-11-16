using System.Windows;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Views;

namespace Wymcmd.Gui;

/// <summary>
/// The WPF entry point. There is no App.xaml: Program.Main owns startup so the same
/// executable can be a console tool, a window, or a service without a second entry point.
/// </summary>
public static class Shell
{
    public static int Run()
    {
        // A list and a few cards do not need the GPU, and asking for it loads every display
        // driver into the process: 150 MB on a laptop with two adapters, for nothing drawn faster.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute)
        });

        application.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("ui exception", e.Exception);
            MessageBox.Show(e.Exception.Message, "wymcmd", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        var window = new MainWindow();
        application.MainWindow = window;
        window.Show();

        return application.Run();
    }
}
