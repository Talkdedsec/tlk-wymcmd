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
