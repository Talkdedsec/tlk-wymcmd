using Wymcmd.Core.Store;
using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class StatsPage
{
    public StatsPage(EventStore store)
    {
        InitializeComponent();
        DataContext = new StatsViewModel(store);
    }
}
