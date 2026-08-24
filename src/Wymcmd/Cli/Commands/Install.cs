using Wymcmd.Core.Localization;
using Wymcmd.Core.Setup;

namespace Wymcmd.Cli.Commands;

public static class Install
{
    public static int Run(CliOptions options)
    {
        var directory = options.Value("--dir");

        try
        {
            var result = PathInstaller.Install(directory, createShortcut: !options.Has("--no-shortcut"));

            ConsoleHost.Good(Loc.T("install.done", result.Directory));
            if (result.ShortcutCreated) ConsoleHost.Dim(Loc.T("install.shortcut"));

            ConsoleHost.Line();
            ConsoleHost.Line(result.PathChanged
                ? Loc.T("install.path_added")
                : Loc.T("install.path_already"));

            return CommandRouter.ExitOk;
        }
        catch (UnauthorizedAccessException)
        {
            ConsoleHost.Bad(Loc.T("install.failed", PathInstaller.DefaultDirectory));
            return CommandRouter.ExitError;
        }
        catch (IOException ex) when ((uint)ex.HResult is 0x80070020 or 0x80070021)
        {
            // Sharing violation: an installed copy is running, and it cannot overwrite itself.
            ConsoleHost.Bad(Loc.T("install.in_use", PathInstaller.DefaultDirectory));
            return CommandRouter.ExitError;
        }
        catch (IOException ex)
        {
            ConsoleHost.Bad(Loc.T("install.failed", ex.Message));
            return CommandRouter.ExitError;
        }
    }
}
