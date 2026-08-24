using System.Diagnostics;

namespace Wymcmd.Launcher;

internal static class Program
{
    private static int Main(string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var target = Path.Combine(directory, "wymcmd.exe");

        if (!File.Exists(target))
        {
            Console.Error.WriteLine($"wymcmd.exe not found next to this launcher ({directory})");
            return 1;
        }

        var info = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };

        foreach (var argument in args) info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process is null) return 1;

        // With no arguments wymcmd is the window, not a command: let go of it immediately so
        // double-clicking the launcher does not leave a console sitting behind the window.
        if (args.Length == 0) return 0;

        process.WaitForExit();
        return process.ExitCode;
    }
}
