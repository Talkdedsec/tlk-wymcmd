using System.Text.Json;
using Wymcmd.Core.Localization;

namespace Wymcmd.Cli.Commands;

/// <summary>
/// When something was recording, and when nothing was. An answer that lands in a blind stretch
/// was pieced together from what Windows happened to keep, and the difference is worth printing.
/// </summary>
public static class Coverage
{
    public static int Run(CliOptions options)
    {
        var to = DateTime.Now;
        var from = to - List.ParseSpan(options.Value("--last") ?? "7d");

        var report = Core.Coverage.CoverageReport.Build(from, to);
        var blind = report.Blind;
        var off = report.Gaps.Where(g => !g.MachineWasUp).Aggregate(TimeSpan.Zero, (t, g) => t + g.Length);

        if (options.Json)
        {
            ConsoleHost.Line(JsonSerializer.Serialize(new
            {
                from = report.From,
                to = report.To,
                watchedSeconds = (long)report.Watched.TotalSeconds,
                machineUpSeconds = (long)report.MachineUp.TotalSeconds,
                share = Math.Round(report.Share, 4),
                blackBox = report.BlackBoxOn,
                spans = report.Spans.Select(s => new
                {
                    kind = s.Kind.ToString(),
                    from = s.From,
                    to = s.To,
                    open = s.Open
                }),
                blind = blind.Select(g => new { from = g.From, to = g.To })
            }));

            return CommandRouter.ExitOk;
        }

        ConsoleHost.Strong(Loc.T("coverage.title"));
        ConsoleHost.Line();
        ConsoleHost.Line("  " + Loc.T("coverage.window", from, to));
        ConsoleHost.Line("  " + Loc.T("coverage.machine_up", Loc.Duration(report.MachineUp)));
        ConsoleHost.Line("  " + Loc.T("coverage.watched_of_up",
            Loc.Duration(report.Watched), (int)Math.Round(report.Share * 100)));
        ConsoleHost.Line("  " + Loc.T(report.BlackBoxOn ? "coverage.blackbox_on" : "coverage.blackbox_off"));
        ConsoleHost.Line();

        if (report.Spans.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("coverage.never"));
            return CommandRouter.ExitOk;
        }

        ConsoleHost.Strong(Loc.T("coverage.spans_header"));
        foreach (var span in report.Spans)
        {
            var kind = Loc.T("coverage.kind." + span.Kind.ToString().ToLowerInvariant());
            var ends = span.Open ? Loc.T("coverage.now") : When(span.To);
            ConsoleHost.Line($"{"  " + When(span.From) + " - " + ends,-44} {ConsoleHost.Color(kind, 90)}");
        }

        if (blind.Count > 0)
        {
            ConsoleHost.Line();
            ConsoleHost.Strong(Loc.T("coverage.blind_header", blind.Count));

            foreach (var gap in blind)
                ConsoleHost.Line($"  {When(gap.From)} - {When(gap.To)}  {ConsoleHost.Color(Loc.Duration(gap.Length), 90)}");
        }

        if (off > TimeSpan.FromMinutes(1))
        {
            ConsoleHost.Line();
            ConsoleHost.Dim(Loc.T("coverage.off_total", Loc.Duration(off)));
        }

        if (blind.Count == 0) return CommandRouter.ExitOk;

        ConsoleHost.Line();
        ConsoleHost.Dim(Loc.T("coverage.gap_hint"));
        return CommandRouter.ExitOk;
    }

    private static string When(DateTime value) => value.ToString("g", Loc.Culture);
}
