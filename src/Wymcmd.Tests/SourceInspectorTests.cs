using System.Diagnostics;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Setup;
using Wymcmd.ViewModels;
using Xunit;

namespace Wymcmd.Tests;

[Collection("language")]
public class SourceInspectorTests
{
    /// <summary>
    /// The panel used to hang for minutes when process auditing had never been on: asking the
    /// Security log for the newest 4688 backwards walks every record before admitting there is
    /// none. Each probe is bounded now, so the whole sweep has to come back quickly even on a
    /// machine where nothing is enabled.
    /// </summary>
    [Fact]
    public void The_whole_sweep_comes_back_in_seconds()
    {
        var clock = Stopwatch.StartNew();

        var statuses = SourceInspector.Inspect();

        Assert.NotEmpty(statuses);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"inspection took {clock.Elapsed}");
    }

    [Fact]
    public void Every_source_is_reported_once()
    {
        var keys = SourceInspector.Inspect().Select(s => s.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains("security_audit", keys);
    }

    /// <summary>A probe that ran out of time must not be shown as a source that is switched off.</summary>
    [Fact]
    public void A_check_that_timed_out_reads_differently_from_a_missing_source()
    {
        Loc.Use("en");

        var unknown = new SourceRow(new SourceStatus("security_audit", SourceState.Unknown));
        var missing = new SourceRow(new SourceStatus("security_audit", SourceState.Missing));

        Assert.NotEqual(missing.StateText, unknown.StateText);
        Assert.Equal(Loc.T("doctor.unknown"), unknown.StateText);
        Assert.NotEqual("doctor.unknown", unknown.StateText);
    }
}
