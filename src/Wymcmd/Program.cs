using Wymcmd.Cli;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Localization;

namespace Wymcmd;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("unhandled: " + (e.ExceptionObject as Exception)?.ToString());

        if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
        {
            Core.Service.WatchdogService.RunAsService();
            return 0;
        }

        if (args.Length == 0)
        {
            Loc.Use(Loc.DetectSystemLanguage());
            return Gui.Shell.Run();
        }

        ConsoleHost.Attach();
        try
        {
            return CommandRouter.RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("command failed", ex);
            ConsoleHost.Bad(ex.Message);
            return CommandRouter.ExitError;
        }
        finally
        {
            ConsoleHost.Detach();
        }
    }
}
