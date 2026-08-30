using Wymcmd.Core.Forensic;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Sign;
using Wymcmd.Core.Store;
using Wymcmd.Core.Tree;
using Wymcmd.Core.Why;
using Wymcmd.Core.Windows;

namespace Wymcmd.Cli.Commands;

public static class WhyCommand
{
    public static int Run(CliOptions options)
    {
        var positional = options.Positional();
        var target = positional.FirstOrDefault() ?? "last";

        using var store = new EventStore();
        ProcEvent? evt = null;

        if (target.Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            evt = store.Query(new EventFilter { ConsoleOnly = !options.Has("--any"), Limit = 1 }).FirstOrDefault()
                  ?? new ForensicHarvester(store)
                      .Window(DateTime.Now.AddHours(-12), DateTime.Now, 3000)
                      .LastOrDefault(candidate => options.Has("--any") || candidate.IsConsoleHost);
        }
        else if (int.TryParse(target, out var pid))
        {
            evt = store.FindByPid(pid) ?? FromLiveProcess(pid) ?? new ForensicHarvester(store).FindByPid(pid);
        }

        if (evt is null)
        {
            ConsoleHost.Dim(Loc.T("cli.error.not_found"));
            return CommandRouter.ExitNotFound;
        }

        if (options.Json)
        {
            ConsoleHost.Line(EventFormatter.Json(evt));
            return CommandRouter.ExitOk;
        }

        EventFormatter.Detail(evt);
        PrintNetwork(evt);
        PrintCoverage(evt);
        PrintHistory(evt);
        return CommandRouter.ExitOk;
    }

    /// <summary>
    /// Where it reached while it was alive. Only Sysmon records this per process, so on a machine
    /// without it there is nothing to show and nothing is claimed - a machine-wide DNS log would
    /// not tell us which process asked.
    /// </summary>
    private static void PrintNetwork(ProcEvent evt)
    {
        var until = evt.ExitTime ?? evt.StartTime.AddHours(2);
        if (until > DateTime.Now) until = DateTime.Now;

        var touches = EvtxReader.NetworkTouches(evt.Pid, evt.StartTime.AddSeconds(-1), until);
        if (touches.Count == 0) return;

        ConsoleHost.Line();
        ConsoleHost.Strong(Loc.T("why.network"));

        foreach (var touch in touches.Take(12))
        {
            var what = Loc.T(touch.IsQuery ? "network.query" : "network.connect");
            var detail = touch.Detail is { Length: > 0 } ? ConsoleHost.Color("  " + touch.Detail, 90) : "";
            ConsoleHost.Line($"  {touch.When.ToString("T", Loc.Culture),-10} {what,-9} {touch.Target}{detail}");
        }

        if (touches.Count > 12) ConsoleHost.Dim("  " + Loc.T("network.more", touches.Count - 12));
    }

    /// <summary>
    /// Whether this was watched happen or reconstructed afterwards. Both answers are useful and
    /// they are not the same answer, so the reader is told which one they are holding.
    /// </summary>
    private static void PrintCoverage(ProcEvent evt)
    {
        try
        {
            if (Core.Coverage.CoverageReport.Covered(evt.StartTime)) return;

            ConsoleHost.Line();
            ConsoleHost.Dim(Loc.T("coverage.not_watched"));
        }
        catch
        {
            // The ledger is a nicety; never let it get in the way of the answer.
        }
    }

    /// <summary>Does this binary have a past on this machine, or did it show up today?</summary>
    private static void PrintHistory(ProcEvent evt)
    {
        var traces = ExecutionHistory.For(evt.ImageName, evt.ImagePath.Length > 0 ? evt.ImagePath : null);
        if (traces.Count == 0) return;

        ConsoleHost.Line();
        ConsoleHost.Strong(Loc.T("why.history"));

        if (ExecutionHistory.FirstSeen(evt.ImagePath, evt.ImageName) is { } first)
        {
            if (first.FirstSeen is { } when)
                ConsoleHost.Line($"  {"AmCache",-12} {Loc.T("history.first_seen", when.ToString("g", Loc.Culture), Loc.Ago(when))}");

            if (first.Sha1 is { Length: > 0 } hash)
                ConsoleHost.Dim($"  {"",-12} sha1 {hash.ToLowerInvariant()}");
        }

        foreach (var trace in traces.Take(4))
        {
            var when = trace.LastRun is { } stamp
                ? $"{stamp.ToString("g", Loc.Culture)}  ({Loc.Ago(stamp)})"
                : "-";
            var count = trace.RunCount is { } runs ? "  " + Loc.T("history.run_count", runs) : "";
            ConsoleHost.Line($"  {trace.Source,-12} {when}{count}");
        }
    }

    /// <summary>
    /// Nothing recorded, but the process is still running - rebuild the answer from the live
    /// system so the tool is useful even on a machine where it has never captured anything.
    /// </summary>
    private static ProcEvent? FromLiveProcess(int pid)
    {
        if (!ProcessQuery.IsAlive(pid)) return null;

        var tree = new ProcessTree();
        tree.Seed();

        var record = tree.Resolve(pid);
        if (record is null) return null;

        var (sid, user) = ProcessQuery.User(pid);
        var parent = tree.Resolve(record.ParentPid, record.StartTime);

        var evt = new ProcEvent
        {
            Pid = record.Pid,
            ParentPid = record.ParentPid,
            StartTime = record.StartTime,
            ImageName = record.ImageName,
            ImagePath = record.ImagePath,
            CommandLine = record.CommandLine,
            WorkingDirectory = ProcessQuery.WorkingDirectory(pid),
            UserName = user,
            UserSid = sid,
            SessionId = record.SessionId,
            Elevated = ProcessQuery.IsElevated(pid) ?? false,
            ParentImageName = parent?.ImageName ?? "",
            ParentCommandLine = parent?.CommandLine,
            Sources = EvidenceSource.LiveSnapshot,
            Confidence = Confidence.High,
            Signature = SignatureVerifier.Check(record.ImagePath)
        };

        evt.Chain.AddRange(tree.BuildChain(record));
        evt.Window = WindowFinder.ConsoleVisibility(pid, other => tree.Resolve(other)?.ParentPid);

        var decoded = CommandLineDecoder.Decode(evt.ImageName, evt.CommandLine);
        evt.DecodedCommand = decoded.Payload;

        new AttributionEngine(new AutostartIndex()).Attribute(evt);
        RiskScorer.Score(evt, decoded.Traits);
        return evt;
    }
}
