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

    private static readonly TimeSpan ForcedRefreshInterval = TimeSpan.FromSeconds(1);

    private static IReadOnlyList<TaskLaunch> _launches = [];
    private static DateTime _loadedAt = DateTime.MinValue;
    private static DateTime _refreshedAt = DateTime.MinValue;

    /// <summary>Scheduler pids get reused quickly, so a match has to be tight in time as well.</summary>
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(15);

    public static TaskLaunch? Find(int pid, int parentPid, DateTime when, string imageName = "")
    {
        EnsureFresh();
        return Lookup(pid, parentPid, when, imageName);
    }

    /// <summary>
    /// The scheduler writes its event in the same second the process starts, so a cached view
    /// is often one beat behind. Callers that already suspect a task (svchost above them) pay
    /// for a fresh read; everyone else keeps the cheap cached answer.
    /// </summary>
    public static TaskLaunch? FindFresh(int pid, int parentPid, DateTime when, string imageName = "")
    {
        var hit = Find(pid, parentPid, when, imageName);
        if (hit is not null) return hit;

        lock (Sync)
        {
            if (DateTime.Now - _refreshedAt < ForcedRefreshInterval) return null;
            _refreshedAt = DateTime.Now;
        }

        Reload();
        return Lookup(pid, parentPid, when, imageName);
    }

    private static TaskLaunch? Lookup(int pid, int parentPid, DateTime when, string imageName)
    {
        List<TaskLaunch> candidates;
        lock (Sync)
        {
            candidates = _launches
                .Where(launch => launch.Pid == pid || launch.Pid == parentPid)
                .Where(launch => (launch.When - when).Duration() < MatchWindow)
                .ToList();
        }

        if (candidates.Count == 0) return null;

        // When the scheduler recorded which program it ran, that has to be this program.
        var named = candidates
            .Where(launch => ActionMatches(launch.ActionName, imageName))
            .ToList();

        if (named.Count > 0) candidates = named;
        else if (imageName.Length > 0 && candidates.All(launch => launch.ActionName is { Length: > 0 }))
            return null;

        return candidates.MinBy(launch => (launch.When - when).Duration());
    }

    private static bool ActionMatches(string? actionName, string imageName)
    {
        if (imageName.Length == 0 || string.IsNullOrWhiteSpace(actionName)) return false;

        var action = Path.GetFileName(actionName.Trim().Trim('"'));
        return action.Equals(imageName, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureFresh()
    {
        lock (Sync)
        {
            if (DateTime.Now - _loadedAt < Freshness) return;
            _loadedAt = DateTime.Now;
        }

        Reload();
    }

    private static void Reload()
    {
        var fresh = EvtxReader.TaskLaunches(DateTime.Now - Window, DateTime.Now.AddMinutes(1), 500);

        lock (Sync)
        {
            _launches = fresh;
            _loadedAt = DateTime.Now;
        }
    }
}
