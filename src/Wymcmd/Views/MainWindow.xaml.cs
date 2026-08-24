using System.Windows;
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

        Closed += (_, _) =>
        {
            _tray.Dispose();
            _model.Dispose();
        };
    }

    private void OnSourcesClick(object sender, RoutedEventArgs e)
        => new SourcesWindow { Owner = this }.ShowDialog();

    private void OnStatsClick(object sender, RoutedEventArgs e)
        => new StatsWindow(_model.Store) { Owner = this }.ShowDialog();

    private void OnTimelineClick(object sender, RoutedEventArgs e)
        => new TimelineWindow(_model.Store) { Owner = this }.ShowDialog();

    private void OnRulesClick(object sender, RoutedEventArgs e)
        => new RulesWindow(_model.Store, _model.Selected) { Owner = this }.ShowDialog();

    private void OnExportClick(object sender, RoutedEventArgs e) => _model.Export();
}
