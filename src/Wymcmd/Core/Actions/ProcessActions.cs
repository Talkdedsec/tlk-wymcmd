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
    public static ActionResult Kill(int pid, string? imageName = null)
    {
        if (KillGuard.IsProtected(pid, imageName ?? NameOf(pid)))
            return new ActionResult(ActionOutcome.Protected, 0, imageName ?? NameOf(pid));

        var handle = OpenProcess(PROCESS_TERMINATE, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return new ActionResult(ProcessQuery.IsAlive(pid) ? ActionOutcome.AccessDenied : ActionOutcome.NotFound);

        try
        {
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
    /// Suspends the root first so it cannot spawn while we work, then kills leaves upward.
    /// </summary>
    public static ActionResult KillTree(ProcessTree tree, int pid)
    {
        if (KillGuard.IsProtected(pid))
            return new ActionResult(ActionOutcome.Protected, 0, NameOf(pid));

        Suspend(pid);

        var descendants = tree.LiveDescendants(pid);
        var killed = 0;

        foreach (var record in descendants.OrderByDescending(r => r.StartTime))
        {
            if (KillGuard.IsProtected(record.Pid, record.ImageName)) continue;
            if (Kill(record.Pid, record.ImageName).Success) killed++;
        }

        Resume(pid);
        if (Kill(pid).Success) killed++;

        return new ActionResult(killed > 0 ? ActionOutcome.Done : ActionOutcome.Failed, killed);
    }

    public static ActionResult Suspend(int pid)
    {
        if (KillGuard.IsProtected(pid)) return new ActionResult(ActionOutcome.Protected);

        var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, (uint)pid);
        if (handle == IntPtr.Zero) return new ActionResult(ActionOutcome.AccessDenied);
        try
        {
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
