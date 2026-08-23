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
        return CommandRouter.ExitOk;
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
