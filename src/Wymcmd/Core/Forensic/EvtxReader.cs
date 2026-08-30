using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Forensic;

public sealed record TaskLaunch(string TaskPath, int Pid, DateTime When, string? ActionName);

public sealed record ScriptBlock(DateTime When, int Pid, string Text);

public sealed record WmiConsumerHit(DateTime When, string Consumer, string? Query);

/// <summary>Somewhere a process reached, or a name it asked to have resolved.</summary>
public sealed record NetworkTouch(DateTime When, bool IsQuery, string Target, string? Detail);

/// <summary>
/// Reads what Windows already recorded on its own. This is the path that answers
/// "why did a console open at 14:22" on a machine where wymcmd was not even running.
/// </summary>
public static class EvtxReader
{
    private const string SecurityLog = "Security";
    private const string PowerShellLog = "Microsoft-Windows-PowerShell/Operational";
    private const string TaskLog = "Microsoft-Windows-TaskScheduler/Operational";
    private const string WmiLog = "Microsoft-Windows-WMI-Activity/Operational";
    private const string SysmonLog = "Microsoft-Windows-Sysmon/Operational";

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(5);

    /// <summary>Process creations from the Security log (event 4688).</summary>
    public static IReadOnlyList<ProcEvent> ProcessCreations(DateTime from, DateTime to, int limit = 5000)
    {
        var results = new List<ProcEvent>();

        foreach (var record in Read(SecurityLog, 4688, from, to, limit))
        {
            var fields = Fields(record);
            if (fields.Count == 0) continue;

            var evt = new ProcEvent
            {
                Pid = Hex(fields.GetValueOrDefault("NewProcessId")),
                ParentPid = Hex(fields.GetValueOrDefault("ProcessId")),
                StartTime = record.TimeCreated ?? from,
                ImagePath = fields.GetValueOrDefault("NewProcessName") ?? "",
                CommandLine = fields.GetValueOrDefault("CommandLine") ?? "",
                UserName = Account(fields),
                UserSid = fields.GetValueOrDefault("SubjectUserSid"),
                ParentImageName = FileName(fields.GetValueOrDefault("ParentProcessName")),
                IntegrityLevel = fields.GetValueOrDefault("MandatoryLabel"),
                Elevated = fields.GetValueOrDefault("TokenElevationType") is "%%1937",
                Sources = EvidenceSource.SecurityLog,
                Confidence = Confidence.High
            };
            evt.ImageName = FileName(evt.ImagePath);
            results.Add(evt);
        }

        return results;
    }

    /// <summary>Process exits (event 4689), used to fill in lifetime and exit status.</summary>
    public static IReadOnlyDictionary<int, (DateTime When, int? Status)> ProcessExits(DateTime from, DateTime to, int limit = 5000)
    {
        var results = new Dictionary<int, (DateTime, int?)>();

        foreach (var record in Read(SecurityLog, 4689, from, to, limit))
        {
            var fields = Fields(record);
            var pid = Hex(fields.GetValueOrDefault("ProcessId"));
            if (pid == 0) continue;

            int? status = int.TryParse(fields.GetValueOrDefault("Status"), out var parsed) ? parsed : null;
            results[pid] = (record.TimeCreated ?? to, status);
        }

        return results;
    }

    public static IReadOnlyList<ProcEvent> SysmonCreations(DateTime from, DateTime to, int limit = 5000)
    {
        var results = new List<ProcEvent>();

        foreach (var record in Read(SysmonLog, 1, from, to, limit))
        {
            var fields = Fields(record);
            if (fields.Count == 0) continue;

            var evt = new ProcEvent
            {
                Pid = int.TryParse(fields.GetValueOrDefault("ProcessId"), out var pid) ? pid : 0,
                ParentPid = int.TryParse(fields.GetValueOrDefault("ParentProcessId"), out var parent) ? parent : 0,
                StartTime = DateTime.TryParse(fields.GetValueOrDefault("UtcTime"), out var utc)
                    ? utc.ToLocalTime()
                    : record.TimeCreated ?? from,
                ImagePath = fields.GetValueOrDefault("Image") ?? "",
                CommandLine = fields.GetValueOrDefault("CommandLine") ?? "",
                UserName = fields.GetValueOrDefault("User"),
                ParentImageName = FileName(fields.GetValueOrDefault("ParentImage")),
                ParentCommandLine = fields.GetValueOrDefault("ParentCommandLine"),
                IntegrityLevel = fields.GetValueOrDefault("IntegrityLevel"),
                Sha256 = HashOf(fields.GetValueOrDefault("Hashes"), "SHA256"),
                Sources = EvidenceSource.Sysmon,
                Confidence = Confidence.Certain
            };
            evt.ImageName = FileName(evt.ImagePath);
            results.Add(evt);
        }

        return results;
    }

