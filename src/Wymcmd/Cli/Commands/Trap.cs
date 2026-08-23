using Wymcmd.Core.Capture;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Store;
using Wymcmd.Core.Tree;
using Wymcmd.Core.Why;

namespace Wymcmd.Cli.Commands;

/// <summary>
/// "Catch it if it happens again" without leaving anything running forever: a temporary
/// watch with a deadline that reports what it caught and then closes itself.
/// </summary>
public static class Trap
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var duration = List.ParseSpan(options.Value("--for") ?? "1h");
        var once = options.Has("--once");

        var rule = new Rule
        {
            Name = "trap",
            Image = options.Value("--image"),
            ImagePath = options.Value("--path"),
            CommandLine = options.Value("--match"),
            Parent = options.Value("--parent"),
            Unsigned = options.Has("--unsigned") ? true : null,
            HiddenWindow = options.Has("--hidden-only") ? true : null,
            MinRisk = options.Number("--risk", 0),
            Action = ParseAction(options.Value("--action"))
        };

        if (rule.Image is null && rule.CommandLine is null && rule.Parent is null &&
            rule.Unsigned is null && rule.HiddenWindow is null && rule.MinRisk == 0)
        {
            // No filter given: watch consoles, the thing this tool is named after.
            rule.Image = "cmd.exe";
        }

        await using var store = new EventStore();
        var tree = new ProcessTree();
        var rules = new RuleSet { Rules = [rule] };

        await using var engine = new CaptureEngine(store, tree, new AttributionEngine(new AutostartIndex()), rules)
        {
            EnforceRules = rule.Action != RuleAction.Log
        };

        var caught = new List<ProcEvent>();
        var finished = new TaskCompletionSource();

        engine.RuleFired += (evt, _) =>
        {
            caught.Add(evt);
            ConsoleHost.Line(EventFormatter.OneLine(evt));
            if (once) finished.TrySetResult();
        };

        try
        {
            engine.Start();
        }
        catch (UnauthorizedAccessException)
        {
            ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
            return CommandRouter.ExitNeedsAdmin;
        }

        ConsoleHost.Dim(Loc.T("trap.armed", Describe(rule), Loc.Duration(duration), rule.Action.ToString().ToLowerInvariant()));
        ConsoleHost.Line();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            finished.TrySetResult();
        };

        await Task.WhenAny(finished.Task, Task.Delay(duration));
        engine.Stop();

        ConsoleHost.Line();
        if (caught.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("trap.nothing"));
            return CommandRouter.ExitNotFound;
        }

        ConsoleHost.Good(Loc.T("trap.caught", caught.Count));
        EventFormatter.Detail(caught[^1]);
        return CommandRouter.ExitOk;
    }

    private static RuleAction ParseAction(string? value) => value?.ToLowerInvariant() switch
    {
        "kill" => RuleAction.Kill,
        "killtree" or "kill-tree" => RuleAction.KillTree,
        "suspend" => RuleAction.Suspend,
        "hide" => RuleAction.Hide,
        "notify" => RuleAction.Notify,
        _ => RuleAction.Log
    };

    private static string Describe(Rule rule)
    {
        var parts = new List<string>();
        if (rule.Image is not null) parts.Add(rule.Image);
        if (rule.Parent is not null) parts.Add("parent=" + rule.Parent);
        if (rule.CommandLine is not null) parts.Add("cmdline~" + rule.CommandLine);
        if (rule.Unsigned == true) parts.Add("unsigned");
        if (rule.HiddenWindow == true) parts.Add("hidden");
        if (rule.MinRisk > 0) parts.Add("risk>=" + rule.MinRisk);
        return string.Join(", ", parts);
    }
}
