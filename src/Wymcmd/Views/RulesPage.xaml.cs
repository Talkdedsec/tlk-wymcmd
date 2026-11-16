using Wymcmd.Core.Model;
using Wymcmd.Core.Store;
using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class RulesPage
{
    public RulesPage(EventStore store, ProcEvent? seed)
    {
        InitializeComponent();
        DataContext = new RulesViewModel(store, seed);
    }
}
