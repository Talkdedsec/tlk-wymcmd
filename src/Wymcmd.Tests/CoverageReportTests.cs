using Wymcmd.Core.Coverage;
using Wymcmd.Core.Store;
using Xunit;

namespace Wymcmd.Tests;

public class CoverageReportTests
{
    private static readonly DateTime Noon = new(2026, 8, 24, 12, 0, 0);

    private static WatchSpan Watched(int fromHour, int toHour, WatchKind kind = WatchKind.Live)
        => new(kind, Noon.AddHours(fromHour), Noon.AddHours(toHour), Open: false);

    private static (DateTime, DateTime) Up(int fromHour, int toHour)
        => (Noon.AddHours(fromHour), Noon.AddHours(toHour));

    private static CoverageReport Report(
        IReadOnlyList<WatchSpan> spans,
        IReadOnlyList<(DateTime From, DateTime To)> awake)
        => CoverageReport.Compose(Noon, Noon.AddHours(10), spans, awake, blackBoxOn: false);

    /// <summary>
    /// The point of the whole thing: an hour with no recording is only a hole in the evidence if
    /// the machine was up for it. A laptop shut for the weekend was not unwatched.
    /// </summary>
    [Fact]
    public void A_stretch_with_the_machine_off_is_not_counted_as_blind()
    {
        var report = Report([Watched(0, 2)], [Up(0, 2)]);

        Assert.Empty(report.Blind);
        Assert.Equal(8, report.Gaps.Sum(g => g.Length.TotalHours), 3);
        Assert.All(report.Gaps, gap => Assert.False(gap.MachineWasUp));
    }

    [Fact]
    public void A_stretch_with_the_machine_up_and_nothing_recording_is_blind()
    {
        var report = Report([Watched(0, 2)], [Up(0, 6)]);

        var blind = Assert.Single(report.Blind);
        Assert.Equal(Noon.AddHours(2), blind.From);
        Assert.Equal(Noon.AddHours(6), blind.To);
    }

    /// <summary>Quoting watched against the wall clock would punish a machine for being switched off.</summary>
    [Fact]
    public void The_share_is_measured_against_the_time_the_machine_was_up()
    {
        var report = Report([Watched(0, 3)], [Up(0, 6)]);

        Assert.Equal(TimeSpan.FromHours(6), report.MachineUp);
        Assert.Equal(0.5, report.Share, 3);
    }

    [Fact]
    public void A_machine_that_was_never_up_reports_no_share_rather_than_dividing_by_zero()
    {
        var report = Report([], []);

        Assert.Equal(0, report.Share);
    }

    [Fact]
    public void Blind_stretches_cut_apart_by_the_power_log_are_reported_as_one()
    {
        // Two awake windows meeting exactly, which is what a wake with no matching sleep looks like.
        var report = Report([], [Up(0, 4), Up(4, 8)]);

        var blind = Assert.Single(report.Blind);
        Assert.Equal(Noon, blind.From);
        Assert.Equal(Noon.AddHours(8), blind.To);
    }

    [Fact]
    public void Recording_that_covers_the_whole_window_leaves_nothing_blind()
    {
        var report = Report([Watched(0, 10)], [Up(0, 10)]);

        Assert.Empty(report.Gaps);
        Assert.Equal(1, report.Share, 3);
    }

    [Fact]
    public void The_black_box_counts_as_recording_like_any_other_watcher()
    {
        var report = Report([Watched(0, 10, WatchKind.BlackBox)], [Up(0, 10)]);

        Assert.Empty(report.Blind);
        Assert.Equal(TimeSpan.FromHours(10), report.Watched);
    }
}
