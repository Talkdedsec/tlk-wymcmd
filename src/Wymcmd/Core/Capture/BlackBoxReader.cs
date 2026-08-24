using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;
using Wymcmd.Core.Store;
using Wymcmd.Core.Windows;

namespace Wymcmd.Core.Capture;

/// <summary>
/// Replays the circular traces the AutoLoggers filled while nothing of ours was running.
/// The manifest session records every start but no command line; the system trace session
/// records the command line as well. Reading both and merging them gives the complete row.
/// </summary>
public static class BlackBoxReader
{
    private const string ProviderName = "Microsoft-Windows-Kernel-Process";

    public static IReadOnlyList<ProcEvent> Read(DateTime from, DateTime to, string? tracePath = null)
    {
        var results = new List<ProcEvent>();

        foreach (var path in tracePath is null
                     ? new[] { AppPaths.BlackBoxTrace, AppPaths.BlackBoxSystemTrace }
                     : [tracePath])
        {
            results.AddRange(ReadOne(path, from, to));
        }

        return Merge(results);
    }

    private static List<ProcEvent> ReadOne(string path, DateTime from, DateTime to)
    {
        var results = new List<ProcEvent>();
        if (!File.Exists(path)) return results;

        // The live session keeps the file open; work on a copy.
        var snapshot = Path.Combine(Path.GetTempPath(),
            $"wymcmd-{Path.GetFileNameWithoutExtension(path)}-{Environment.ProcessId}.etl");

        try
        {
            File.Copy(path, snapshot, overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Warn($"cannot read {Path.GetFileName(path)}: {ex.Message}");
            return results;
        }

        try
        {
            using var source = new ETWTraceEventSource(snapshot);

            // Manifest session: no command line, but every start is there.
            source.Dynamic.All += data =>
            {
                if (!data.ProviderName.Equals(ProviderName, StringComparison.OrdinalIgnoreCase)) return;
                if (!data.EventName.StartsWith("ProcessStart", StringComparison.OrdinalIgnoreCase)) return;
                if (data.TimeStamp < from || data.TimeStamp > to) return;

                Add(results, new ProcEvent
                {
                    Pid = Int(data, "ProcessID"),
                    ParentPid = Int(data, "ParentProcessID"),
                    StartKey = ULong(data, "ProcessSequenceNumber"),
                    StartTime = data.TimeStamp,
                    ImagePath = PathNames.Normalize(Payload(data, "ImageName")),
                    SessionId = Int(data, "SessionID"),
                    Sources = EvidenceSource.BlackBox,
                    Confidence = Confidence.Certain
                });
            };

            // System trace session: the classic kernel events, which carry the command line.
            source.Kernel.ProcessStart += data =>
            {
                if (data.TimeStamp < from || data.TimeStamp > to) return;

                Add(results, new ProcEvent
                {
                    Pid = data.ProcessID,
                    ParentPid = data.ParentID,
                    StartTime = data.TimeStamp,
                    ImagePath = PathNames.Normalize(data.ImageFileName),
                    CommandLine = data.CommandLine ?? "",
                    SessionId = (int)data.SessionID,
                    Sources = EvidenceSource.BlackBox,
                    Confidence = Confidence.Certain
                });
            };

            source.Kernel.ProcessStop += data =>
            {
                var match = results.LastOrDefault(evt => evt.Pid == data.ProcessID && evt.ExitTime is null);
                if (match is null) return;

                match.ExitTime = data.TimeStamp;
                match.ExitCode = data.ExitStatus;
            };

            source.Process();
        }
        catch (Exception ex)
        {
            Log.Warn($"{Path.GetFileName(path)} unreadable: {ex.Message}");
        }
        finally
        {
            try { File.Delete(snapshot); } catch { /* temp file */ }
        }

        return results;
    }

    private static void Add(List<ProcEvent> results, ProcEvent evt)
    {
        evt.ImageName = Path.GetFileName(evt.ImagePath);

        // Kernel-side entries with no image (the idle process and friends) carry nothing
        // a person could act on, and an empty row in the list is worse than no row.
        if (evt.ImageName.Length == 0) return;

        results.Add(evt);
    }

    /// <summary>The same launch seen by both sessions becomes one row, keeping the command line.</summary>
    private static List<ProcEvent> Merge(List<ProcEvent> events)
    {
        var merged = new Dictionary<(int Pid, long Second), ProcEvent>();

        foreach (var evt in events.OrderBy(evt => evt.CommandLine.Length))
        {
            var key = (evt.Pid, new DateTimeOffset(evt.StartTime).ToUnixTimeSeconds());

            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = evt;
                continue;
            }

            if (existing.CommandLine.Length == 0) existing.CommandLine = evt.CommandLine;
            if (existing.ImagePath.Length == 0) existing.ImagePath = evt.ImagePath;
            if (existing.ParentPid == 0) existing.ParentPid = evt.ParentPid;
            existing.ExitTime ??= evt.ExitTime;
            existing.ExitCode ??= evt.ExitCode;
        }

        return merged.Values.OrderBy(evt => evt.StartTime).ToList();
    }

    private static string? Payload(TraceEvent data, string name)
    {
        try
        {
            return data.PayloadByName(name)?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int Int(TraceEvent data, string name)
        => int.TryParse(Payload(data, name), out var value) ? value : 0;

    private static ulong ULong(TraceEvent data, string name)
        => ulong.TryParse(Payload(data, name), out var value) ? value : 0;
}
