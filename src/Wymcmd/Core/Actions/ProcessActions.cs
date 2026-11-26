using System.Diagnostics;
using System.Runtime.InteropServices;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Tree;
using Wymcmd.Core.Windows;
using static Wymcmd.Core.Windows.NativeMethods;

namespace Wymcmd.Core.Actions;

public enum ActionOutcome
{
    Done,
    Protected,
    AccessDenied,
    NotFound,
    Failed
}

public sealed record ActionResult(ActionOutcome Outcome, int Affected = 0, string? Detail = null)
{
    public bool Success => Outcome == ActionOutcome.Done;
}

public static class ProcessActions
{
    /// <summary>
    /// Windows hands a pid to the next process as soon as the last one is gone, and a console
    /// that lives 40 ms is gone before a rule is done judging it. Whoever asks for a kill can say
    /// when their process started; a process with that pid and another start time is left alone.
    /// </summary>
    private static readonly TimeSpan StartSlack = TimeSpan.FromMilliseconds(500);

    /// <param name="startedAt">When the process the caller means started. Null skips the identity check.</param>
    /// <param name="slack">How far the two start times may differ. Wider only for times that came from another clock.</param>
    public static ActionResult Kill(int pid, string? imageName = null, DateTime? startedAt = null, TimeSpan? slack = null)
    {
        if (KillGuard.IsProtected(pid, imageName ?? NameOf(pid)))
            return new ActionResult(ActionOutcome.Protected, 0, imageName ?? NameOf(pid));

        var handle = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return new ActionResult(ProcessQuery.IsAlive(pid) ? ActionOutcome.AccessDenied : ActionOutcome.NotFound);

        try
        {
            if (!IsTheSameProcess(handle, startedAt, slack))
                return new ActionResult(ActionOutcome.NotFound, 0, "the pid now belongs to another process");

            // The name the caller passed describes the process it saw; this one is what is there now.
            if (ImageNameOf(handle) is { } actual && KillGuard.IsProtectedName(actual))
                return new ActionResult(ActionOutcome.Protected, 0, actual);

            if (!TerminateProcess(handle, 1))
                return new ActionResult(ActionOutcome.Failed, 0, $"win32 error {Marshal.GetLastWin32Error()}");

            Log.Info($"killed pid {pid} ({imageName ?? NameOf(pid)})");
            return new ActionResult(ActionOutcome.Done, 1);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Suspends the root first so it cannot spawn while we work, then kills leaves upward. The
    /// root is killed while still suspended; it is only let go again if it could not be killed.
    /// </summary>
    public static ActionResult KillTree(ProcessTree tree, int pid, DateTime? startedAt = null, TimeSpan? slack = null)
    {
        if (KillGuard.IsProtected(pid))
            return new ActionResult(ActionOutcome.Protected, 0, NameOf(pid));

        // A root that is no longer the process the caller meant has no children of interest either.
        var held = Suspend(pid, startedAt, slack);
        if (held.Outcome == ActionOutcome.NotFound) return held;

        var descendants = tree.LiveDescendants(pid);
        var killed = 0;

        foreach (var record in descendants.OrderByDescending(r => r.StartTime))
        {
            if (KillGuard.IsProtected(record.Pid, record.ImageName)) continue;
            if (Kill(record.Pid, record.ImageName, record.StartTime).Success) killed++;
        }

        if (Kill(pid, null, startedAt, slack).Success) killed++;
        else Resume(pid);

        return new ActionResult(killed > 0 ? ActionOutcome.Done : ActionOutcome.Failed, killed);
    }

    public static ActionResult Suspend(int pid, DateTime? startedAt = null, TimeSpan? slack = null)
    {
        if (KillGuard.IsProtected(pid)) return new ActionResult(ActionOutcome.Protected);

        var handle = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return new ActionResult(ActionOutcome.AccessDenied);
        try
        {
            if (!IsTheSameProcess(handle, startedAt, slack))
                return new ActionResult(ActionOutcome.NotFound, 0, "the pid now belongs to another process");

            if (ImageNameOf(handle) is { } actual && KillGuard.IsProtectedName(actual))
                return new ActionResult(ActionOutcome.Protected, 0, actual);

            return NtSuspendProcess(handle) == 0
                ? new ActionResult(ActionOutcome.Done, 1)
                : new ActionResult(ActionOutcome.Failed);
        }
        finally { CloseHandle(handle); }
    }

    public static ActionResult Resume(int pid)
    {
        var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, (uint)pid);
        if (handle == IntPtr.Zero) return new ActionResult(ActionOutcome.AccessDenied);
        try
        {
            return NtResumeProcess(handle) == 0
                ? new ActionResult(ActionOutcome.Done, 1)
                : new ActionResult(ActionOutcome.Failed);
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Asks the window to close - the process gets to run its own shutdown.</summary>
    public static ActionResult CloseWindow(int pid)
    {
        var windows = WindowFinder.ForProcess(pid).Where(w => w.Visible).ToList();
        if (windows.Count == 0) return new ActionResult(ActionOutcome.NotFound);

        foreach (var window in windows)
            PostMessageW(window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        return new ActionResult(ActionOutcome.Done, windows.Count);
    }

    public static ActionResult HideWindow(int pid)
    {
        var windows = WindowFinder.ForProcess(pid).Where(w => w.Visible).ToList();
        if (windows.Count == 0) return new ActionResult(ActionOutcome.NotFound);

        foreach (var window in windows)
            ShowWindow(window.Handle, SW_HIDE);

        return new ActionResult(ActionOutcome.Done, windows.Count);
    }

    public static ActionResult OpenLocation(string imagePath)
    {
        if (!File.Exists(imagePath)) return new ActionResult(ActionOutcome.NotFound);

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{imagePath}\"") { UseShellExecute = true });
        return new ActionResult(ActionOutcome.Done, 1);
    }

    private static bool IsTheSameProcess(IntPtr handle, DateTime? startedAt, TimeSpan? slack)
    {
        if (startedAt is not { } expected) return true;
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) return false;

        return (DateTime.FromFileTime(created) - expected).Duration() <= (slack ?? StartSlack);
    }

    private static string? ImageNameOf(IntPtr handle)
    {
        var buffer = new System.Text.StringBuilder(1024);
        uint size = 1024;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? Path.GetFileName(buffer.ToString()) : null;
    }

    private static string NameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }
}
