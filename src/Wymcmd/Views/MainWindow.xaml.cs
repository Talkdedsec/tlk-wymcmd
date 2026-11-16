using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wymcmd.Core.Localization;
using Wymcmd.Gui;
using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();
    private readonly TrayIcon _tray;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;

        _tray = new TrayIcon(this, () => _model.PanicCommand.Execute(null));
        _model.Alert += evt => Dispatcher.BeginInvoke(() => _tray.Notify(evt));

        SourceInitialized += (_, _) => UseDarkTitleBar();

        Closed += (_, _) =>
        {
            _tray.Dispose();
            _model.Dispose();
        };
    }

    /// <summary>
    /// Every page is built when it is opened, so it always shows the database as it is now -
    /// the same thing the separate dialogs used to guarantee by being created per click.
    /// </summary>
    private void OnNav(object sender, RoutedEventArgs e)
    {
        // The first button is checked while the window is still being built.
        if (PageHost is null) return;

        var (title, page) = ((FrameworkElement)sender).Tag switch
        {
            "timeline" => ("gui.timeline_title", (System.Windows.Controls.UserControl?)new TimelinePage(_model.Store)),
            "rules" => ("gui.rules_title", new RulesPage(_model.Store, _model.Selected)),
            "stats" => ("stats.title", new StatsPage(_model.Store)),
            "sources" => ("gui.sources_title", new SourcesPage()),
            _ => ("gui.launches", null)
        };

        PageHost.Content = page;
        LaunchesPage.Visibility = page is null ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding($"[{title}]") { Source = LocProxy.Instance });
    }

    private void OnExportClick(object sender, RoutedEventArgs e) => _model.Export();

    // Without this the caption stays light grey above a near-black window.
    private void UseDarkTitleBar()
    {
        var dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
