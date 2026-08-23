using System.Collections.Concurrent;
using Wymcmd.Core.Model;
using Wymcmd.Core.Windows;

namespace Wymcmd.Core.Tree;

public sealed class ProcRecord
{
    public int Pid { get; init; }
    public int ParentPid { get; set; }
    public ulong StartKey { get; set; }
    public string ImageName { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public int? ExitCode { get; set; }
    public int SessionId { get; set; }
    public string? UserName { get; set; }

    public bool Alive => ExitTime is null;

    public bool CoversMoment(DateTime moment)
        => StartTime <= moment && (ExitTime is null || ExitTime >= moment);
}

/// <summary>
/// The in-memory family tree. Records outlive the processes they describe, which is the
/// whole point: by the time you ask "who started this cmd", the parent is usually gone.
/// </summary>
public sealed class ProcessTree
{
    private const int MaxChainDepth = 24;

    private readonly ConcurrentDictionary<int, List<ProcRecord>> _byPid = new();
    private readonly Lock _sync = new();

    public int Count => _byPid.Values.Sum(list => list.Count);

    public void Seed()
    {
        foreach (var record in ProcessSnapshot.Capture())
            Add(record);
    }

    public ProcRecord Add(ProcRecord record)
    {
        var list = _byPid.GetOrAdd(record.Pid, _ => []);
        lock (_sync)
        {
            // A pid gets reused; the previous tenant is closed out when a newer start arrives.
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].ExitTime is null && list[i].StartTime <= record.StartTime)
                    list[i].ExitTime = record.StartTime;
            }
            list.Add(record);
        }
        return record;
    }

    public void MarkExit(int pid, DateTime when, int? exitCode)
    {
        if (!_byPid.TryGetValue(pid, out var list)) return;
        lock (_sync)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].ExitTime is not null) continue;
                list[i].ExitTime = when;
                list[i].ExitCode = exitCode;
                return;
            }
        }
    }

    public ProcRecord? Resolve(int pid, DateTime? moment = null)
    {
        if (!_byPid.TryGetValue(pid, out var list)) return null;
        lock (_sync)
        {
            if (list.Count == 0) return null;
            if (moment is not { } at) return list[^1];

            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i].CoversMoment(at)) return list[i];

            // Nothing covers that moment - fall back to the newest record that started before it.
            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i].StartTime <= at) return list[i];

            return null;
        }
    }

    public IReadOnlyList<AncestorLink> BuildChain(ProcRecord child)
    {
        var chain = new List<AncestorLink>();
        var seen = new HashSet<int> { child.Pid };
        var current = child;

        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            var parentPid = current.ParentPid;
            if (parentPid <= 0 || !seen.Add(parentPid)) break;

            // The parent must predate the child, otherwise the pid was recycled.
            var parent = Resolve(parentPid, current.StartTime);
            if (parent is null || parent.StartTime > current.StartTime)
            {
                chain.Add(new AncestorLink
                {
                    Pid = parentPid,
                    ImageName = parent?.ImageName ?? "",
                    Alive = false
                });
                break;
            }

            chain.Add(new AncestorLink
            {
                Pid = parent.Pid,
                ImageName = parent.ImageName,
                ImagePath = parent.ImagePath,
                CommandLine = parent.CommandLine,
                StartTime = parent.StartTime,
                Alive = parent.Alive
            });

            if (parent.Pid == 4 || parent.Pid == 0) break;
            current = parent;
        }

        return chain;
    }

    public IReadOnlyList<ProcRecord> LiveDescendants(int pid)
    {
        var found = new List<ProcRecord>();
        var frontier = new Queue<int>();
        frontier.Enqueue(pid);
        var visited = new HashSet<int> { pid };

        var live = LiveRecords();
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var candidate in live.Where(r => r.ParentPid == parent))
            {
                if (!visited.Add(candidate.Pid)) continue;
                found.Add(candidate);
                frontier.Enqueue(candidate.Pid);
            }
        }

        return found;
    }

    public IReadOnlyList<ProcRecord> LiveRecords()
    {
        lock (_sync)
        {
            return _byPid.Values.SelectMany(list => list).Where(r => r.Alive).ToList();
        }
    }

    public IReadOnlyList<ProcRecord> AllRecords()
    {
        lock (_sync)
        {
            return _byPid.Values.SelectMany(list => list).ToList();
        }
    }

    /// <summary>Drops dead records older than the retention window so long runs stay flat in memory.</summary>
    public void Prune(TimeSpan keepDeadFor)
    {
        var cutoff = DateTime.Now - keepDeadFor;
        lock (_sync)
        {
            foreach (var (pid, list) in _byPid)
            {
                list.RemoveAll(r => r.ExitTime is { } exit && exit < cutoff);
                if (list.Count == 0) _byPid.TryRemove(pid, out _);
            }
        }
    }
}
