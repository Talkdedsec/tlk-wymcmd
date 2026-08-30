using Microsoft.Data.Sqlite;
using Wymcmd.Core.Store;
using Xunit;

namespace Wymcmd.Tests;

public class WatchLedgerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"wymcmd-watch-{Guid.NewGuid():N}.db");

    private WatchLedger New() => new(_file);

    /// <summary>Writes a session straight into the table so the past can be set up on purpose.</summary>
    private void Session(DateTime started, DateTime beat, DateTime? stopped, WatchKind kind = WatchKind.Live)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _file }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO watch_sessions (kind, started, beat, stopped, pid)
            VALUES ($kind, $started, $beat, $stopped, 1)
            """;
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$started", Stamp(started));
        command.Parameters.AddWithValue("$beat", Stamp(beat));
        command.Parameters.AddWithValue("$stopped", stopped is null ? DBNull.Value : Stamp(stopped.Value));
        command.ExecuteNonQuery();
    }

    private static long Stamp(DateTime value) => new DateTimeOffset(value).ToUnixTimeMilliseconds();

    [Fact]
    public void A_session_that_was_closed_becomes_the_stretch_it_covered()
    {
        var ledger = New();
        var start = DateTime.Now.AddHours(-5);
        Session(start, start.AddHours(1), start.AddHours(1));

        var spans = ledger.Spans(DateTime.Now.AddHours(-6), DateTime.Now);

        var span = Assert.Single(spans);
        Assert.False(span.Open);
        Assert.Equal(start, span.From, TimeSpan.FromSeconds(1));
        Assert.Equal(start.AddHours(1), span.To, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The machine losing power leaves a session that was never closed. Its coverage ends at the
    /// last heartbeat - claiming it up to now would say the tool was watching while the machine
    /// was off, which is the one lie this table exists to prevent.
    /// </summary>
    [Fact]
    public void A_session_that_was_lost_ends_at_its_last_heartbeat()
    {
        var ledger = New();
        var start = DateTime.Now.AddHours(-4);
        Session(start, start.AddMinutes(30), stopped: null);

        var span = Assert.Single(ledger.Spans(DateTime.Now.AddHours(-6), DateTime.Now));

        Assert.False(span.Open);
        Assert.Equal(start.AddMinutes(30), span.To, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_session_still_beating_runs_up_to_now_and_reads_as_open()
    {
        var ledger = New();
        var start = DateTime.Now.AddMinutes(-10);
        Session(start, DateTime.Now, stopped: null);

        var span = Assert.Single(ledger.Spans(DateTime.Now.AddHours(-1), DateTime.Now));

        Assert.True(span.Open);
        Assert.Equal(DateTime.Now, span.To, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Two_watchers_at_once_are_one_covered_stretch()
    {
        var ledger = New();
        var start = DateTime.Now.AddHours(-3);
        Session(start, start.AddHours(1), start.AddHours(1));
        Session(start.AddMinutes(30), start.AddHours(2), start.AddHours(2), WatchKind.Service);

        var span = Assert.Single(ledger.Spans(DateTime.Now.AddHours(-6), DateTime.Now));

        Assert.Equal(start, span.From, TimeSpan.FromSeconds(1));
        Assert.Equal(start.AddHours(2), span.To, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void What_is_left_between_sessions_is_reported_as_a_gap()
    {
        var ledger = New();
        var now = DateTime.Now;
        Session(now.AddHours(-5), now.AddHours(-4), now.AddHours(-4));
        Session(now.AddHours(-2), now.AddHours(-1), now.AddHours(-1));

        var gaps = ledger.Gaps(now.AddHours(-6), now);

        Assert.Equal(3, gaps.Count);
        Assert.Equal(now.AddHours(-4), gaps[1].From, TimeSpan.FromSeconds(1));
        Assert.Equal(now.AddHours(-2), gaps[1].To, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_moment_inside_a_session_is_covered_and_one_outside_is_not()
    {
        var ledger = New();
        var start = DateTime.Now.AddHours(-3);
        Session(start, start.AddHours(1), start.AddHours(1));

        Assert.True(ledger.Covered(start.AddMinutes(30)));
        Assert.False(ledger.Covered(start.AddHours(2)));
    }

    [Fact]
    public void An_empty_ledger_reports_the_whole_window_as_a_gap()
    {
        var ledger = New();
        var now = DateTime.Now;

        Assert.Empty(ledger.Spans(now.AddDays(-1), now));
        Assert.Single(ledger.Gaps(now.AddDays(-1), now));
    }

    [Fact]
    public void Beginning_and_ending_a_session_records_the_time_it_ran()
    {
        var ledger = New();
        var id = ledger.Begin(WatchKind.Service);
        ledger.Beat(id);
        ledger.End(id);

        var span = Assert.Single(ledger.Spans(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(5)));

        Assert.Equal(WatchKind.Service, span.Kind);
        Assert.False(span.Open);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch { /* the file is a temp file either way */ }
    }
}
