using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Service;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;

namespace Wymcmd.Cli.Commands;

/// <summary>
/// Leaves the machine the way it was found: audit policy reverted, black box removed,
/// service gone, data deleted. A tool that watches the system has to be able to let go of it.
/// </summary>
public static class Uninstall
{
    public static int Run(CliOptions options)
    {
        var purge = options.Has("--purge");

        if (!Elevation.IsElevated)
        {
            var arguments = purge ? new[] { "uninstall", "--purge", "--lang", Loc.Language } : ["uninstall", "--lang", Loc.Language];
            var code = Elevation.Relaunch(arguments);
            if (code is null)
            {
                ConsoleHost.Bad(Loc.T("cli.error.needs_admin"));
                return CommandRouter.ExitNeedsAdmin;
            }
            return code.Value;
        }

        var removed = new List<string>();

        try
        {
            var reverted = AuditPolicySetup.RevertAll();
            removed.AddRange(reverted.Select(key => Loc.T("doctor." + key)));
        }
        catch (Exception ex)
        {
            Log.Error("could not revert audit policy", ex);
        }

        if (BlackBoxInstaller.IsInstalled())
        {
            BlackBoxInstaller.Uninstall();
            removed.Add(Loc.T("doctor.blackbox"));
        }

        if (WatchdogService.IsInstalled())
        {
            WatchdogService.Uninstall();
            removed.Add(Loc.T("uninstall.service"));
        }

        if (purge && Directory.Exists(AppPaths.Root))
        {
            try
            {
                Directory.Delete(AppPaths.Root, recursive: true);
                removed.Add(Loc.T("uninstall.data"));
            }
            catch (IOException ex)
            {
                ConsoleHost.Warn(Loc.T("uninstall.data_locked", ex.Message));
            }
        }

        if (removed.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("uninstall.nothing"));
            return CommandRouter.ExitOk;
        }

        foreach (var item in removed) ConsoleHost.Good(Loc.T("uninstall.removed", item));
        ConsoleHost.Line();
        ConsoleHost.Dim(Loc.T("uninstall.done"));
        return CommandRouter.ExitOk;
    }
}
