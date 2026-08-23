using System.Text;
using System.Text.RegularExpressions;

namespace Wymcmd.Core.Why;

[Flags]
public enum CommandTraits
{
    None = 0,
    Encoded = 1 << 0,
    HiddenWindow = 1 << 1,
    BypassesPolicy = 1 << 2,
    DownloadsContent = 1 << 3,
    RunsScriptFile = 1 << 4,
    LivingOffTheLand = 1 << 5,
    SelfDeleting = 1 << 6
}

public sealed record DecodedCommand(string? Payload, CommandTraits Traits, string? ScriptPath);

/// <summary>
/// Turns "powershell -nop -w hidden -enc SQBFAFgA..." into the script a human can read,
/// and flags the shapes that matter when scoring risk.
/// </summary>
public static partial class CommandLineDecoder
{
    private static readonly string[] LolBins =
    [
        "mshta.exe", "rundll32.exe", "regsvr32.exe", "certutil.exe", "bitsadmin.exe",
        "wmic.exe", "installutil.exe", "msbuild.exe", "cscript.exe", "wscript.exe",
        "forfiles.exe", "pcalua.exe", "conhost.exe", "curl.exe"
    ];

    public static DecodedCommand Decode(string imageName, string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return new DecodedCommand(null, CommandTraits.None, null);

        var traits = CommandTraits.None;
        string? payload = null;

        if (EncodedArgument().Match(commandLine) is { Success: true } encoded)
        {
            traits |= CommandTraits.Encoded;
            payload = TryDecodeBase64(encoded.Groups["data"].Value);
        }

        if (HiddenWindowArgument().IsMatch(commandLine)) traits |= CommandTraits.HiddenWindow;
        if (PolicyBypassArgument().IsMatch(commandLine)) traits |= CommandTraits.BypassesPolicy;

        var haystack = payload is null ? commandLine : commandLine + " " + payload;
        if (DownloadArgument().IsMatch(haystack)) traits |= CommandTraits.DownloadsContent;
        if (SelfDeleteArgument().IsMatch(haystack)) traits |= CommandTraits.SelfDeleting;

        var scriptPath = ScriptArgument().Match(haystack) is { Success: true } script
            ? script.Groups["path"].Value.Trim('"')
            : null;
        if (scriptPath is not null) traits |= CommandTraits.RunsScriptFile;

        if (LolBins.Contains(imageName, StringComparer.OrdinalIgnoreCase) && HasArguments(commandLine))
            traits |= CommandTraits.LivingOffTheLand;

        // cmd /c "<inner>" - show the inner command, that is what actually ran.
        if (payload is null && CmdWrapper().Match(commandLine) is { Success: true } wrapper)
            payload = wrapper.Groups["inner"].Value.Trim().Trim('"');

        return new DecodedCommand(payload, traits, scriptPath);
    }

    public static string[] SplitArguments(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in commandLine)
        {
            switch (character)
            {
                case '"':
                    quoted = !quoted;
                    break;
                case ' ' when !quoted:
                    if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return [.. parts];
    }

    /// <summary>The executable part of a command line, without arguments or quotes.</summary>
    public static string ImageFromCommandLine(string commandLine)
    {
        var arguments = SplitArguments(commandLine);
        return arguments.Length == 0 ? "" : arguments[0];
    }

    private static bool HasArguments(string commandLine) => SplitArguments(commandLine).Length > 1;

    private static string? TryDecodeBase64(string data)
    {
        try
        {
            var bytes = Convert.FromBase64String(data.Trim());
            // PowerShell -EncodedCommand is UTF-16LE; anything else is likely plain UTF-8.
            var text = bytes.Length > 1 && bytes[1] == 0
                ? Encoding.Unicode.GetString(bytes)
                : Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"-(?:e|en|enc|encod|encode|encoded|encodedcommand)\s+(?<data>[A-Za-z0-9+/=]{16,})", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedArgument();

    [GeneratedRegex(@"-(?:w|windowstyle)\s+h(?:idden)?\b|\bCREATE_NO_WINDOW\b", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenWindowArgument();

    [GeneratedRegex(@"-(?:ep|exec|executionpolicy)\s+(?:bypass|unrestricted)\b|-nop\b|-noprofile\b", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyBypassArgument();

    [GeneratedRegex(@"\b(?:iwr|irm|invoke-webrequest|invoke-restmethod|downloadstring|downloadfile|curl|wget|bitsadmin|certutil\s+-urlcache|start-bitstransfer)\b|https?://", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadArgument();

    [GeneratedRegex(@"\bdel\s+(?:/[fq]\s+)*""?%~f0|remove-item\s+.*\$MyInvocation", RegexOptions.IgnoreCase)]
    private static partial Regex SelfDeleteArgument();

    [GeneratedRegex(@"(?:-file\s+|/script:|\s)(?<path>""[^""]+\.(?:ps1|bat|cmd|vbs|js|wsf|py)""|\S+\.(?:ps1|bat|cmd|vbs|js|wsf|py))", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptArgument();

    [GeneratedRegex(@"^\s*""?[^""]*cmd(?:\.exe)?""?\s+/[ck]\s+(?<inner>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CmdWrapper();
}
