using Microsoft.Data.Sqlite;

namespace Wymcmd.Core.Store;

public sealed record Tally(string Label, int Count);

public sealed record StatsSnapshot(
    int Total,
    int Consoles,
    int Hidden,
    int Unsigned,
    IReadOnlyList<Tally> TopLaunchers,
    IReadOnlyList<Tally> TopImages,
    IReadOnlyList<Tally> TopCommands,
    IReadOnlyList<Tally> ByHour);

/// <summary>
/// Aggregates over the recorded history - which programs open consoles here, at what hours,
/// and with what command lines. Patterns show up in this view that single events never do.
/// </summary>
public static class EventStats
{
    public static StatsSnapshot Collect(EventStore store, TimeSpan window)
    {
        var since = new DateTimeOffset(DateTime.Now - window).ToUnixTimeMilliseconds();
        using var connection = store.OpenRead();

        return new StatsSnapshot(
            Scalar(connection, "SELECT COUNT(*) FROM events WHERE start_time >= $since", since),
            Scalar(connection, $"SELECT COUNT(*) FROM events WHERE start_time >= $since AND {ConsoleFilter}", since),
            Scalar(connection, "SELECT COUNT(*) FROM events WHERE start_time >= $since AND window = 2", since),
            Scalar(connection, "SELECT COUNT(*) FROM events WHERE start_time >= $since AND sign_status = 1", since),
            Tallies(connection, """
                SELECT COALESCE(NULLIF(parent_image, ''), '?'), COUNT(*) c
                FROM events WHERE start_time >= $since
                GROUP BY 1 ORDER BY c DESC LIMIT 10
                """, since),
            Tallies(connection, """
                SELECT image_name, COUNT(*) c
                FROM events WHERE start_time >= $since
                GROUP BY 1 ORDER BY c DESC LIMIT 10
                """, since),
            Tallies(connection, $"""
                SELECT command_line, COUNT(*) c
                FROM events WHERE start_time >= $since AND command_line <> '' AND {ConsoleFilter}
                GROUP BY 1 ORDER BY c DESC LIMIT 10
                """, since),
            Tallies(connection, """
                SELECT strftime('%H', start_time / 1000, 'unixepoch', 'localtime') h, COUNT(*) c
                FROM events WHERE start_time >= $since
                GROUP BY h ORDER BY h
                """, since));
    }

    private static string ConsoleFilter
    {
        get
        {
            var names = string.Join(", ", Model.ProcEvent.ConsoleImages.Select(name => "'" + name + "'"));
            return $"image_name COLLATE NOCASE IN ({names})";
        }
    }

    private static int Scalar(SqliteConnection connection, string sql, long since)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$since", since);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static List<Tally> Tallies(SqliteConnection connection, string sql, long since)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$since", since);

        using var reader = command.ExecuteReader();
        var results = new List<Tally>();
        while (reader.Read())
            results.Add(new Tally(reader.IsDBNull(0) ? "?" : reader.GetString(0), reader.GetInt32(1)));

        return results;
    }
}
