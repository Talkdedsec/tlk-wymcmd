using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wymcmd.Cli.Commands;
using Wymcmd.Core.Forensic;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;

namespace Wymcmd.ViewModels;

/// <summary>
/// "Something flashed at 14:22." This rebuilds that minute from every source on the machine,
/// which is slower than reading the database and worth the wait exactly once per question.
/// </summary>
public sealed partial class TimelineViewModel(EventStore store) : ObservableObject
{
    public ObservableCollection<ProcEvent> Events { get; } = [];

    [ObservableProperty] private string _moment = "now";
    [ObservableProperty] private string _radius = "60s";
    [ObservableProperty] private bool _consoleOnly = true;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private ProcEvent? _selected;

    public string SourceHint => SourceInspector.IsAdministrator()
        ? Loc.T("gui.timeline_hint")
        : Loc.T("gui.timeline_hint_limited");

    [RelayCommand]
    private async Task Load()
    {
        var moment = Timeline.ParseMoment(Moment);
        if (moment is null)
        {
            Message = Loc.T("cli.error.bad_argument", "time", Moment);
            return;
        }

        var radius = Cli.Commands.List.ParseSpan(Radius);
        var consoleOnly = ConsoleOnly;

        Busy = true;
        Message = Loc.T("gui.timeline_working");

        try
        {
            var found = await Task.Run(() =>
            {
                var events = new ForensicHarvester(store).Around(moment.Value, radius);
                return consoleOnly ? events.Where(evt => evt.IsConsoleHost).ToList() : events.ToList();
            });

            Events.Clear();
            foreach (var evt in found.OrderByDescending(evt => evt.StartTime)) Events.Add(evt);

            Selected = Events.FirstOrDefault();
            Message = found.Count == 0
                ? Loc.T("cli.error.not_found")
                : Loc.T("timeline.header", moment.Value.ToString("g", Loc.Culture), Loc.Duration(radius));
        }
        finally
        {
            Busy = false;
        }
    }
}
