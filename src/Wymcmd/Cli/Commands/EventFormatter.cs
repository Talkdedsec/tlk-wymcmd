using System.Text.Json;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Why;

namespace Wymcmd.Cli.Commands;

/// <summary>
/// Human output goes through Loc; --json output never does - machine keys stay English so
/// scripts do not break when someone switches the interface language.
/// </summary>
public static class EventFormatter
{
    public static string OneLine(ProcEvent evt)
    {
        var time = evt.StartTime.ToString("HH:mm:ss", Loc.Culture);
        var window = evt.Window switch
        {
            WindowVisibility.Hidden => "H",
            WindowVisibility.Visible => "V",
            WindowVisibility.Embedded => "E",
            _ => "?"
        };
        var risk = ConsoleHost.Risk(evt.Risk, $"{evt.Risk,3}");
        var verdict = AttributionEngine.Verdict(evt);

        return $"{ConsoleHost.Color(time, 90)}  {evt.ImageName,-18} {window}  {risk}  {verdict}";
    }

    public static void Detail(ProcEvent evt)
    {
        ConsoleHost.Strong($"{evt.ImageName}  (pid {evt.Pid})");
        ConsoleHost.Line(AttributionEngine.Verdict(evt));
        ConsoleHost.Line();

        Field("why.when", $"{evt.StartTime.ToString("F", Loc.Culture)}  ({Loc.Ago(evt.StartTime)})");
        if (evt.Lifetime is { } life) Field("why.lifetime", Loc.Duration(life));
        if (evt.ExitCode is { } code) Field("why.exit_code", code.ToString());

        Field("why.image", evt.ImagePath.Length > 0 ? evt.ImagePath : evt.ImageName);
        Field("why.command", evt.CommandLine);
        if (evt.DecodedCommand is { Length: > 0 }) Field("why.decoded", evt.DecodedCommand);
        if (evt.WorkingDirectory is { Length: > 0 }) Field("why.workdir", evt.WorkingDirectory);

        Field("why.signature", SignatureText(evt));
        Field("why.window", Loc.T("window." + evt.Window.ToString().ToLowerInvariant()));
        Field("why.user", $"{evt.UserName ?? "?"}  (session {evt.SessionId}{(evt.Elevated ? ", elevated" : "")})");
        Field("why.source", SourceText(evt));
        Field("why.confidence", Loc.T("confidence." + evt.Confidence.ToString().ToLowerInvariant()));
        Field("why.evidence", EvidenceText(evt));

        ConsoleHost.Line();
        ConsoleHost.Strong(Loc.T("why.chain"));
        var indent = 0;
        foreach (var link in evt.Chain.AsEnumerable().Reverse())
        {
            var name = link.ImageName.Length > 0 ? link.ImageName : Loc.T("chain.exited");
            ConsoleHost.Line($"{new string(' ', indent * 2)}{name} ({link.Pid})");
            indent++;
        }
        ConsoleHost.Line($"{new string(' ', indent * 2)}{ConsoleHost.Color(evt.ImageName, 97)} ({evt.Pid})");

        if (evt.RiskFactors.Count > 0)
        {
            ConsoleHost.Line();
            ConsoleHost.Line($"{Loc.T("why.risk")}: {ConsoleHost.Risk(evt.Risk, evt.Risk + "/100")}");
            foreach (var factor in evt.RiskFactors.OrderByDescending(f => f.Weight))
            {
                var detail = factor.Detail is { Length: > 0 } ? $"  ({factor.Detail})" : "";
                ConsoleHost.Line($"  +{factor.Weight,-3} {Loc.T("risk." + factor.Key)}{detail}");
            }
        }
    }

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
        riskFactors = evt.RiskFactors.Select(f => new { key = f.Key, weight = f.Weight, detail = f.Detail }),
        chain = evt.Chain.Select(link => new { pid = link.Pid, image = link.ImageName, commandLine = link.CommandLine })
    });

    private static string SignatureText(ProcEvent evt) => evt.Signature.Status switch
    {
        SignatureStatus.Valid => Loc.T("signature.valid", evt.Signature.Publisher ?? "?"),
        SignatureStatus.Unsigned => Loc.T("signature.unsigned"),
        SignatureStatus.Invalid => Loc.T("signature.invalid"),
        SignatureStatus.Expired => Loc.T("signature.expired"),
        _ => Loc.T("signature.unknown")
    };

    private static string SourceText(ProcEvent evt)
    {
        if (evt.Source is null) return Loc.T("source.unknown");

        var kind = Loc.T("source." + evt.Source.Kind.ToString().ToLowerInvariant());
        var name = evt.Source.Name is { Length: > 0 } ? $": {evt.Source.Name}" : "";
        var location = evt.Source.Location is { Length: > 0 } ? $"  [{evt.Source.Location}]" : "";
        return kind + name + location;
    }

    private static string EvidenceText(ProcEvent evt)
        => evt.Sources == EvidenceSource.None ? "-" : evt.Sources.ToString();

    private static void Field(string key, string value)
    {
        var label = Loc.T(key);
        ConsoleHost.Line($"{ConsoleHost.Color(label.PadRight(14), 90)} {value}");
    }
}
