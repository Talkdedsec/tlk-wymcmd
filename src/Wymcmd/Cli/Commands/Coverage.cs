using System.Text.Json;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;

namespace Wymcmd.Cli.Commands;

/// <summary>
/// When something was watching, and when nothing was. An answer that lands in a gap was pieced
/// together from what Windows happened to keep, and the difference matters enough to print.
/// </summary>
public static class Coverage
{
    public static int Run(CliOptions options)
    {
        var to = DateTime.Now;
        var from = to - List.ParseSpan(options.Value("--last") ?? "7d");

        var ledger = new WatchLedger();
        var spans = ledger.Spans(from, to);
        var gaps = ledger.Gaps(from, to);

        var watched = spans.Aggregate(TimeSpan.Zero, (total, s) => total + (s.To - s.From));
        var share = (to - from).TotalSeconds > 0 ? watched.TotalSeconds / (to - from).TotalSeconds : 0;
        var blackBox = BlackBoxInstaller.IsInstalled() && BlackBoxInstaller.IsEnabled() != false;

        if (options.Json)
        {
            ConsoleHost.Line(JsonSerializer.Serialize(new
            {
                from,
                to,
                watchedSeconds = (long)watched.TotalSeconds,
                share = Math.Round(share, 4),
                blackBox,
                spans = spans.Select(s => new { kind = s.Kind.ToString(), from = s.From, to = s.To, open = s.Open }),
                gaps = gaps.Select(g => new { from = g.From, to = g.To })
            }));

            return CommandRouter.ExitOk;
        }

        ConsoleHost.Strong(Loc.T("coverage.title"));
        ConsoleHost.Line();
        ConsoleHost.Line("  " + Loc.T("coverage.window", from, to));
        ConsoleHost.Line("  " + Loc.T("coverage.watched", Loc.Duration(watched), (int)Math.Round(share * 100)));
        ConsoleHost.Line("  " + Loc.T(blackBox ? "coverage.blackbox_on" : "coverage.blackbox_off"));
        ConsoleHost.Line();

        if (spans.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("coverage.never"));
            return CommandRouter.ExitOk;
        }

        ConsoleHost.Strong(Loc.T("coverage.spans_header"));
        foreach (var span in spans)
        {
            var kind = Loc.T("coverage.kind." + span.Kind.ToString().ToLowerInvariant());
            var ends = span.Open ? Loc.T("coverage.now") : When(span.To);
            ConsoleHost.Line($"{"  " + When(span.From) + " - " + ends,-44} {ConsoleHost.Color(kind, 90)}");
        }

        if (gaps.Count == 0) return CommandRouter.ExitOk;

        ConsoleHost.Line();
        ConsoleHost.Strong(Loc.T("coverage.gaps_header", gaps.Count));
        foreach (var gap in gaps)
            ConsoleHost.Line($"  {When(gap.From)} - {When(gap.To)}  {ConsoleHost.Color(Loc.Duration(gap.To - gap.From), 90)}");

        ConsoleHost.Line();
        ConsoleHost.Dim(Loc.T("coverage.gap_hint"));
        return CommandRouter.ExitOk;
    }

    private static string When(DateTime value) => value.ToString("g", Loc.Culture);
}
