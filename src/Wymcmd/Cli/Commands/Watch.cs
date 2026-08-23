using Wymcmd.Core.Capture;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Store;
using Wymcmd.Core.Tree;
using Wymcmd.Core.Why;

namespace Wymcmd.Cli.Commands;

public static class Watch
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var consoleOnly = options.Has("--console");
        var hiddenOnly = options.Has("--hidden");
        var minRisk = options.Number("--risk", 0);
        var enforce = options.Has("--enforce");

        await using var store = new EventStore();
        var tree = new ProcessTree();
        var attribution = new AttributionEngine(new AutostartIndex());
        var rules = RuleSet.Load(AppPaths.Rules);

        await using var engine = new CaptureEngine(store, tree, attribution, rules) { EnforceRules = enforce };

        var seen = new HashSet<(int, DateTime)>();
        engine.Observed += evt =>
        {
            if (consoleOnly && !evt.IsConsoleHost) return;
            if (hiddenOnly && evt.Window != WindowVisibility.Hidden) return;
            if (evt.Risk < minRisk) return;
            if (!seen.Add((evt.Pid, evt.StartTime))) return;

            ConsoleHost.Line(options.Json ? EventFormatter.Json(evt) : EventFormatter.OneLine(evt));
        };

        engine.RuleFired += (evt, rule) =>
            ConsoleHost.Line(ConsoleHost.Color($"  rule '{rule.Name}' -> {rule.Action} ({evt.ImageName} {evt.Pid})", 93));

        try
        {
            engine.Start();
        }
        catch (UnauthorizedAccessException)
        {
            ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
            return CommandRouter.ExitNeedsAdmin;
        }

        if (!options.Json)
        {
            ConsoleHost.Dim(engine.Lossless ? Loc.T("watch.started") : Loc.T("watch.degraded"));
            ConsoleHost.Line();
        }

        var stopping = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.TrySetResult();
        };

        await stopping.Task;
        engine.Stop();

        if (!options.Json) ConsoleHost.Dim(Loc.T("watch.stopped"));
        return CommandRouter.ExitOk;
    }
}
