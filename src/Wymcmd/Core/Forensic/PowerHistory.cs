using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Forensic;

/// <summary>
/// When this machine was awake at all.
///
/// A stretch with no recording means one of two very different things: nothing was watching a
/// running machine, or the machine was off. Only the first is a hole in the evidence, and calling
/// them both a gap wastes the reader's attention on hours where there was nothing to see.
///
/// Windows writes both transitions to the System log, which any user can read - no elevation and
/// no audit policy needed, so this works on a machine where nothing has ever been turned on.
/// </summary>
public static class PowerHistory
{
    private const string LogName = "System";

    /// <summary>Boot, resume from sleep, and the event log service coming up behind them.</summary>
    private static readonly int[] WokeUp = [12, 107, 1, 6005];

    /// <summary>Shutdown, entering sleep, a clean service stop, and the unexpected-shutdown marker.</summary>
    private static readonly int[] WentAway = [13, 42, 6006, 6008];

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The stretches inside the window when the machine was up, oldest first. With no transition
    /// recorded at all the machine is taken to have been up throughout - it is running now, and
    /// nothing says otherwise.
    /// </summary>
    public static IReadOnlyList<(DateTime From, DateTime To)> Awake(DateTime from, DateTime to)
    {
        var transitions = Read(from, to);
        if (transitions.Count == 0) return [(from, to)];

        var awake = new List<(DateTime From, DateTime To)>();

        // Whatever the first transition is tells us what came before it: a machine that woke up
        // was off, and a machine that went away was up.
        var since = transitions[0].Up ? (DateTime?)null : from;

        foreach (var (moment, up) in transitions)
        {
            if (up)
            {
                since ??= moment;
                continue;
            }

            if (since is { } start && moment > start) awake.Add((start, moment));
            since = null;
        }

        if (since is { } last && to > last) awake.Add((last, to));

        return awake;
    }

    /// <summary>What is left of the window once the awake stretches are taken out.</summary>
    public static IReadOnlyList<(DateTime From, DateTime To)> Asleep(DateTime from, DateTime to)
    {
        var asleep = new List<(DateTime From, DateTime To)>();
        var cursor = from;

        foreach (var (start, end) in Awake(from, to))
        {
            if (start > cursor) asleep.Add((cursor, start));
            if (end > cursor) cursor = end;
        }

        if (cursor < to) asleep.Add((cursor, to));

        return asleep.Where(a => a.To - a.From >= TimeSpan.FromSeconds(1)).ToList();
    }

    private static List<(DateTime Moment, bool Up)> Read(DateTime from, DateTime to)
    {
        var transitions = new List<(DateTime, bool)>();

        try
        {
            var ids = string.Join(" or ", WokeUp.Concat(WentAway).Select(id => $"EventID={id}"));
            var window = (long)Math.Max(1000, (DateTime.Now - from).TotalMilliseconds);

            var query = new EventLogQuery(LogName, PathType.LogName,
                $"*[System[({ids}) and TimeCreated[timediff(@SystemTime) <= {window}]]]");

            using var reader = new EventLogReader(query);
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < ReadBudget)
            {
                using var record = reader.ReadEvent(ReadBudget - clock.Elapsed);
                if (record is null) break;

                if (record.TimeCreated is not { } moment || moment < from || moment > to) continue;

                var id = record.Id;
                if (WokeUp.Contains(id)) transitions.Add((moment, true));
                else if (WentAway.Contains(id)) transitions.Add((moment, false));
            }
        }
        catch (EventLogException ex)
        {
            Log.Warn("power history unavailable: " + ex.Message);
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        transitions.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return transitions;
    }
}
