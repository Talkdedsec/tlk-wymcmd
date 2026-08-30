using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wymcmd.Core.Coverage;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Service;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;

namespace Wymcmd.ViewModels;

public sealed partial class SourceRow(SourceStatus status) : ObservableObject
{
    public string Key { get; } = status.Key;
    public string Label { get; } = Loc.T("doctor." + status.Key);
    public SourceState State { get; } = status.State;
    public string? Detail { get; } = status.Detail;

    public string StateText => Loc.T(State switch
    {
        SourceState.Ok => "doctor.ok",
        SourceState.Degraded => "doctor.degraded",
        SourceState.Unknown => "doctor.unknown",
        _ => "doctor.missing"
    });
}

public sealed partial class SourcesViewModel : ObservableObject
{
    public SourcesViewModel() => _ = Refresh();

    public ObservableCollection<SourceRow> Rows { get; } = [];

    [ObservableProperty] private string _blackBoxText = "";
    [ObservableProperty] private string _serviceText = "";
    [ObservableProperty] private string _coverageText = "";
    [ObservableProperty] private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Idle))]
    private bool _busy;

    public bool Idle => !Busy;

    /// <summary>
    /// Every probe reaches for the event log, the service list or the registry, and the Security
    /// log one costs seconds on a machine that never had process auditing turned on. None of it
    /// belongs on the dispatcher: the window used to appear only once the slowest check came
    /// back, which on a large log was indistinguishable from a freeze.
    /// </summary>
    [RelayCommand]
    private async Task Refresh()
    {
        Busy = true;

        try
        {
            var (statuses, blackBox, service, coverage) = await Task.Run(() => (
                SourceInspector.Inspect(),
                BlackBoxInstaller.IsInstalled()
                    ? Loc.T("blackbox.installed", BlackBoxInstaller.TraceSizeBytes() / (1024 * 1024))
                    : Loc.T("blackbox.how_to_enable"),
                WatchdogService.IsInstalled()
                    ? Loc.T("service.state", WatchdogService.State() ?? "?")
                    : Loc.T("service.not_installed"),
                DescribeCoverage()));

            Rows.Clear();
            foreach (var status in statuses) Rows.Add(new SourceRow(status));

            BlackBoxText = blackBox;
            ServiceText = service;
            CoverageText = coverage;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>How much of the last week was actually recorded, and how much only looks like it.</summary>
    private static string DescribeCoverage()
    {
        try
        {
            var to = DateTime.Now;
            var report = CoverageReport.Build(to.AddDays(-7), to);

            return report.Spans.Count == 0
                ? Loc.T("coverage.never")
                : Loc.T("coverage.summary",
                    Loc.Duration(report.Watched),
                    (int)Math.Round(report.Share * 100),
                    report.Blind.Count);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>These all shell out to our own elevated CLI, so the UAC prompt names the action.</summary>
    [RelayCommand]
    private Task EnableSources() => RunElevated("sources", "enable");

    [RelayCommand]
    private Task ToggleBlackBox()
        => RunElevated("blackbox", BlackBoxInstaller.IsInstalled() ? "off" : "on");

    [RelayCommand]
    private Task ToggleService()
        => RunElevated("service", WatchdogService.IsInstalled() ? "uninstall" : "install");

    [RelayCommand]
    private Task RemoveEverything() => RunElevated("uninstall", "--purge");

    private async Task RunElevated(params string[] arguments)
    {
        Busy = true;

        // The child runs to completion and the UAC prompt alone can sit there for a while.
        var code = await Task.Run(() => Elevation.Relaunch([.. arguments, "--lang", Loc.Language]));

        Message = code switch
        {
            0 => Loc.T("gui.action_done"),
            null => Loc.T("cli.error.needs_admin"),
            _ => Loc.T("gui.action_failed", code.Value)
        };

        await Refresh();
    }
}