    /// <summary>Task Scheduler telling us which task owns which pid (events 129 and 200).</summary>
    public static IReadOnlyList<TaskLaunch> TaskLaunches(DateTime from, DateTime to, int limit = 2000)
    {
        var results = new List<TaskLaunch>();

        foreach (var record in Read(TaskLog, 129, from, to, limit))
        {
            var fields = Fields(record);
            var task = fields.GetValueOrDefault("TaskName");
            if (task is null) continue;

            results.Add(new TaskLaunch(
                task,
                int.TryParse(fields.GetValueOrDefault("ProcessID"), out var pid) ? pid : 0,
                record.TimeCreated ?? from,
                fields.GetValueOrDefault("Path")));
        }

        foreach (var record in Read(TaskLog, 200, from, to, limit))
        {
            var fields = Fields(record);
            var task = fields.GetValueOrDefault("TaskName");
            if (task is null) continue;

            results.Add(new TaskLaunch(
                task,
                int.TryParse(fields.GetValueOrDefault("EnginePID"), out var pid) ? pid : 0,
                record.TimeCreated ?? from,
                fields.GetValueOrDefault("ActionName")));
        }

        return results;
    }

    /// <summary>PowerShell script block logging - the real script behind an encoded command.</summary>
    public static IReadOnlyList<ScriptBlock> ScriptBlocks(DateTime from, DateTime to, int limit = 2000)
    {
        var results = new List<ScriptBlock>();

        foreach (var record in Read(PowerShellLog, 4104, from, to, limit))
        {
            var fields = Fields(record);
            var text = fields.GetValueOrDefault("ScriptBlockText");
            if (string.IsNullOrWhiteSpace(text)) continue;

            results.Add(new ScriptBlock(record.TimeCreated ?? from, record.ProcessId ?? 0, text));
        }

        return results;
    }

    public static IReadOnlyList<WmiConsumerHit> WmiConsumerActivity(DateTime from, DateTime to, int limit = 500)
    {
        var results = new List<WmiConsumerHit>();

        foreach (var record in Read(WmiLog, 5861, from, to, limit))
        {
            var description = SafeDescription(record);
            if (description is null) continue;

            results.Add(new WmiConsumerHit(record.TimeCreated ?? from, description, null));
        }

        return results;
    }

    /// <summary>
    /// Where one process reached while it was alive, from Sysmon's connection (3) and DNS query
    /// (22) events. Only Sysmon records this per process; without it there is nothing to read and
    /// the answer is an honest empty list rather than a guess from machine-wide DNS.
    /// </summary>
    public static IReadOnlyList<NetworkTouch> NetworkTouches(int pid, DateTime from, DateTime to, int limit = 400)
    {
        var results = new List<NetworkTouch>();

        foreach (var record in Read(SysmonLog, 3, from, to, limit))
        {
            var fields = Fields(record);
            if (Number(fields.GetValueOrDefault("ProcessId")) != pid) continue;

            var host = fields.GetValueOrDefault("DestinationHostname");
            var address = fields.GetValueOrDefault("DestinationIp") ?? "?";
            var port = fields.GetValueOrDefault("DestinationPort");
            var protocol = fields.GetValueOrDefault("Protocol")?.ToUpperInvariant();

            var target = host is { Length: > 0 } ? $"{host} ({address})" : address;
            if (port is { Length: > 0 }) target += ":" + port;

            results.Add(new NetworkTouch(record.TimeCreated ?? from, IsQuery: false, target, protocol));
        }

        foreach (var record in Read(SysmonLog, 22, from, to, limit))
        {
            var fields = Fields(record);
            if (Number(fields.GetValueOrDefault("ProcessId")) != pid) continue;

            var name = fields.GetValueOrDefault("QueryName");
            if (name is not { Length: > 0 }) continue;

            results.Add(new NetworkTouch(
                record.TimeCreated ?? from, IsQuery: true, name, Resolved(fields.GetValueOrDefault("QueryResults"))));
        }

        return results.OrderBy(touch => touch.When).ToList();
    }

