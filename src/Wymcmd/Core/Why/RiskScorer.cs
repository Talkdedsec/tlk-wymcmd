using Wymcmd.Core.Model;

namespace Wymcmd.Core.Why;

/// <summary>
/// Turns the collected facts into a 0-100 number plus the reasons behind it. The reasons
/// matter more than the number - they are what the detail pane shows.
/// </summary>
public static class RiskScorer
{
    public const int WarnThreshold = 40;
    public const int AlertThreshold = 70;

    private static readonly string[] SuspiciousFolders =
    [
        "\\appdata\\local\\temp\\", "\\windows\\temp\\", "\\downloads\\", "\\appdata\\roaming\\",
        "\\public\\", "\\programdata\\", "\\recycle"
    ];

    public static void Score(ProcEvent evt, CommandTraits traits)
    {
        evt.RiskFactors.Clear();

        if (evt.Signature.Status == SignatureStatus.Unsigned)
            Add(evt, "unsigned", 30);
        else if (evt.Signature.Status is SignatureStatus.Invalid or SignatureStatus.Expired)
            Add(evt, "unsigned", 20, evt.Signature.Status.ToString());

        if (evt.Window == WindowVisibility.Hidden && evt.IsConsoleHost)
            Add(evt, "hidden_window", 25);

        if (traits.HasFlag(CommandTraits.HiddenWindow))
            Add(evt, "hidden_window", 15);

        var path = evt.ImagePath.ToLowerInvariant();
        if (SuspiciousFolders.Any(folder => path.Contains(folder)))
            Add(evt, "temp_path", 20, Path.GetDirectoryName(evt.ImagePath));

        if (traits.HasFlag(CommandTraits.Encoded))
            Add(evt, "encoded_command", 20);

        if (traits.HasFlag(CommandTraits.DownloadsContent))
            Add(evt, "network_tool", 15);

        if (traits.HasFlag(CommandTraits.LivingOffTheLand))
            Add(evt, "lolbin", 10, evt.ImageName);

        if (evt.Lifetime is { TotalMilliseconds: < 200 })
            Add(evt, "very_short_life", 10);

        if (evt.Elevated)
            Add(evt, "elevated", 10);

        if (evt.Source is null || evt.Source.Kind == LaunchSourceKind.Unknown)
            Add(evt, "unknown_source", 15);

        evt.Risk = Math.Clamp(evt.RiskFactors.Sum(factor => factor.Weight), 0, 100);
    }

    private static void Add(ProcEvent evt, string key, int weight, string? detail = null)
    {
        if (evt.RiskFactors.Any(f => f.Key == key)) return;
        evt.RiskFactors.Add(new RiskFactor(key, weight, detail));
    }
}
