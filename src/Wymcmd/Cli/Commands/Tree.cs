using Wymcmd.Core.Localization;
using Wymcmd.Core.Tree;

namespace Wymcmd.Cli.Commands;

public static class Tree
{
    public static int Run(CliOptions options)
    {
        var tree = new ProcessTree();
        tree.Seed();

        var positional = options.Positional();
        var live = tree.LiveRecords();

        if (positional.Length > 0 && int.TryParse(positional[0], out var pid))
        {
            var root = tree.Resolve(pid);
            if (root is null)
            {
                ConsoleHost.Dim(Loc.T("cli.error.not_found"));
                return CommandRouter.ExitNotFound;
            }

            foreach (var link in tree.BuildChain(root).Reverse())
                ConsoleHost.Line(ConsoleHost.Color($"{link.ImageName} ({link.Pid})", 90));

            Print(root, live, 0);
            return CommandRouter.ExitOk;
        }

        var rootPids = live
            .Where(record => live.All(other => other.Pid != record.ParentPid))
            .OrderBy(record => record.ImageName);

        foreach (var record in rootPids) Print(record, live, 0);
        return CommandRouter.ExitOk;
    }

    private static void Print(ProcRecord record, IReadOnlyList<ProcRecord> live, int depth)
    {
        var indent = new string(' ', depth * 2);
        var name = depth == 0 ? ConsoleHost.Color(record.ImageName, 97) : record.ImageName;
        ConsoleHost.Line($"{indent}{name} ({record.Pid})");

        if (depth > 12) return;

        foreach (var child in live.Where(other => other.ParentPid == record.Pid && other.Pid != record.Pid)
                                  .OrderBy(other => other.StartTime))
            Print(child, live, depth + 1);
    }
}
