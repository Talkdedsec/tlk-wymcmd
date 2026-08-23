using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Store;

public sealed record EventFilter
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public bool ConsoleOnly { get; init; }
    public bool HiddenOnly { get; init; }
    public bool UnsignedOnly { get; init; }
    public int MinRisk { get; init; }
    public string? Text { get; init; }
    public int Limit { get; init; } = 200;
}

/// <summary>
/// SQLite behind a channel: capture threads never touch the file, a single writer drains
/// the queue in batches. Reads go through short-lived connections.
/// </summary>
public sealed class EventStore : IAsyncDisposable, IDisposable
{
    private const int BatchSize = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _connectionString;
    private readonly Channel<ProcEvent> _queue = Channel.CreateUnbounded<ProcEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writer;

    public EventStore(string? path = null)
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
        _writer = Task.Run(DrainAsync);
    }

    public void Enqueue(ProcEvent evt) => _queue.Writer.TryWrite(evt);

    public async Task FlushAsync()
    {
        // Give the writer a moment to pick up whatever is queued.
        for (var i = 0; i < 40 && _queue.Reader.Count > 0; i++)
            await Task.Delay(25);
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS events (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                pid            INTEGER NOT NULL,
                parent_pid     INTEGER NOT NULL,
                start_key      INTEGER NOT NULL DEFAULT 0,
                start_time     INTEGER NOT NULL,
                exit_time      INTEGER,
                exit_code      INTEGER,
                image_name     TEXT NOT NULL,
                image_path     TEXT,
                command_line   TEXT,
                decoded        TEXT,
                work_dir       TEXT,
                user_name      TEXT,
                user_sid       TEXT,
                session_id     INTEGER,
                elevated       INTEGER,
                integrity      TEXT,
                parent_image   TEXT,
                parent_cmdline TEXT,
                window         INTEGER,
                sign_status    INTEGER,
                sign_publisher TEXT,
                source_kind    INTEGER,
                source_name    TEXT,
                source_where   TEXT,
                sources        INTEGER,
                confidence     INTEGER,
                risk           INTEGER,
                risk_factors   TEXT,
                chain          TEXT,
                sha256         TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_events_start ON events(start_time DESC);
            CREATE INDEX IF NOT EXISTS idx_events_image ON events(image_name);
            CREATE INDEX IF NOT EXISTS idx_events_risk  ON events(risk DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_events_identity ON events(pid, start_time, image_name);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private async Task DrainAsync()
    {
        var batch = new List<ProcEvent>(BatchSize);
        var reader = _queue.Reader;

        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(_shutdown.Token)) break;

                var deadline = DateTime.UtcNow + FlushInterval;
                while (batch.Count < BatchSize && DateTime.UtcNow < deadline && reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count == 0)
                {
                    await Task.Delay(FlushInterval, _shutdown.Token);
                    continue;
                }

                Write(batch);
                batch.Clear();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("event store write failed: " + ex.Message);
                batch.Clear();
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        while (reader.TryRead(out var leftover)) batch.Add(leftover);
        if (batch.Count > 0)
        {
            try { Write(batch); } catch { /* shutting down */ }
        }
    }

    private void Write(List<ProcEvent> batch)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO events
            (pid, parent_pid, start_key, start_time, exit_time, exit_code, image_name, image_path,
             command_line, decoded, work_dir, user_name, user_sid, session_id, elevated, integrity,
             parent_image, parent_cmdline, window, sign_status, sign_publisher, source_kind,
             source_name, source_where, sources, confidence, risk, risk_factors, chain, sha256)
            VALUES
            ($pid, $parent_pid, $start_key, $start_time, $exit_time, $exit_code, $image_name, $image_path,
             $command_line, $decoded, $work_dir, $user_name, $user_sid, $session_id, $elevated, $integrity,
             $parent_image, $parent_cmdline, $window, $sign_status, $sign_publisher, $source_kind,
             $source_name, $source_where, $sources, $confidence, $risk, $risk_factors, $chain, $sha256);
            """;

        var parameters = command.Parameters;
        foreach (var name in new[]
        {
            "$pid", "$parent_pid", "$start_key", "$start_time", "$exit_time", "$exit_code", "$image_name",
            "$image_path", "$command_line", "$decoded", "$work_dir", "$user_name", "$user_sid", "$session_id",
            "$elevated", "$integrity", "$parent_image", "$parent_cmdline", "$window", "$sign_status",
            "$sign_publisher", "$source_kind", "$source_name", "$source_where", "$sources", "$confidence",
            "$risk", "$risk_factors", "$chain", "$sha256"
        })
        {
            parameters.Add(command.CreateParameter());
            parameters[^1].ParameterName = name;
        }

        foreach (var evt in batch)
        {
            parameters["$pid"].Value = evt.Pid;
            parameters["$parent_pid"].Value = evt.ParentPid;
            parameters["$start_key"].Value = (long)evt.StartKey;
            parameters["$start_time"].Value = Stamp(evt.StartTime);
            parameters["$exit_time"].Value = evt.ExitTime is { } exit ? Stamp(exit) : DBNull.Value;
            parameters["$exit_code"].Value = evt.ExitCode as object ?? DBNull.Value;
            parameters["$image_name"].Value = evt.ImageName;
            parameters["$image_path"].Value = evt.ImagePath;
            parameters["$command_line"].Value = evt.CommandLine;
            parameters["$decoded"].Value = evt.DecodedCommand as object ?? DBNull.Value;
            parameters["$work_dir"].Value = evt.WorkingDirectory as object ?? DBNull.Value;
            parameters["$user_name"].Value = evt.UserName as object ?? DBNull.Value;
            parameters["$user_sid"].Value = evt.UserSid as object ?? DBNull.Value;
            parameters["$session_id"].Value = evt.SessionId;
            parameters["$elevated"].Value = evt.Elevated ? 1 : 0;
            parameters["$integrity"].Value = evt.IntegrityLevel as object ?? DBNull.Value;
            parameters["$parent_image"].Value = evt.ParentImageName;
            parameters["$parent_cmdline"].Value = evt.ParentCommandLine as object ?? DBNull.Value;
            parameters["$window"].Value = (int)evt.Window;
            parameters["$sign_status"].Value = (int)evt.Signature.Status;
            parameters["$sign_publisher"].Value = evt.Signature.Publisher as object ?? DBNull.Value;
            parameters["$source_kind"].Value = (int)(evt.Source?.Kind ?? LaunchSourceKind.Unknown);
            parameters["$source_name"].Value = evt.Source?.Name as object ?? DBNull.Value;
            parameters["$source_where"].Value = evt.Source?.Location as object ?? DBNull.Value;
            parameters["$sources"].Value = (int)evt.Sources;
            parameters["$confidence"].Value = (int)evt.Confidence;
            parameters["$risk"].Value = evt.Risk;
            parameters["$risk_factors"].Value = evt.RiskFactors.Count == 0
                ? DBNull.Value
                : JsonSerializer.Serialize(evt.RiskFactors.Select(f => new { f.Key, f.Weight, f.Detail }));
            parameters["$chain"].Value = evt.Chain.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(evt.Chain);
            parameters["$sha256"].Value = evt.Sha256 as object ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<ProcEvent> Query(EventFilter filter)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        var where = new List<string>();
        if (filter.From is { } from) { where.Add("start_time >= $from"); command.Parameters.AddWithValue("$from", Stamp(from)); }
        if (filter.To is { } to) { where.Add("start_time <= $to"); command.Parameters.AddWithValue("$to", Stamp(to)); }
        if (filter.HiddenOnly) where.Add($"window = {(int)WindowVisibility.Hidden}");
        if (filter.UnsignedOnly) where.Add($"sign_status = {(int)SignatureStatus.Unsigned}");
        if (filter.MinRisk > 0) { where.Add("risk >= $risk"); command.Parameters.AddWithValue("$risk", filter.MinRisk); }
        if (filter.ConsoleOnly)
        {
            var names = string.Join(", ", ProcEvent.ConsoleImages.Select(n => "'" + n + "'"));
            where.Add($"image_name COLLATE NOCASE IN ({names})");
        }
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            where.Add("(image_name LIKE $text OR command_line LIKE $text OR source_name LIKE $text OR image_path LIKE $text)");
            command.Parameters.AddWithValue("$text", "%" + filter.Text.Trim() + "%");
        }

        command.CommandText = "SELECT * FROM events"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY start_time DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(filter.Limit, 1, 100_000));

        using var reader = command.ExecuteReader();
        var results = new List<ProcEvent>();
        while (reader.Read()) results.Add(Read(reader));
        return results;
    }

    public ProcEvent? FindByPid(int pid, DateTime? near = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = near is null
            ? "SELECT * FROM events WHERE pid = $pid ORDER BY start_time DESC LIMIT 1"
            : "SELECT * FROM events WHERE pid = $pid ORDER BY ABS(start_time - $near) LIMIT 1";
        command.Parameters.AddWithValue("$pid", pid);
        if (near is { } moment) command.Parameters.AddWithValue("$near", Stamp(moment));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public long CountAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    public void UpdateExit(int pid, DateTime startTime, DateTime exitTime, int? exitCode)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE events SET exit_time = $exit, exit_code = $code
            WHERE pid = $pid AND start_time = $start
            """;
        command.Parameters.AddWithValue("$exit", Stamp(exitTime));
        command.Parameters.AddWithValue("$code", exitCode as object ?? DBNull.Value);
        command.Parameters.AddWithValue("$pid", pid);
        command.Parameters.AddWithValue("$start", Stamp(startTime));
        command.ExecuteNonQuery();
    }

    public int Prune(TimeSpan keepFor)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM events WHERE start_time < $cutoff";
        command.Parameters.AddWithValue("$cutoff", Stamp(DateTime.Now - keepFor));
        var removed = command.ExecuteNonQuery();

        if (removed > 0)
        {
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            vacuum.ExecuteNonQuery();
        }
        return removed;
    }

    private static long Stamp(DateTime value)
        => new DateTimeOffset(value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value).ToUnixTimeMilliseconds();

    private static DateTime Unstamp(long value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;

    private static ProcEvent Read(SqliteDataReader reader)
    {
        string? Text(string column)
        {
            var index = reader.GetOrdinal(column);
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }

        int? Number(string column)
        {
            var index = reader.GetOrdinal(column);
            return reader.IsDBNull(index) ? null : reader.GetInt32(index);
        }

        var evt = new ProcEvent
        {
            RowId = reader.GetInt64(reader.GetOrdinal("id")),
            Pid = reader.GetInt32(reader.GetOrdinal("pid")),
            ParentPid = reader.GetInt32(reader.GetOrdinal("parent_pid")),
            StartKey = (ulong)reader.GetInt64(reader.GetOrdinal("start_key")),
            StartTime = Unstamp(reader.GetInt64(reader.GetOrdinal("start_time"))),
            ExitTime = reader.IsDBNull(reader.GetOrdinal("exit_time")) ? null : Unstamp(reader.GetInt64(reader.GetOrdinal("exit_time"))),
            ExitCode = Number("exit_code"),
            ImageName = reader.GetString(reader.GetOrdinal("image_name")),
            ImagePath = Text("image_path") ?? "",
            CommandLine = Text("command_line") ?? "",
            DecodedCommand = Text("decoded"),
            WorkingDirectory = Text("work_dir"),
            UserName = Text("user_name"),
            UserSid = Text("user_sid"),
            SessionId = Number("session_id") ?? 0,
            Elevated = Number("elevated") == 1,
            IntegrityLevel = Text("integrity"),
            ParentImageName = Text("parent_image") ?? "",
            ParentCommandLine = Text("parent_cmdline"),
            Window = (WindowVisibility)(Number("window") ?? 0),
            Sources = (EvidenceSource)(Number("sources") ?? 0),
            Confidence = (Confidence)(Number("confidence") ?? 0),
            Risk = Number("risk") ?? 0,
            Sha256 = Text("sha256"),
            Signature = new SignatureInfo
            {
                Status = (SignatureStatus)(Number("sign_status") ?? 0),
                Publisher = Text("sign_publisher")
            }
        };

        var kind = (LaunchSourceKind)(Number("source_kind") ?? 0);
        if (kind != LaunchSourceKind.Unknown || Text("source_name") is not null)
        {
            evt.Source = new LaunchSource
            {
                Kind = kind,
                Name = Text("source_name"),
                Location = Text("source_where"),
                Confidence = evt.Confidence
            };
        }

        if (Text("chain") is { } chainJson)
        {
            try
            {
                var links = JsonSerializer.Deserialize<List<AncestorLink>>(chainJson);
                if (links is not null) evt.Chain.AddRange(links);
            }
            catch (JsonException) { /* older row format */ }
        }

        if (Text("risk_factors") is { } factorJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(factorJson);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    evt.RiskFactors.Add(new RiskFactor(
                        item.GetProperty("Key").GetString() ?? "",
                        item.GetProperty("Weight").GetInt32(),
                        item.TryGetProperty("Detail", out var detail) ? detail.GetString() : null));
                }
            }
            catch (JsonException) { /* older row format */ }
        }

        return evt;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _shutdown.CancelAfter(TimeSpan.FromSeconds(3));
        try { await _writer; } catch { /* shutdown */ }
        _shutdown.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
