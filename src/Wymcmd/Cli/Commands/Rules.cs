using System.Text.Json;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Store;

namespace Wymcmd.Cli.Commands;

public static class Rules
{
    public static int Run(CliOptions options)
    {
        var action = options.Positional(
            "--image", "--path", "--match", "--parent", "--ancestor", "--signer", "--user",
            "--action", "--name", "--priority", "--risk", "--last")
            .FirstOrDefault()?.ToLowerInvariant() ?? "list";

        return action switch
        {
            "add" => Add(options),
            "rm" or "remove" or "delete" => Remove(options),
            "enable" => Toggle(options, true),
            "disable" => Toggle(options, false),
            "test" => Test(options),
            _ => Show(options)
        };
    }

    private static int Show(CliOptions options)
    {
        var set = RuleSet.Load(AppPaths.Rules);

        if (options.Json)
        {
            ConsoleHost.Line(JsonSerializer.Serialize(set.Rules));
            return CommandRouter.ExitOk;
        }

        if (set.Rules.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("rules.empty"));
            return CommandRouter.ExitOk;
        }

        foreach (var rule in set.Rules.OrderBy(r => r.Priority))
        {
            var state = rule.Enabled ? ConsoleHost.Color("on ", 92) : ConsoleHost.Color("off", 90);
            ConsoleHost.Line($"{rule.Id}  {state}  {rule.Priority,4}  {rule.Action,-8}  {rule.Name}");
            ConsoleHost.Dim("      " + Describe(rule));
        }

        return CommandRouter.ExitOk;
    }

    private static int Add(CliOptions options)
    {
        var set = RuleSet.Load(AppPaths.Rules);

        var rule = new Rule
        {
            Name = options.Value("--name") ?? options.Value("--image") ?? "rule",
            Image = options.Value("--image"),
            ImagePath = options.Value("--path"),
            CommandLine = options.Value("--match"),
            Parent = options.Value("--parent"),
            Ancestor = options.Value("--ancestor"),
            Signer = options.Value("--signer"),
            User = options.Value("--user"),
            Unsigned = options.Has("--unsigned") ? true : null,
            HiddenWindow = options.Has("--hidden") ? true : null,
            Elevated = options.Has("--elevated") ? true : null,
            InTempPath = options.Has("--temp") ? true : null,
            MinRisk = options.Number("--risk", 0),
            Priority = options.Number("--priority", 100),
            Action = Enum.TryParse<RuleAction>(options.Value("--action") ?? "log", true, out var parsed)
                ? parsed
                : RuleAction.Log,
            Note = options.Value("--note")
        };

        set.Rules.Add(rule);
        set.Save(AppPaths.Rules);

        ConsoleHost.Good(Loc.T("rules.added", rule.Id, rule.Action.ToString().ToLowerInvariant()));
        ConsoleHost.Dim("      " + Describe(rule));

        // Show what this rule would have done, so nobody arms a rule blindly.
        var wouldMatch = DryRun(rule, Commands.List.ParseSpan(options.Value("--last") ?? "24h"));
        ConsoleHost.Line();
        ConsoleHost.Dim(Loc.T("rules.dry_run", wouldMatch.Count));
        foreach (var evt in wouldMatch.Take(10)) ConsoleHost.Line("  " + EventFormatter.OneLine(evt));

        return CommandRouter.ExitOk;
    }

    private static int Remove(CliOptions options)
    {
        var id = options.Positional().Skip(1).FirstOrDefault();
        if (id is null) return CommandRouter.ExitError;

        var set = RuleSet.Load(AppPaths.Rules);
        var removed = set.Rules.RemoveAll(rule => rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        set.Save(AppPaths.Rules);

        if (removed == 0)
        {
            ConsoleHost.Dim(Loc.T("cli.error.not_found"));
            return CommandRouter.ExitNotFound;
        }

        ConsoleHost.Good(Loc.T("rules.removed", id));
        return CommandRouter.ExitOk;
    }

    private static int Toggle(CliOptions options, bool enabled)
    {
        var id = options.Positional().Skip(1).FirstOrDefault();
        var set = RuleSet.Load(AppPaths.Rules);
        var rule = set.Rules.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
        {
            ConsoleHost.Dim(Loc.T("cli.error.not_found"));
            return CommandRouter.ExitNotFound;
        }

        rule.Enabled = enabled;
        set.Save(AppPaths.Rules);
        ConsoleHost.Good(Loc.T(enabled ? "rules.enabled" : "rules.disabled", rule.Id));
        return CommandRouter.ExitOk;
    }

    private static int Test(CliOptions options)
    {
        var set = RuleSet.Load(AppPaths.Rules);
        var window = Commands.List.ParseSpan(options.Value("--last") ?? "24h");

        if (set.Rules.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("rules.empty"));
            return CommandRouter.ExitOk;
        }

        foreach (var rule in set.Rules.OrderBy(r => r.Priority))
        {
            var matches = DryRun(rule, window);
            ConsoleHost.Line($"{rule.Id}  {rule.Name}");
            ConsoleHost.Dim("      " + Loc.T("rules.dry_run", matches.Count));
            foreach (var evt in matches.Take(5)) ConsoleHost.Line("      " + EventFormatter.OneLine(evt));
        }

        return CommandRouter.ExitOk;
    }

    private static List<ProcEvent> DryRun(Rule rule, TimeSpan window)
    {
        using var store = new EventStore();
        return store
            .Query(new EventFilter { From = DateTime.Now - window, Limit = 5000 })
            .Where(rule.Matches)
            .ToList();
    }

    private static string Describe(Rule rule)
    {
        var parts = new List<string>();
        if (rule.Image is not null) parts.Add("image=" + rule.Image);
        if (rule.ImagePath is not null) parts.Add("path=" + rule.ImagePath);
        if (rule.CommandLine is not null) parts.Add("cmdline~" + rule.CommandLine);
        if (rule.Parent is not null) parts.Add("parent=" + rule.Parent);
        if (rule.Ancestor is not null) parts.Add("ancestor=" + rule.Ancestor);
        if (rule.Signer is not null) parts.Add("signer=" + rule.Signer);
        if (rule.User is not null) parts.Add("user=" + rule.User);
        if (rule.Unsigned == true) parts.Add("unsigned");
        if (rule.HiddenWindow == true) parts.Add("hidden");
        if (rule.Elevated == true) parts.Add("elevated");
        if (rule.InTempPath == true) parts.Add("temp-path");
        if (rule.MinRisk > 0) parts.Add("risk>=" + rule.MinRisk);
        return parts.Count == 0 ? "(matches everything)" : string.Join(", ", parts);
    }
}