    /// <summary>Sysmon writes results as "type: 5 ::ffff:1.2.3.4;type: 5 ...;" - keep the addresses.</summary>
    public static string? Resolved(string? results)
    {
        if (results is not { Length: > 0 }) return null;

        var addresses = results
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Replace("type:", "", StringComparison.OrdinalIgnoreCase).Trim())
            .Select(part => part.Contains(' ') ? part[(part.LastIndexOf(' ') + 1)..] : part)
            .Select(part => part.Replace("::ffff:", "", StringComparison.OrdinalIgnoreCase))
            .Where(part => part.Length > 0 && part != "-")
            .Distinct()
            .Take(4)
            .ToList();

        return addresses.Count > 0 ? string.Join(", ", addresses) : null;
    }

    private static int Number(string? value) => int.TryParse(value, out var parsed) ? parsed : 0;

    private static IEnumerable<EventRecord> Read(string log, int eventId, DateTime from, DateTime to, int limit)
    {
        EventLogReader reader;
        try
        {
            var xpath = $"*[System[EventID={eventId} and TimeCreated[@SystemTime>='{from.ToUniversalTime():o}' " +
                        $"and @SystemTime<='{to.ToUniversalTime():o}']]]";

            reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
        }
        catch (EventLogNotFoundException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            Log.Debug($"event log {log} needs administrator rights");
            yield break;
        }
        catch (EventLogException ex)
        {
            Log.Debug($"event log {log} unavailable: {ex.Message}");
            yield break;
        }

        using (reader)
        {
            // A filtered read still walks the log, and these logs run to hundreds of megabytes.
            // The caller is often the window, so the walk gets a budget rather than the chance to
            // hold the interface for as long as the machine's history happens to be long.
            var clock = Stopwatch.StartNew();

            for (var i = 0; i < limit; i++)
            {
                var left = ReadBudget - clock.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    Log.Debug($"event log {log} read gave up after {i} records");
                    yield break;
                }

                EventRecord? record;
                try
                {
                    record = reader.ReadEvent(left);
                }
                catch (EventLogException)
                {
                    yield break;
                }

                if (record is null) yield break;
                using (record) yield return record;
            }
        }
    }

    private static Dictionary<string, string> Fields(EventRecord record)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var document = XDocument.Parse(record.ToXml());
            var ns = document.Root?.Name.Namespace ?? XNamespace.None;

            foreach (var data in document.Descendants(ns + "Data"))
            {
                var name = data.Attribute("Name")?.Value;
                if (name is null) continue;
                values[name] = data.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("event xml unreadable: " + ex.Message);
        }

        return values;
    }

    private static string? SafeDescription(EventRecord record)
    {
        try
        {
            return record.FormatDescription();
        }
        catch (EventLogException)
        {
            return null;
        }
    }

    private static int Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex : 0;
        return int.TryParse(value, out var plain) ? plain : 0;
    }

    private static string FileName(string? path)
        => string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path);

    private static string? Account(Dictionary<string, string> fields)
    {
        var user = fields.GetValueOrDefault("SubjectUserName");
        var domain = fields.GetValueOrDefault("SubjectDomainName");
        if (string.IsNullOrEmpty(user)) return null;
        return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }

    private static string? HashOf(string? hashes, string algorithm)
    {
        if (string.IsNullOrWhiteSpace(hashes)) return null;
        foreach (var part in hashes.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(algorithm, StringComparison.OrdinalIgnoreCase))
                return pair[1].Trim();
        }
        return null;
    }
}
