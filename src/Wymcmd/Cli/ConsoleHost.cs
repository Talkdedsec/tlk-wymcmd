using System.Text;
using static Wymcmd.Core.Windows.NativeMethods;

namespace Wymcmd.Cli;

/// <summary>
/// The app is a WinExe so the GUI never flashes a console; when it is started with arguments
/// it attaches to the console that launched it and behaves like a normal command line tool.
/// </summary>
public static class ConsoleHost
{
    private static bool _attached;

    private const string Esc = "\u001b";

    public static bool Colors { get; private set; }

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;

        // AttachConsole resets the standard handles of a windowed process, which would throw
        // away a redirection the caller set up ("wymcmd list --json > out.json"). Remember the
        // inherited handles first and put them back afterwards.
        var inheritedOut = GetStdHandle(STD_OUTPUT_HANDLE);
        var inheritedError = GetStdHandle(STD_ERROR_HANDLE);
        var outputRedirected = IsRedirected(inheritedOut);
        var errorRedirected = IsRedirected(inheritedError);

        if (!AttachConsole(ATTACH_PARENT_PROCESS) && !outputRedirected)
            AllocConsole();

        if (outputRedirected) SetStdHandle(STD_OUTPUT_HANDLE, inheritedOut);
        if (errorRedirected) SetStdHandle(STD_ERROR_HANDLE, inheritedError);

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.OutputEncoding = new UTF8Encoding(false);

        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        Colors = !outputRedirected && GetConsoleMode(handle, out var mode)
                 && SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    /// <summary>A handle pointing at a file or a pipe rather than at a console.</summary>
    private static bool IsRedirected(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        var type = GetFileType(handle);
        return type != FILE_TYPE_CHAR && type != 0;
    }

    public static void Detach()
    {
        if (!_attached) return;
        Console.Out.Flush();
        FreeConsole();
        _attached = false;
    }

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Dim(string text) => Write(text, Esc + "[90m");
    public static void Good(string text) => Write(text, Esc + "[92m");
    public static void Warn(string text) => Write(text, Esc + "[93m");
    public static void Bad(string text) => Write(text, Esc + "[91m");
    public static void Accent(string text) => Write(text, Esc + "[96m");
    public static void Strong(string text) => Write(text, Esc + "[97m");

    public static string Color(string text, int code) => Colors ? Esc + "[" + code + "m" + text + Esc + "[0m" : text;

    public static string Paint(string text, string color) => Colors ? color + text + Esc + "[0m" : text;

    public static string Risk(int risk, string text)
    {
        if (!Colors) return text;
        var color = risk switch
        {
            >= 70 => Esc + "[91m",
            >= 40 => Esc + "[93m",
            _ => Esc + "[92m"
        };
        return color + text + Esc + "[0m";
    }

    private static void Write(string text, string color) => Console.WriteLine(Paint(text, color));
}
