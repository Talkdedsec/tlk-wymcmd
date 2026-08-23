using Wymcmd.Core.Actions;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Tree;

namespace Wymcmd.Cli.Commands;

public static class Kill
{
    public static int Run(CliOptions options)
    {
        var positional = options.Positional();
        if (positional.Length == 0 || !int.TryParse(positional[0], out var pid))
        {
            ConsoleHost.Bad(Loc.T("cli.error.bad_argument", "pid", positional.FirstOrDefault() ?? ""));
            return CommandRouter.ExitError;
        }

        ActionResult result;
        if (options.Has("--tree"))
        {
            var tree = new ProcessTree();
            tree.Seed();
            result = ProcessActions.KillTree(tree, pid);
        }
        else
        {
            result = ProcessActions.Kill(pid);
        }

        switch (result.Outcome)
        {
            case ActionOutcome.Done:
                ConsoleHost.Good(Loc.T("kill.done", pid, result.Affected));
                return CommandRouter.ExitOk;

            case ActionOutcome.Protected:
                ConsoleHost.Bad(Loc.T("kill.protected", pid));
                return CommandRouter.ExitError;

            case ActionOutcome.AccessDenied:
                ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
                return CommandRouter.ExitNeedsAdmin;

            case ActionOutcome.NotFound:
                ConsoleHost.Dim(Loc.T("cli.error.not_found"));
                return CommandRouter.ExitNotFound;

            default:
                ConsoleHost.Bad(Loc.T("kill.failed", pid, result.Detail ?? ""));
                return CommandRouter.ExitError;
        }
    }
}
