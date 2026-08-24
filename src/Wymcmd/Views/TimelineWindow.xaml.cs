using System.Windows;
using Wymcmd.Core.Store;
using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class TimelineWindow : Window
{
    public TimelineWindow(EventStore store)
    {
        InitializeComponent();
        DataContext = new TimelineViewModel(store);
    }
}
