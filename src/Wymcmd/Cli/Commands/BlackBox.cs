using Wymcmd.Core.Capture;
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
            "read" => Read(options),
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

    /// <summary>Shows what the recorder itself holds, with no other source mixed in.</summary>
    private static int Read(CliOptions options)
    {
        var window = Commands.List.ParseSpan(options.Value("--last") ?? "10m");
        var events = BlackBoxReader.Read(DateTime.Now - window, DateTime.Now.AddMinutes(1));

        if (options.Has("--console"))
            events = events.Where(evt => evt.IsConsoleHost).ToList();

        if (events.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("blackbox.empty"));
            return CommandRouter.ExitNotFound;
        }

        var withCommandLine = events.Count(evt => evt.CommandLine.Length > 0);

        foreach (var evt in events.TakeLast(options.Number("--limit", 20)))
        {
            var command = evt.CommandLine.Length > 0 ? evt.CommandLine : ConsoleHost.Color("-", 90);
            ConsoleHost.Line($"{evt.StartTime:HH:mm:ss}  {evt.ImageName,-20} {command}");
        }

        ConsoleHost.Line();
        ConsoleHost.Dim(Loc.T("blackbox.read_summary", events.Count, withCommandLine));
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
            foreach (var session in BlackBoxInstaller.Describe())
            {
                var sessionState = session.Enabled switch
                {
                    true => Loc.T("doctor.ok"),
                    false => Loc.T("doctor.missing"),
                    null => Loc.T("doctor.detail.needs_admin")
                };

                ConsoleHost.Line($"  {session.Name,-24} {sessionState,-24} {session.Bytes / (1024 * 1024)} MB");
                ConsoleHost.Dim($"    {session.File}");
                ConsoleHost.Dim($"    {Loc.T(session.CarriesCommandLine ? "blackbox.with_command_lines" : "blackbox.without_command_lines")}");
            }

            ConsoleHost.Line();
            ConsoleHost.Dim(Loc.T("blackbox.no_process"));
        }
        else
        {
            ConsoleHost.Dim(Loc.T("blackbox.how_to_enable"));
        }

        return installed ? CommandRouter.ExitOk : CommandRouter.ExitSourceDisabled;
    }
}
