using System.Text.Json;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Setup;

namespace Wymcmd.Cli.Commands;

public static class Doctor
{
    public static int Run(CliOptions options)
    {
        var statuses = SourceInspector.Inspect();

        if (options.Json)
        {
            ConsoleHost.Line(JsonSerializer.Serialize(statuses.Select(s => new
            {
                key = s.Key,
                state = s.State.ToString(),
                detail = s.Detail
            })));
            return statuses.Any(s => s.State == SourceState.Missing)
                ? CommandRouter.ExitSourceDisabled
                : CommandRouter.ExitOk;
        }

        ConsoleHost.Strong(Loc.T("doctor.title"));
        ConsoleHost.Line();

        foreach (var status in statuses)
        {
            var label = Loc.T("doctor." + status.Key);
            var (text, color) = status.State switch
            {
                SourceState.Ok => (Loc.T("doctor.ok"), 92),
                SourceState.Degraded => (Loc.T("doctor.degraded"), 93),
                SourceState.Unknown => (Loc.T("doctor.unknown"), 90),
                _ => (Loc.T("doctor.missing"), 91)
            };

            var detail = status.Detail is { Length: > 0 } ? ConsoleHost.Color("  " + status.Detail, 90) : "";
            ConsoleHost.Line($"  {label.PadRight(34)} {ConsoleHost.Color(text, color)}{detail}");
        }

        if (statuses.Any(s => s.State != SourceState.Ok))
        {
            ConsoleHost.Line();
            ConsoleHost.Dim(Loc.T("doctor.hint_enable"));
        }

        return statuses.Any(s => s.State == SourceState.Missing)
            ? CommandRouter.ExitSourceDisabled
            : CommandRouter.ExitOk;
    }
}
