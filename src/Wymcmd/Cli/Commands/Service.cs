using Wymcmd.Core.Localization;
using Wymcmd.Core.Service;
using Wymcmd.Core.Setup;

namespace Wymcmd.Cli.Commands;

public static class Service
{
    public static int Run(CliOptions options)
    {
        var action = options.Positional().FirstOrDefault()?.ToLowerInvariant() ?? "status";

        if (action != "status" && !Elevation.IsElevated)
        {
            var code = Elevation.Relaunch("service", action, "--lang", Loc.Language);
            if (code is null)
            {
                ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
                return CommandRouter.ExitNeedsAdmin;
            }
            return code.Value;
        }

        return action switch
        {
            "install" => Report(WatchdogService.Install(), "service.installed"),
            "uninstall" => Report(WatchdogService.Uninstall(), "service.uninstalled"),
            "start" => Report(WatchdogService.StartService(), "service.started"),
            "stop" => Report(WatchdogService.StopService(), "service.stopped"),
            _ => Status()
        };
    }

    private static int Status()
    {
        if (!WatchdogService.IsInstalled())
        {
            ConsoleHost.Dim(Loc.T("service.not_installed"));
            return CommandRouter.ExitSourceDisabled;
        }

        ConsoleHost.Line(Loc.T("service.state", WatchdogService.State() ?? "?"));
        return CommandRouter.ExitOk;
    }

    private static int Report(bool success, string key)
    {
        if (success)
        {
            ConsoleHost.Good(Loc.T(key));
            return CommandRouter.ExitOk;
        }

        ConsoleHost.Bad(Loc.T("service.failed"));
        return CommandRouter.ExitError;
    }
}
