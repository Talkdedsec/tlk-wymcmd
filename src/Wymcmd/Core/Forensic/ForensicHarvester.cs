using Wymcmd.Core.Capture;
using Wymcmd.Core.Model;
using Wymcmd.Core.Sign;
using Wymcmd.Core.Store;
using Wymcmd.Core.Why;

namespace Wymcmd.Core.Forensic;

/// <summary>
/// Rebuilds history from whatever this machine kept: our own database, Sysmon, the Security
/// log, Task Scheduler and PowerShell logging. Records are merged per process, the strongest
/// source wins each field, and the result says how sure it is.
/// </summary>
public sealed class ForensicHarvester(EventStore? store = null)
{
    private readonly AutostartIndex _autostart = new();

    public IReadOnlyList<ProcEvent> Window(DateTime from, DateTime to, int limit = 2000)
    {
        var merged = new Dictionary<(int Pid, long Second), ProcEvent>();

        void Absorb(IEnumerable<ProcEvent> events)
        {
            foreach (var evt in events)
            {
                if (evt.Pid == 0) continue;
                var key = (evt.Pid, new DateTimeOffset(evt.StartTime).ToUnixTimeSeconds());

                if (merged.TryGetValue(key, out var existing))
                    Merge(existing, evt);
                else
                    merged[key] = evt;
            }
        }

        // Weakest first so stronger sources overwrite what they know better.
        Absorb(EvtxReader.ProcessCreations(from, to, limit));
        Absorb(BlackBoxReader.Read(from, to));
        Absorb(EvtxReader.SysmonCreations(from, to, limit));
        if (store is not null)
            Absorb(store.Query(new EventFilter { From = from, To = to, Limit = limit }));

        var exits = EvtxReader.ProcessExits(from, to, limit);
        var tasks = EvtxReader.TaskLaunches(from - TimeSpan.FromMinutes(5), to, 1000);
        var scripts = EvtxReader.ScriptBlocks(from, to, 1000);

        var results = merged.Values.OrderBy(e => e.StartTime).ToList();
        var byPid = results.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var evt in results)
        {
            if (evt.ExitTime is null && exits.TryGetValue(evt.Pid, out var exit) && exit.When >= evt.StartTime)
            {
                evt.ExitTime = exit.When;
                evt.ExitCode = exit.Status;
            }

            BuildChain(evt, byPid);
            AttachTask(evt, tasks);
            AttachScript(evt, scripts);

            if (evt.Signature.Status == SignatureStatus.Unknown && evt.ImagePath.Length > 0)
                evt.Signature = SignatureVerifier.Check(evt.ImagePath);

            var decoded = CommandLineDecoder.Decode(evt.ImageName, evt.CommandLine);
            evt.DecodedCommand ??= decoded.Payload;

            if (evt.Source is null)
                new AttributionEngine(_autostart).Attribute(evt);

            // Nothing recorded whether a window appeared, so this stays an inference.
            if (evt.Window == WindowVisibility.Unknown && decoded.Traits.HasFlag(CommandTraits.HiddenWindow))
                evt.Window = WindowVisibility.Hidden;

            RiskScorer.Score(evt, decoded.Traits);
        }

