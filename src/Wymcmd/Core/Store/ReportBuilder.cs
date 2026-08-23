using System.Text;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Why;

namespace Wymcmd.Core.Store;

/// <summary>A single launch written out as something you can paste into a ticket or a chat.</summary>
public static class ReportBuilder
{
    public static string Markdown(ProcEvent evt)
    {
        var text = new StringBuilder();

        text.AppendLine($"# {evt.ImageName} — {Loc.T("why.when")} {evt.StartTime.ToString("F", Loc.Culture)}");
        text.AppendLine();
        text.AppendLine($"**{AttributionEngine.Verdict(evt)}**");
        text.AppendLine();

        text.AppendLine($"| | |");
        text.AppendLine($"|---|---|");
        Row(text, Loc.T("why.image"), evt.ImagePath);
        Row(text, Loc.T("why.command"), Code(evt.CommandLine));
        if (evt.DecodedCommand is { Length: > 0 }) Row(text, Loc.T("why.decoded"), Code(evt.DecodedCommand));
        if (evt.WorkingDirectory is { Length: > 0 }) Row(text, Loc.T("why.workdir"), evt.WorkingDirectory);
        Row(text, Loc.T("why.source"), SourceText(evt));
        Row(text, Loc.T("why.signature"), SignatureText(evt));
        Row(text, Loc.T("why.window"), Loc.T("window." + evt.Window.ToString().ToLowerInvariant()));
        Row(text, Loc.T("why.user"), $"{evt.UserName ?? "?"} (session {evt.SessionId})");
        if (evt.Lifetime is { } life) Row(text, Loc.T("why.lifetime"), Loc.Duration(life));
        if (evt.ExitCode is { } code) Row(text, Loc.T("why.exit_code"), code.ToString());
        Row(text, Loc.T("why.confidence"), Loc.T("confidence." + evt.Confidence.ToString().ToLowerInvariant()));
        Row(text, Loc.T("why.evidence"), evt.Sources.ToString());
        Row(text, "pid", evt.Pid.ToString());
        text.AppendLine();

        text.AppendLine($"## {Loc.T("why.chain")}");
        text.AppendLine();
        var depth = 0;
        foreach (var link in evt.Chain.AsEnumerable().Reverse())
        {
            var name = link.ImageName.Length > 0 ? link.ImageName : Loc.T("chain.exited");
            text.AppendLine($"{new string(' ', depth * 2)}- {name} ({link.Pid})");
            depth++;
        }
        text.AppendLine($"{new string(' ', depth * 2)}- **{evt.ImageName}** ({evt.Pid})");
        text.AppendLine();

        if (evt.RiskFactors.Count > 0)
        {
            text.AppendLine($"## {Loc.T("why.risk")}: {evt.Risk}/100");
            text.AppendLine();
            foreach (var factor in evt.RiskFactors.OrderByDescending(f => f.Weight))
            {
                var detail = factor.Detail is { Length: > 0 } ? $" ({factor.Detail})" : "";
                text.AppendLine($"- +{factor.Weight} {Loc.T("risk." + factor.Key)}{detail}");
            }
            text.AppendLine();
        }

        text.AppendLine("---");
        text.AppendLine($"_{Loc.T("app.name")} · {Loc.T("app.tagline")}_");
        return text.ToString();
    }

    private static void Row(StringBuilder text, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        text.AppendLine($"| {label} | {value.Replace("|", "\\|")} |");
    }

    private static string Code(string value) => "`" + value.Replace("`", "'") + "`";

    private static string SourceText(ProcEvent evt)
    {
        if (evt.Source is null) return Loc.T("source.unknown");
        var kind = Loc.T("source." + evt.Source.Kind.ToString().ToLowerInvariant());
        return evt.Source.Name is { Length: > 0 } ? $"{kind}: {evt.Source.Name}" : kind;
    }

    private static string SignatureText(ProcEvent evt) => evt.Signature.Status switch
    {
        SignatureStatus.Valid => Loc.T("signature.valid", evt.Signature.Publisher ?? "?"),
        SignatureStatus.Unsigned => Loc.T("signature.unsigned"),
        SignatureStatus.Invalid => Loc.T("signature.invalid"),
        SignatureStatus.Expired => Loc.T("signature.expired"),
        _ => Loc.T("signature.unknown")
    };
}
