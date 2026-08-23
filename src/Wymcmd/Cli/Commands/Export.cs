using System.Text;
using Wymcmd.Core.Forensic;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Store;

namespace Wymcmd.Cli.Commands;

public static class Export
{
    public static int Run(CliOptions options)
    {
        var since = Commands.List.ParseSpan(options.Value("--since") ?? "24h");
        var format = (options.Value("--format") ?? "jsonl").ToLowerInvariant();
        var from = DateTime.Now - since;

        using var store = new EventStore();

        var events = options.Has("--forensic")
            ? new ForensicHarvester(store).Window(from, DateTime.Now, 20000)
            : store.Query(new EventFilter { From = from, Limit = 20000 });

        if (options.Has("--console")) events = events.Where(e => e.IsConsoleHost).ToList();

        if (events.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("cli.error.not_found"));
            return CommandRouter.ExitNotFound;
        }

        var content = format switch
        {
            "csv" => Csv(events),
            "report" or "md" or "markdown" => Report(events),
            _ => string.Join(Environment.NewLine, events.Select(EventFormatter.Json))
        };

        var target = options.Value("--out") ?? DefaultPath(format);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.WriteAllText(target, content, new UTF8Encoding(false));

        ConsoleHost.Good(Loc.T("export.written", events.Count, Path.GetFullPath(target)));
        return CommandRouter.ExitOk;
    }

    private static string DefaultPath(string format)
    {
        var extension = format switch
        {
            "csv" => "csv",
            "report" or "md" or "markdown" => "md",
            _ => "jsonl"
        };
        return Path.Combine(AppPaths.ExportDirectory, $"wymcmd-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
    }

    private static string Csv(IEnumerable<ProcEvent> events)
    {
        var text = new StringBuilder();
        text.AppendLine("start_time,pid,parent_pid,image,command_line,window,signature,publisher,source_kind,source_name,risk,confidence,evidence");

        foreach (var evt in events)
        {
            text.AppendLine(string.Join(",", new[]
            {
                evt.StartTime.ToString("o"),
                evt.Pid.ToString(),
                evt.ParentPid.ToString(),
                evt.ImagePath.Length > 0 ? evt.ImagePath : evt.ImageName,
                evt.CommandLine,
                evt.Window.ToString(),
                evt.Signature.Status.ToString(),
                evt.Signature.Publisher ?? "",
                (evt.Source?.Kind ?? LaunchSourceKind.Unknown).ToString(),
                evt.Source?.Name ?? "",
                evt.Risk.ToString(),
                evt.Confidence.ToString(),
                evt.Sources.ToString()
            }.Select(Quote)));
        }

        return text.ToString();
    }

    private static string Quote(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static string Report(IReadOnlyList<ProcEvent> events)
        => string.Join(Environment.NewLine + Environment.NewLine,
            events.OrderByDescending(e => e.Risk).Take(50).Select(ReportBuilder.Markdown));
}