        return results;
    }

    /// <summary>Everything that happened around a moment, newest sources merged in.</summary>
    public IReadOnlyList<ProcEvent> Around(DateTime moment, TimeSpan radius)
        => Window(moment - radius, moment + radius);

    public ProcEvent? FindByPid(int pid, DateTime? near = null)
    {
        var anchor = near ?? DateTime.Now;
        var window = Window(anchor.AddHours(-12), anchor.AddMinutes(1), 5000);
        return window.LastOrDefault(e => e.Pid == pid);
    }

    private static void Merge(ProcEvent target, ProcEvent extra)
    {
        target.Sources |= extra.Sources;
        if (extra.Confidence > target.Confidence) target.Confidence = extra.Confidence;

        if (target.CommandLine.Length == 0) target.CommandLine = extra.CommandLine;
        if (target.ImagePath.Length == 0) target.ImagePath = extra.ImagePath;
        if (target.ImageName.Length == 0) target.ImageName = extra.ImageName;
        if (target.ParentPid == 0) target.ParentPid = extra.ParentPid;
        if (target.ParentImageName.Length == 0) target.ParentImageName = extra.ParentImageName;
        target.ParentCommandLine ??= extra.ParentCommandLine;
        target.UserName ??= extra.UserName;
        target.UserSid ??= extra.UserSid;
        target.IntegrityLevel ??= extra.IntegrityLevel;
        target.Sha256 ??= extra.Sha256;
        target.DecodedCommand ??= extra.DecodedCommand;
        target.ExitTime ??= extra.ExitTime;
        target.ExitCode ??= extra.ExitCode;
        target.Source ??= extra.Source;

        if (target.Window == WindowVisibility.Unknown) target.Window = extra.Window;
        if (target.Signature.Status == SignatureStatus.Unknown) target.Signature = extra.Signature;
        if (target.Chain.Count == 0 && extra.Chain.Count > 0) target.Chain.AddRange(extra.Chain);
    }

    private static void BuildChain(ProcEvent evt, Dictionary<int, List<ProcEvent>> byPid)
    {
        if (evt.Chain.Count > 0) return;

        var current = evt;
        var seen = new HashSet<int> { evt.Pid };

        for (var depth = 0; depth < 12; depth++)
        {
            if (current.ParentPid <= 0 || !seen.Add(current.ParentPid))
            {
                if (current.ParentImageName.Length > 0)
                    evt.Chain.Add(new AncestorLink { Pid = current.ParentPid, ImageName = current.ParentImageName });
                return;
            }

            var parent = byPid.GetValueOrDefault(current.ParentPid)?
                .LastOrDefault(candidate => candidate.StartTime <= current.StartTime);

            if (parent is null)
            {
                evt.Chain.Add(new AncestorLink
                {
                    Pid = current.ParentPid,
                    ImageName = current.ParentImageName,
                    Alive = false
                });
                return;
            }

            evt.Chain.Add(new AncestorLink
            {
                Pid = parent.Pid,
                ImageName = parent.ImageName,
                ImagePath = parent.ImagePath,
                CommandLine = parent.CommandLine,
                StartTime = parent.StartTime,
                Alive = false
            });

            current = parent;
        }
    }

    private static void AttachTask(ProcEvent evt, IReadOnlyList<TaskLaunch> tasks)
    {
        // The scheduler logs the pid of the process it started, so this is a direct hit.
        var hit = tasks.FirstOrDefault(task =>
            (task.Pid == evt.Pid || task.Pid == evt.ParentPid) &&
            Math.Abs((task.When - evt.StartTime).TotalSeconds) < 60);

        if (hit is null) return;

        evt.Source = new LaunchSource
        {
            Kind = LaunchSourceKind.ScheduledTask,
            Name = hit.TaskPath,
            Location = "Task Scheduler",
            Confidence = Confidence.Certain,
            FoundVia = EvidenceSource.TaskLog
        };
        evt.Sources |= EvidenceSource.TaskLog;
    }

    private static void AttachScript(ProcEvent evt, IReadOnlyList<ScriptBlock> scripts)
    {
        if (evt.DecodedCommand is { Length: > 0 }) return;
        if (!evt.ImageName.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) &&
            !evt.ImageName.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase)) return;

        var blocks = scripts
            .Where(block => block.Pid == evt.Pid && block.When >= evt.StartTime.AddSeconds(-2))
            .OrderBy(block => block.When)
            .Select(block => block.Text)
            .ToList();

        if (blocks.Count == 0) return;

        evt.DecodedCommand = string.Join(Environment.NewLine, blocks);
        evt.Sources |= EvidenceSource.ScriptBlockLog;
    }
}
