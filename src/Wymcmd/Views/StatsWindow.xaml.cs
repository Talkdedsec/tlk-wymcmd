using System.Windows;
using Wymcmd.Core.Store;
using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class StatsWindow : Window
{
    public StatsWindow(EventStore store)
    {
        InitializeComponent();
        DataContext = new StatsViewModel(store);
    }
}
