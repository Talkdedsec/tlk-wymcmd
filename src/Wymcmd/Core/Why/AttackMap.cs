using Wymcmd.Core.Model;

namespace Wymcmd.Core.Why;

/// <summary>One ATT&amp;CK technique. The id and the name are proper nouns and stay in English.</summary>
public sealed record AttackTechnique(string Id, string Name)
{
    /// <summary>Sub-techniques live under their parent: T1547.001 is /techniques/T1547/001/.</summary>
    public string Url => "https://attack.mitre.org/techniques/" + Id.Replace('.', '/') + "/";
}

/// <summary>
/// Names what was found in the vocabulary a security reader already has.
///
/// Nothing here is a guess: a technique is only attached when the tool has already established
/// the thing it names - a scheduled task it read out of the task store, an encoded command it
/// decoded. It does not score, accuse or infer intent; it labels the evidence so a launch can be
/// looked up, compared with a detection rule, or pasted into a ticket.
/// </summary>
public static class AttackMap
{
    private static readonly Dictionary<LaunchSourceKind, AttackTechnique> BySource = new()
    {
        [LaunchSourceKind.ScheduledTask] = new("T1053.005", "Scheduled Task"),
        [LaunchSourceKind.RunKey] = new("T1547.001", "Registry Run Keys / Startup Folder"),
        [LaunchSourceKind.StartupFolder] = new("T1547.001", "Registry Run Keys / Startup Folder"),
        [LaunchSourceKind.Service] = new("T1543.003", "Windows Service"),
        [LaunchSourceKind.WmiSubscription] = new("T1546.003", "Windows Management Instrumentation Event Subscription"),
        [LaunchSourceKind.ImageFileExecutionOptions] = new("T1546.012", "Image File Execution Options Injection"),
        [LaunchSourceKind.ActiveSetup] = new("T1547.014", "Active Setup"),
        [LaunchSourceKind.LogonScript] = new("T1037.001", "Logon Script (Windows)"),
        [LaunchSourceKind.OfficeMacro] = new("T1204.002", "Malicious File"),
        [LaunchSourceKind.BrowserOrDownload] = new("T1204.002", "Malicious File"),
        [LaunchSourceKind.RemoteAccess] = new("T1021", "Remote Services")
    };

    private static readonly (CommandTraits Trait, AttackTechnique Technique)[] ByTrait =
    [
        (CommandTraits.Encoded, new("T1027", "Obfuscated Files or Information")),
        (CommandTraits.HiddenWindow, new("T1564.003", "Hidden Window")),
        (CommandTraits.DownloadsContent, new("T1105", "Ingress Tool Transfer")),
        (CommandTraits.LivingOffTheLand, new("T1218", "System Binary Proxy Execution")),
        (CommandTraits.SelfDeleting, new("T1070.004", "File Deletion"))
    ];

    public static IReadOnlyList<AttackTechnique> For(ProcEvent evt)
    {
        var decoded = CommandLineDecoder.Decode(evt.ImageName, evt.CommandLine);
        return For(evt, decoded.Traits);
    }

    /// <summary>The traits are passed in wherever the caller has already decoded the command line.</summary>
    public static IReadOnlyList<AttackTechnique> For(ProcEvent evt, CommandTraits traits)
    {
        var found = new List<AttackTechnique>();

        if (evt.Source is { } origin && BySource.TryGetValue(origin.Kind, out var source)) found.Add(source);

        foreach (var (trait, technique) in ByTrait)
            if (traits.HasFlag(trait)) found.Add(technique);

        // The interpreter itself, named from what actually ran rather than from the command line.
        if (Interpreter(evt.ImageName) is { } shell) found.Add(shell);

        return found.DistinctBy(t => t.Id).ToList();
    }

    private static AttackTechnique? Interpreter(string imageName) => imageName.ToLowerInvariant() switch
    {
        "powershell.exe" or "pwsh.exe" => new AttackTechnique("T1059.001", "PowerShell"),
        "cmd.exe" => new AttackTechnique("T1059.003", "Windows Command Shell"),
        "wscript.exe" or "cscript.exe" => new AttackTechnique("T1059.005", "Visual Basic"),
        "mshta.exe" => new AttackTechnique("T1218.005", "Mshta"),
        "rundll32.exe" => new AttackTechnique("T1218.011", "Rundll32"),
        "regsvr32.exe" => new AttackTechnique("T1218.010", "Regsvr32"),
        "msiexec.exe" => new AttackTechnique("T1218.007", "Msiexec"),
        "python.exe" => new AttackTechnique("T1059.006", "Python"),
        _ => null
    };
}
