using Wymcmd.Core.Capture;
using Wymcmd.Core.Forensic;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Coverage;

/// <summary>
/// A stretch nothing was recording. Whether the machine was up decides whether it is a hole in
/// the evidence or simply an hour when there was nothing to see.
/// </summary>
public sealed record CoverageGap(DateTime From, DateTime To, bool MachineWasUp)
{
    public TimeSpan Length => To - From;
}

/// <summary>
/// What this machine can honestly account for over a window: when something was recording, when
/// the machine was up, and which of the remaining stretches are actually blind.
///
/// The number worth quoting is watched against machine-up time, not against the wall clock. A
/// laptop that was shut for five days out of seven was not 70% unwatched.
/// </summary>
public sealed record CoverageReport(
    DateTime From,
    DateTime To,
    IReadOnlyList<WatchSpan> Spans,
    IReadOnlyList<CoverageGap> Gaps,
    TimeSpan Watched,
    TimeSpan MachineUp,
    bool BlackBoxOn)
{
    /// <summary>Stretches where the machine was up and nothing was recording. The ones that matter.</summary>
    public IReadOnlyList<CoverageGap> Blind => Gaps.Where(g => g.MachineWasUp).ToList();

    /// <summary>Share of the time the machine was actually up. Zero when it never was.</summary>
    public double Share => MachineUp > TimeSpan.Zero
        ? Math.Min(1, Watched.TotalSeconds / MachineUp.TotalSeconds)
        : 0;

    /// <summary>
    /// Was anything recording at that moment? The ledger answers for free; the trace is only
    /// opened when the ledger says no, which is the case where the black box is the answer.
    /// </summary>
    public static bool Covered(DateTime moment)
    {
        if (new WatchLedger().Covered(moment)) return true;

        return Recorder() is { } trace && trace.From <= moment && moment <= trace.To;
    }

    public static CoverageReport Build(DateTime from, DateTime to, WatchLedger? ledger = null)
    {
        var on = BlackBoxOnNow();

        return Compose(
            from, to,
            Combine(from, to, ledger ?? new WatchLedger(), on),
            PowerHistory.Awake(from, to),
            on);
    }

    private static bool BlackBoxOnNow()
        => BlackBoxInstaller.IsInstalled() && BlackBoxInstaller.IsEnabled() != false;

    /// <summary>
    /// How far back the recorder answers for, and up to when. A running session is recording right
    /// now even though its trace was last flushed minutes ago, so a live session reaches to now -
    /// taking the file's timestamp as the end would call the last few minutes unwatched.
    /// </summary>
    private static (DateTime From, DateTime To)? Recorder()
    {
        if (BlackBoxReader.RetainedRange() is not { } trace) return null;

        return BlackBoxOnNow() ? (trace.From, DateTime.Now) : trace;
    }

    /// <summary>
    /// The arithmetic on its own, with the machine handed in rather than asked. Build gathers the
    /// real sessions and the real power log; this turns them into the report.
    /// </summary>
    public static CoverageReport Compose(
        DateTime from,
        DateTime to,
        IReadOnlyList<WatchSpan> spans,
        IReadOnlyList<(DateTime From, DateTime To)> awake,
        bool blackBoxOn)
    {
        var watched = spans.Aggregate(TimeSpan.Zero, (total, s) => total + (s.To - s.From));
        var up = awake.Aggregate(TimeSpan.Zero, (total, a) => total + (a.To - a.From));

        return new CoverageReport(
            from, to, spans,
            Classify(Holes(from, to, spans), awake),
            watched, up, blackBoxOn);
    }

    /// <summary>
    /// The watchers we recorded, plus the black box. The recorder leaves no session behind - it is
    /// Windows that starts it and nothing of ours is running - so its coverage is read back from
    /// how far the trace still reaches.
    /// </summary>
    private static IReadOnlyList<WatchSpan> Combine(
        DateTime from, DateTime to, WatchLedger ledger, bool blackBoxOn)
    {
        var spans = ledger.Spans(from, to).ToList();

        if (BlackBoxReader.RetainedRange() is { } raw)
        {
            var trace = blackBoxOn ? (From: raw.From, To: DateTime.Now) : raw;

            var start = trace.From > from ? trace.From : from;
            var end = trace.To < to ? trace.To : to;

            if (end > start) spans.Add(new WatchSpan(WatchKind.BlackBox, start, end, Open: false));
        }

        return WatchLedger.MergeSpans(spans);
    }

    private static List<(DateTime From, DateTime To)> Holes(
        DateTime from, DateTime to, IReadOnlyList<WatchSpan> spans)
    {
        var holes = new List<(DateTime From, DateTime To)>();
        var cursor = from;

        foreach (var span in spans.OrderBy(s => s.From))
        {
            if (span.From > cursor) holes.Add((cursor, span.From));
            if (span.To > cursor) cursor = span.To;
        }

        if (cursor < to) holes.Add((cursor, to));

        return holes;
    }

    /// <summary>Cuts each unrecorded stretch along the moments the machine was up.</summary>
    private static List<CoverageGap> Classify(
        List<(DateTime From, DateTime To)> holes,
        IReadOnlyList<(DateTime From, DateTime To)> awake)
    {
        var gaps = new List<CoverageGap>();

        foreach (var hole in holes)
        {
            var cursor = hole.From;

            foreach (var window in awake.Where(a => a.To > hole.From && a.From < hole.To).OrderBy(a => a.From))
            {
                var start = window.From > hole.From ? window.From : hole.From;
                var end = window.To < hole.To ? window.To : hole.To;

                if (start > cursor) gaps.Add(new CoverageGap(cursor, start, MachineWasUp: false));
                if (end > start) gaps.Add(new CoverageGap(start, end, MachineWasUp: true));

                cursor = end;
            }

            if (hole.To > cursor) gaps.Add(new CoverageGap(cursor, hole.To, MachineWasUp: false));
        }

        return Join(gaps.Where(g => g.Length >= TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The power log records a wake without a matching sleep often enough that one blind stretch
    /// arrives cut into pieces. Reading them as separate gaps would overstate how fragmented the
    /// record is, so touching stretches of the same kind are put back together.
    /// </summary>
    private static List<CoverageGap> Join(IEnumerable<CoverageGap> gaps)
    {
        var joined = new List<CoverageGap>();

        foreach (var gap in gaps.OrderBy(g => g.From))
        {
            if (joined.Count > 0
                && joined[^1].MachineWasUp == gap.MachineWasUp
                && gap.From - joined[^1].To < TimeSpan.FromSeconds(1))
            {
                joined[^1] = joined[^1] with { To = gap.To };
                continue;
            }

            joined.Add(gap);
        }

        return joined;
    }
}
