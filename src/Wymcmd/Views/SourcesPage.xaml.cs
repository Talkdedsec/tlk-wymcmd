using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class SourcesPage
{
    public SourcesPage()
    {
        InitializeComponent();
        DataContext = new SourcesViewModel();
    }
}
