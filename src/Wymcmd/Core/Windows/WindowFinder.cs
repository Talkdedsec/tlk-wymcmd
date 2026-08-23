using System.Text;
using Wymcmd.Core.Model;
using static Wymcmd.Core.Windows.NativeMethods;

namespace Wymcmd.Core.Windows;

public sealed record WindowInfo(IntPtr Handle, int Pid, string ClassName, string Title, bool Visible);

/// <summary>
/// Maps windows back to processes. A console window belongs to conhost.exe, not to cmd.exe,
/// so console lookups also check the host's parent - otherwise every console looks headless.
/// </summary>
public static class WindowFinder
{
    public static readonly string[] ConsoleClasses =
    [
        "ConsoleWindowClass",
        "PseudoConsoleWindow",
        "CASCADIA_HOSTING_WINDOW_CLASS"
    ];

    public static IReadOnlyList<WindowInfo> All()
    {
        var found = new List<WindowInfo>(256);

        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var pid);

            var className = new StringBuilder(256);
            GetClassNameW(handle, className, className.Capacity);

            var title = new StringBuilder(512);
            GetWindowTextW(handle, title, title.Capacity);

            found.Add(new WindowInfo(handle, (int)pid, className.ToString(), title.ToString(), IsWindowVisible(handle)));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    public static IReadOnlyList<WindowInfo> ForProcess(int pid)
        => All().Where(w => w.Pid == pid).ToList();

    /// <summary>
    /// Decides whether a console process actually put a window on screen. Returns Unknown when
    /// the process is already gone - guessing would be worse than admitting we do not know.
    /// </summary>
    public static WindowVisibility ConsoleVisibility(int pid, Func<int, int?> parentOf)
    {
        if (!ProcessQuery.IsAlive(pid)) return WindowVisibility.Unknown;

        var windows = All();

        var own = windows.Where(w => w.Pid == pid).ToList();
        if (own.Any(w => w.Visible && ConsoleClasses.Contains(w.ClassName)))
            return WindowVisibility.Visible;

        // conhost.exe hosts the window for cmd/powershell; find the host whose parent is us.
        foreach (var window in windows.Where(w => ConsoleClasses.Contains(w.ClassName)))
        {
            var owner = parentOf(window.Pid);
            if (owner == pid)
                return window.Visible ? WindowVisibility.Visible : WindowVisibility.Hidden;
        }

        if (own.Count > 0 && own.Any(w => w.Visible)) return WindowVisibility.Visible;

        // Running inside Windows Terminal or an IDE panel: no window of its own, but not hidden either.
        var host = parentOf(pid);
        if (host is { } hostPid && windows.Any(w => w.Pid == hostPid && w.Visible))
            return WindowVisibility.Embedded;

        return WindowVisibility.Hidden;
    }
}
