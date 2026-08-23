using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Service;
using Wymcmd.Core.Setup;

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
        _ => "doctor.missing"
    });
}

public sealed partial class SourcesViewModel : ObservableObject
{
    public SourcesViewModel() => Refresh();

    public ObservableCollection<SourceRow> Rows { get; } = [];

    [ObservableProperty] private string _blackBoxText = "";
    [ObservableProperty] private string _serviceText = "";
    [ObservableProperty] private string _message = "";

    [RelayCommand]
    private void Refresh()
    {
        Rows.Clear();
        foreach (var status in SourceInspector.Inspect()) Rows.Add(new SourceRow(status));

        BlackBoxText = BlackBoxInstaller.IsInstalled()
            ? Loc.T("blackbox.installed", BlackBoxInstaller.TraceSizeBytes() / (1024 * 1024))
            : Loc.T("blackbox.how_to_enable");

        ServiceText = WatchdogService.IsInstalled()
            ? Loc.T("service.state", WatchdogService.State() ?? "?")
            : Loc.T("service.not_installed");
    }

    /// <summary>These all shell out to our own elevated CLI, so the UAC prompt names the action.</summary>
    [RelayCommand]
    private void EnableSources() => RunElevated("sources", "enable");

    [RelayCommand]
    private void ToggleBlackBox()
        => RunElevated("blackbox", BlackBoxInstaller.IsInstalled() ? "off" : "on");

    [RelayCommand]
    private void ToggleService()
        => RunElevated("service", WatchdogService.IsInstalled() ? "uninstall" : "install");

    [RelayCommand]
    private void RemoveEverything() => RunElevated("uninstall", "--purge");

    private void RunElevated(params string[] arguments)
    {
        var code = Elevation.Relaunch([.. arguments, "--lang", Loc.Language]);
        Message = code switch
        {
            0 => Loc.T("gui.action_done"),
            null => Loc.T("cli.error.needs_admin"),
            _ => Loc.T("gui.action_failed", code.Value)
        };
        Refresh();
    }
}
