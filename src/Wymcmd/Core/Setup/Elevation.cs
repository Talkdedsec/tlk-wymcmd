using System.Diagnostics;
using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Setup;

/// <summary>
/// The app runs as a normal user. Only the handful of operations that genuinely need it -
/// enabling sources, installing the black box, killing another user's process - ask for
/// elevation, and they ask by relaunching just that command.
/// </summary>
public static class Elevation
{
    public static bool IsElevated => SourceInspector.IsAdministrator();

    /// <summary>Relaunches the same executable elevated with the given arguments. Returns its exit code.</summary>
    public static int? Relaunch(params string[] arguments)
    {
        var executable = Environment.ProcessPath;
        if (executable is null) return null;

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            var process = Process.Start(info);
            if (process is null) return null;

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User dismissed the UAC prompt.
            Log.Info("elevation declined by the user");
            return null;
        }
    }
}
