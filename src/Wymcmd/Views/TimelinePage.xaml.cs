using Wymcmd.Core.Store;
using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class TimelinePage
{
    public TimelinePage(EventStore store)
    {
        InitializeComponent();
        DataContext = new TimelineViewModel(store);
    }
}
