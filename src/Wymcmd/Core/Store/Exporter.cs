using System.Text;
using System.Text.Json;
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Store;

/// <summary>
/// Machine-readable output, shared by the command line and the window. Keys stay in English
/// whatever the interface language is, so a script written today keeps working tomorrow.
/// </summary>
public static class Exporter
{
    public static string Json(ProcEvent evt) => JsonSerializer.Serialize(new
    {
        pid = evt.Pid,
        parentPid = evt.ParentPid,
        startTime = evt.StartTime,
        exitTime = evt.ExitTime,
        exitCode = evt.ExitCode,
        image = evt.ImageName,
        imagePath = evt.ImagePath,
        commandLine = evt.CommandLine,
        decoded = evt.DecodedCommand,
        workingDirectory = evt.WorkingDirectory,
        user = evt.UserName,
        sessionId = evt.SessionId,
        elevated = evt.Elevated,
        window = evt.Window.ToString(),
        signature = new { status = evt.Signature.Status.ToString(), publisher = evt.Signature.Publisher },
        source = evt.Source is null ? null : new
        {
            kind = evt.Source.Kind.ToString(),
            name = evt.Source.Name,
            location = evt.Source.Location
        },
        confidence = evt.Confidence.ToString(),
        evidence = evt.Sources.ToString(),
        risk = evt.Risk,
        riskFactors = evt.RiskFactors.Select(factor => new { key = factor.Key, weight = factor.Weight, detail = factor.Detail }),
        attack = Why.AttackMap.For(evt).Select(technique => new { id = technique.Id, name = technique.Name, url = technique.Url }),
        chain = evt.Chain.Select(link => new { pid = link.Pid, image = link.ImageName, commandLine = link.CommandLine })
    });

    public static string Csv(IEnumerable<ProcEvent> events)
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
    {
        // A spreadsheet treats a cell that starts with one of these as a formula and runs it.
        // Command lines and task names are written by whoever started the process, so a leading
        // quote turns them back into text. The jsonl export is the one to feed to a program.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
