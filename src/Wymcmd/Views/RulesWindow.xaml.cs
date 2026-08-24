using System.Windows;
using Wymcmd.Core.Model;
using Wymcmd.Core.Store;
using Wymcmd.ViewModels;

namespace Wymcmd.Views;

public partial class RulesWindow : Window
{
    public RulesWindow(EventStore store, ProcEvent? seed)
    {
        InitializeComponent();
        DataContext = new RulesViewModel(store, seed);
    }
}
