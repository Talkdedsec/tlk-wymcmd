using Wymcmd.Core.Forensic;

namespace Wymcmd.Core.Why;

/// <summary>
/// The Task Scheduler writes the pid of every process it starts. That is a direct answer to
/// "which task did this", and it works even when the svchost above us refuses to hand over its
/// command line - which is exactly what happens when we are not elevated.
/// </summary>
public static class TaskLaunchIndex
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly Lock Sync = new();

    private static IReadOnlyList<TaskLaunch> _launches = [];
    private static DateTime _loadedAt = DateTime.MinValue;

    public static TaskLaunch? Find(int pid, int parentPid, DateTime when)
    {
        EnsureFresh();

        lock (Sync)
        {
            return _launches.FirstOrDefault(launch =>
                (launch.Pid == pid || launch.Pid == parentPid) &&
                Math.Abs((launch.When - when).TotalSeconds) < 60);
        }
    }

    private static void EnsureFresh()
    {
        lock (Sync)
        {
            if (DateTime.Now - _loadedAt < Freshness) return;
            _loadedAt = DateTime.Now;
        }

        var fresh = EvtxReader.TaskLaunches(DateTime.Now - Window, DateTime.Now.AddMinutes(1), 500);

        lock (Sync)
        {
            _launches = fresh;
        }
    }
}
