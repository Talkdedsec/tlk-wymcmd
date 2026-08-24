using Wymcmd.Core.Localization;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;

namespace Wymcmd.Cli.Commands;

public static class BlackBox
{
    public static int Run(CliOptions options)
    {
        var action = options.Positional("--size").FirstOrDefault()?.ToLowerInvariant() ?? "status";

        return action switch
        {
            "on" or "install" => TurnOn(options),
            "off" or "uninstall" => TurnOff(options),
            _ => Status()
        };
    }

    private static int TurnOn(CliOptions options)
    {
        var size = options.Number("--size", 64);

        if (!Elevation.IsElevated)
        {
            ConsoleHost.Dim(Loc.T("sources.elevating"));
            var code = Elevation.Relaunch("blackbox", "on", "--size", size.ToString(), "--lang", Loc.Language);
            if (code is null)
            {
                ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
                return CommandRouter.ExitNeedsAdmin;
            }
            return code.Value;
        }

        BlackBoxInstaller.Install(size);
        ConsoleHost.Good(Loc.T("blackbox.installed", size));
        ConsoleHost.Dim(Loc.T("blackbox.reboot_note"));
        return CommandRouter.ExitOk;
    }

    private static int TurnOff(CliOptions options)
    {
        if (!Elevation.IsElevated)
        {
            var code = Elevation.Relaunch("blackbox", "off", "--lang", Loc.Language);
            if (code is null)
            {
                ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
                return CommandRouter.ExitNeedsAdmin;
            }
            return code.Value;
        }

        BlackBoxInstaller.Uninstall(deleteTrace: !options.Has("--keep-trace"));
        ConsoleHost.Good(Loc.T("blackbox.removed"));
        return CommandRouter.ExitOk;
    }

    private static int Status()
    {
        var installed = BlackBoxInstaller.IsInstalled();
        var enabled = BlackBoxInstaller.IsEnabled();
        var size = BlackBoxInstaller.TraceSizeBytes();

        var state = (installed, enabled) switch
        {
            (false, _) => ConsoleHost.Color(Loc.T("doctor.missing"), 91),
            (true, false) => ConsoleHost.Color(Loc.T("doctor.degraded"), 93),
            _ => ConsoleHost.Color(Loc.T("doctor.ok"), 92)
        };

        ConsoleHost.Line($"{Loc.T("doctor.blackbox")}: {state}");

        if (installed)
        {
            ConsoleHost.Dim($"{AppPaths.BlackBoxTrace}  ({size / (1024 * 1024)} MB)");
            ConsoleHost.Dim(Loc.T("blackbox.no_process"));
        }
        else
        {
            ConsoleHost.Dim(Loc.T("blackbox.how_to_enable"));
        }

        return installed ? CommandRouter.ExitOk : CommandRouter.ExitSourceDisabled;
    }
}
