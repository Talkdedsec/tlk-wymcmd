using Microsoft.Data.Sqlite;

namespace Wymcmd.Core.Store;

/// <summary>
/// Live and Service are written to the table. BlackBox never is - the recorder leaves no session
/// behind because nothing of ours is running while it records; its coverage is read back from the
/// trace instead.
/// </summary>
public enum WatchKind { Live = 0, Service = 1, BlackBox = 2 }

/// <summary>A stretch of time something was actually recording. Open means it still is.</summary>
public sealed record WatchSpan(WatchKind Kind, DateTime From, DateTime To, bool Open);

/// <summary>
/// Who was watching, and when.
///
/// Everything else in the tool answers "what launched this". That answer is worth far less if it
/// cannot say whether it was watched happen or pieced together afterwards from what Windows kept.
/// A session is opened when capture starts and closed when it stops; while it runs it leaves a
/// heartbeat, so a session that ends with the machine losing power still knows within a minute
/// where its coverage really ended. What is left between sessions is a gap, and the tool says so
/// out loud rather than presenting a reconstruction as a recording.
/// </summary>
public sealed class WatchLedger
{
    /// <summary>How often a running session marks that it is still there.</summary>
    public static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>Past this without a beat, a session that never closed is treated as lost.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(150);

    private readonly string _connectionString;

    public WatchLedger(string? path = null)
    {
        var file = path ?? AppPaths.Database;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        Initialize();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS watch_sessions (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                kind    INTEGER NOT NULL,
                started INTEGER NOT NULL,
                beat    INTEGER NOT NULL,
                stopped INTEGER,
                pid     INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_watch_started ON watch_sessions(started DESC);
            """;
        command.ExecuteNonQuery();
    }

    public long Begin(WatchKind kind)
    {
        var now = Stamp(DateTime.Now);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO watch_sessions (kind, started, beat, pid)
            VALUES ($kind, $now, $now, $pid);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$pid", Environment.ProcessId);

        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void Beat(long id) => Touch(id, "beat = $now");

    public void End(long id) => Touch(id, "beat = $now, stopped = $now");

    private void Touch(long id, string assignment)
    {
        if (id <= 0) return;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE watch_sessions SET {assignment} WHERE id = $id";
        command.Parameters.AddWithValue("$now", Stamp(DateTime.Now));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Sessions that overlap the window, clipped to it and merged, newest first. A session that
    /// never closed and has gone quiet ends at its last heartbeat, not at now - claiming coverage
    /// for a machine that was switched off is the one mistake this table exists to avoid.
    /// </summary>
    public IReadOnlyList<WatchSpan> Spans(DateTime from, DateTime to)
    {
        var raw = new List<WatchSpan>();

        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT kind, started, beat, stopped FROM watch_sessions
                WHERE started <= $to AND COALESCE(stopped, beat) >= $from
                ORDER BY started
                """;
            command.Parameters.AddWithValue("$from", Stamp(from));
            command.Parameters.AddWithValue("$to", Stamp(to));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = (WatchKind)reader.GetInt32(0);
                var started = Unstamp(reader.GetInt64(1));
                var beat = Unstamp(reader.GetInt64(2));
                var stopped = reader.IsDBNull(3) ? (DateTime?)null : Unstamp(reader.GetInt64(3));

                var open = stopped is null && DateTime.Now - beat < Stale;
                var ends = stopped ?? (open ? DateTime.Now : beat);

                raw.Add(new WatchSpan(kind, Later(started, from), Earlier(ends, to), open));
            }
        }

        return MergeSpans(raw);
    }

    /// <summary>
    /// What nothing was recording. These are the moments an answer can only be inferred.
    /// Anything shorter than a second is the seam between two sessions rather than a real hole,
    /// and listing it would bury the gaps that matter.
    /// </summary>
    public IReadOnlyList<(DateTime From, DateTime To)> Gaps(DateTime from, DateTime to)
    {
        var gaps = new List<(DateTime From, DateTime To)>();
        var cursor = from;

        foreach (var span in Spans(from, to).OrderBy(s => s.From))
        {
            if (span.From > cursor) gaps.Add((cursor, span.From));
            if (span.To > cursor) cursor = span.To;
        }

        if (cursor < to) gaps.Add((cursor, to));

        return gaps.Where(g => g.To - g.From >= TimeSpan.FromSeconds(1)).ToList();
    }

    public bool Covered(DateTime moment)
        => Spans(moment.AddSeconds(-1), moment.AddSeconds(1)).Any(s => s.From <= moment && moment <= s.To);

    /// <summary>Two watchers running at once is one covered stretch, not two.</summary>
    public static List<WatchSpan> MergeSpans(List<WatchSpan> spans)
    {
        var merged = new List<WatchSpan>();

        foreach (var span in spans.OrderBy(s => s.From))
        {
            var last = merged.Count > 0 ? merged[^1] : null;

            if (last is not null && span.From <= last.To)
            {
                merged[^1] = last with
                {
                    To = Later(last.To, span.To),
                    Open = last.Open || span.Open,
                    Kind = last.Kind
                };
                continue;
            }

            merged.Add(span);
        }

        merged.Reverse();
        return merged;
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static long Stamp(DateTime value)
        => new DateTimeOffset(value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value).ToUnixTimeMilliseconds();

    private static DateTime Unstamp(long value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;
}
