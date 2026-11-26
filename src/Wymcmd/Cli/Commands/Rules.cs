using System.Text.Json;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Setup;
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

        try
        {
            return action switch
            {
                "add" => Add(options),
                "rm" or "remove" or "delete" => Remove(options),
                "enable" => Toggle(options, true),
                "disable" => Toggle(options, false),
                "test" => Test(options),
                "apply" => Apply(options),
                _ => Show(options)
            };
        }
        catch (UnauthorizedAccessException)
        {
            // Rules in the shared folder take an administrator, and the prompt was declined.
            ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
            return CommandRouter.ExitNeedsAdmin;
        }
    }

    /// <summary>
    /// The elevated half of a save: an unelevated wymcmd writes the new set to a temporary file
    /// and starts this to put it in the protected folder. Not listed in the help.
    /// </summary>
    private static int Apply(CliOptions options)
    {
        var file = options.Positional().Skip(1).FirstOrDefault();
        if (file is null || !File.Exists(file)) return CommandRouter.ExitError;
        if (!Elevation.IsElevated) throw new UnauthorizedAccessException();

        RuleSet.Parse(File.ReadAllText(file)).SaveCurrent();
        return CommandRouter.ExitOk;
    }

    private static int Show(CliOptions options)
    {
        var set = RuleSet.LoadCurrent();

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
        var set = RuleSet.LoadCurrent();

        var actionText = options.Value("--action") ?? "log";
        if (!Enum.TryParse<RuleAction>(actionText, true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(actionText, out _))
            throw new ArgumentException(Loc.T("cli.error.bad_argument", "--action", actionText));

        // A pattern that does not compile would be saved, never match, and warn on every launch.
        if (options.Value("--match") is { } pattern)
        {
            try { _ = new System.Text.RegularExpressions.Regex(pattern); }
            catch (ArgumentException) { throw new ArgumentException(Loc.T("cli.error.bad_argument", "--match", pattern)); }
        }

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
            Action = parsed,
            Note = options.Value("--note")
        };

        // "rules add --action kill" with nothing else would end every process that starts.
        if (Unconditional(rule) && rule.Action is RuleAction.Hide or RuleAction.Suspend or RuleAction.Kill or RuleAction.KillTree)
        {
            ConsoleHost.Bad(Loc.T("rules.needs_condition"));
            return CommandRouter.ExitError;
        }

        set.Rules.Add(rule);
        set.SaveCurrent();

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

        var set = RuleSet.LoadCurrent();
        var removed = set.Rules.RemoveAll(rule => rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        set.SaveCurrent();

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
        var set = RuleSet.LoadCurrent();
        var rule = set.Rules.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
        {
            ConsoleHost.Dim(Loc.T("cli.error.not_found"));
            return CommandRouter.ExitNotFound;
        }

        rule.Enabled = enabled;
        set.SaveCurrent();
        ConsoleHost.Good(Loc.T(enabled ? "rules.enabled" : "rules.disabled", rule.Id));
        return CommandRouter.ExitOk;
    }

    private static int Test(CliOptions options)
    {
        var set = RuleSet.LoadCurrent();
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
        return parts.Count == 0 ? Loc.T("rules.matches_everything") : string.Join(", ", parts);
    }

    private static bool Unconditional(Rule rule)
        => rule is
        {
            Image: null, ImagePath: null, CommandLine: null, Parent: null, Ancestor: null, Signer: null, User: null,
            Unsigned: null, HiddenWindow: null, Elevated: null, InTempPath: null, SessionId: null, MinRisk: 0
        };
}
