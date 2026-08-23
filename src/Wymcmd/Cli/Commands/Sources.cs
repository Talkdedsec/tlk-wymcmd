using Wymcmd.Core.Localization;
using Wymcmd.Core.Setup;

namespace Wymcmd.Cli.Commands;

public static class Sources
{
    public static int Run(CliOptions options)
    {
        var action = options.Positional().FirstOrDefault()?.ToLowerInvariant() ?? "status";

        return action switch
        {
            "enable" => Enable(options),
            "disable" => Disable(),
            _ => Doctor.Run(options)
        };
    }

    private static int Enable(CliOptions options)
    {
        if (!Elevation.IsElevated)
        {
            ConsoleHost.Dim(Loc.T("sources.elevating"));
            var code = Elevation.Relaunch("sources", "enable", "--lang", Loc.Language);
            if (code is null)
            {
                ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
                return CommandRouter.ExitNeedsAdmin;
            }
            return code.Value;
        }

        var changed = AuditPolicySetup.EnableAll();
        if (changed.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("sources.already_on"));
            return CommandRouter.ExitOk;
        }

        foreach (var key in changed)
            ConsoleHost.Good(Loc.T("sources.enabled", Loc.T("doctor." + key)));

        ConsoleHost.Line();
        ConsoleHost.Dim(Loc.T("sources.note_retroactive"));
        return CommandRouter.ExitOk;
    }

    private static int Disable()
    {
        if (!Elevation.IsElevated)
        {
            var code = Elevation.Relaunch("sources", "disable", "--lang", Loc.Language);
            if (code is null)
            {
                ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
                return CommandRouter.ExitNeedsAdmin;
            }
            return code.Value;
        }

        var reverted = AuditPolicySetup.RevertAll();
        foreach (var key in reverted)
            ConsoleHost.Good(Loc.T("sources.disabled", Loc.T("doctor." + key)));

        if (reverted.Count == 0) ConsoleHost.Dim(Loc.T("sources.nothing_to_revert"));
        return CommandRouter.ExitOk;
    }
}
