using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Store;

namespace Wymcmd.ViewModels;

public sealed record StatBar(string Label, int Count, double Share);

public sealed partial class StatsViewModel : ObservableObject
{
    private readonly EventStore _store;

    public StatsViewModel(EventStore store)
    {
        _store = store;
        Load();
    }

    public ObservableCollection<StatBar> Launchers { get; } = [];
    public ObservableCollection<StatBar> Images { get; } = [];
    public ObservableCollection<StatBar> Commands { get; } = [];
    public ObservableCollection<StatBar> Hours { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _days = 7;

    [RelayCommand]
    private void Load()
    {
        var stats = EventStats.Collect(_store, TimeSpan.FromDays(Days));

        Fill(Launchers, stats.TopLaunchers);
        Fill(Images, stats.TopImages);
        Fill(Commands, stats.TopCommands);
        Fill(Hours, stats.ByHour);

        Summary = Loc.T("stats.summary", stats.Total, stats.Consoles, stats.Hidden, stats.Unsigned, Days);
    }

    partial void OnDaysChanged(int value) => Load();

    private static void Fill(ObservableCollection<StatBar> target, IReadOnlyList<Tally> tallies)
    {
        target.Clear();
        var peak = tallies.Count == 0 ? 1 : Math.Max(1, tallies.Max(t => t.Count));

        foreach (var tally in tallies)
        {
            var label = tally.Label.Length > 70 ? tally.Label[..67] + "..." : tally.Label;
            target.Add(new StatBar(label, tally.Count, (double)tally.Count / peak));
        }
    }
}
