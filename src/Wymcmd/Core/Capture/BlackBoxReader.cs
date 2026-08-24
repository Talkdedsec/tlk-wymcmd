using Microsoft.Diagnostics.Tracing;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;
using Wymcmd.Core.Store;
using Wymcmd.Core.Windows;

namespace Wymcmd.Core.Capture;

/// <summary>
/// Replays the circular trace the AutoLogger has been filling while nothing of ours was
/// running. Kernel-Process start events carry no command line, so those get filled in later
/// from the Security log or from the live process if it is somehow still around.
/// </summary>
public static class BlackBoxReader
{
    private const string ProviderName = "Microsoft-Windows-Kernel-Process";

    public static IReadOnlyList<ProcEvent> Read(DateTime from, DateTime to, string? tracePath = null)
    {
        var path = tracePath ?? AppPaths.BlackBoxTrace;
        var results = new List<ProcEvent>();
        if (!File.Exists(path)) return results;

        // The live session keeps the file open; work on a copy.
        var snapshot = Path.Combine(Path.GetTempPath(), $"wymcmd-blackbox-{Environment.ProcessId}.etl");
        try
        {
            File.Copy(path, snapshot, overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Warn("cannot read the black box trace: " + ex.Message);
            return results;
        }

        try
        {
            using var source = new ETWTraceEventSource(snapshot);
            source.Dynamic.All += data =>
            {
                if (!data.ProviderName.Equals(ProviderName, StringComparison.OrdinalIgnoreCase)) return;
                if (!data.EventName.StartsWith("ProcessStart", StringComparison.OrdinalIgnoreCase)) return;
                if (data.TimeStamp < from || data.TimeStamp > to) return;

                var imagePath = Payload(data, "ImageName") ?? "";
                var evt = new ProcEvent
                {
                    Pid = Int(data, "ProcessID"),
                    ParentPid = Int(data, "ParentProcessID"),
                    StartKey = ULong(data, "ProcessSequenceNumber"),
                    StartTime = data.TimeStamp,
                    ImagePath = PathNames.Normalize(imagePath),
                    SessionId = Int(data, "SessionID"),
                    Sources = EvidenceSource.BlackBox,
                    Confidence = Confidence.Certain
                };
                evt.ImageName = Path.GetFileName(evt.ImagePath);

                // Kernel-side entries with no image (the idle process and friends) carry nothing
                // a person could act on, and an empty row in the list is worse than no row.
                if (evt.ImageName.Length == 0) return;

                results.Add(evt);
            };

            source.Process();
        }
        catch (Exception ex)
        {
            Log.Warn("black box trace unreadable: " + ex.Message);
        }
        finally
        {
            try { File.Delete(snapshot); } catch { /* temp file */ }
        }

        return results;
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
