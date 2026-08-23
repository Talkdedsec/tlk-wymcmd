using System.Windows;
using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class SourcesWindow : Window
{
    public SourcesWindow()
    {
        InitializeComponent();
        DataContext = new SourcesViewModel();
    }
}
