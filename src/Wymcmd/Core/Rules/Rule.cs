using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Rules;

public sealed class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;

    /// <summary>Image name glob, e.g. cmd.exe or *.tmp.exe</summary>
    public string? Image { get; set; }

    public string? ImagePath { get; set; }

    /// <summary>Regular expression tested against the full command line.</summary>
    public string? CommandLine { get; set; }

    public string? Parent { get; set; }
    public string? Ancestor { get; set; }
    public string? User { get; set; }
    public string? Signer { get; set; }

    public bool? Unsigned { get; set; }
    public bool? HiddenWindow { get; set; }
    public bool? Elevated { get; set; }
    public bool? InTempPath { get; set; }
    public int? SessionId { get; set; }
    public int MinRisk { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RuleAction Action { get; set; } = RuleAction.Log;

    public string? Note { get; set; }

    [JsonIgnore] public int MatchCount { get; set; }
    [JsonIgnore] public DateTime? LastMatch { get; set; }

    public bool Matches(ProcEvent evt)
    {
        if (!Enabled) return false;

        if (Image is not null && !Glob(Image, evt.ImageName)) return false;
        if (ImagePath is not null && !Glob(ImagePath, evt.ImagePath)) return false;
        if (Parent is not null && !Glob(Parent, evt.Chain.FirstOrDefault()?.ImageName ?? evt.ParentImageName)) return false;
        if (User is not null && !Glob(User, evt.UserName ?? "")) return false;
        if (Signer is not null && !Glob(Signer, evt.Signature.Publisher ?? "")) return false;

        if (Ancestor is not null && !evt.Chain.Any(link => Glob(Ancestor, link.ImageName))) return false;

        if (CommandLine is not null)
        {
            try
            {
                if (!Regex.IsMatch(evt.CommandLine, CommandLine, Insensitive, TimeSpan.FromMilliseconds(50)))
                    return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                Log.Warn($"rule {Id} has an invalid command line pattern");
                return false;
            }
        }

        if (Unsigned == true && evt.Signature.Status != SignatureStatus.Unsigned) return false;
        if (Unsigned == false && evt.Signature.Status == SignatureStatus.Unsigned) return false;
        if (HiddenWindow == true && evt.Window != WindowVisibility.Hidden) return false;
        if (HiddenWindow == false && evt.Window == WindowVisibility.Hidden) return false;
        if (Elevated is { } elevated && evt.Elevated != elevated) return false;
        if (SessionId is { } session && evt.SessionId != session) return false;
        if (MinRisk > 0 && evt.Risk < MinRisk) return false;

        if (InTempPath is { } temp)
        {
            var path = evt.ImagePath.ToLowerInvariant();
            var isTemp = path.Contains("\\temp\\") || path.Contains("\\downloads\\");
            if (isTemp != temp) return false;
        }

        return true;
    }

    // Names and command lines are not prose: on a Turkish system culture-aware folding would
    // stop "INSTALL" from matching "install".
    private const RegexOptions Insensitive = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static bool Glob(string pattern, string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, Insensitive);
    }
}

public sealed class RuleSet
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<Rule> Rules { get; set; } = [];

    /// <summary>A view over Rules, not state - writing it to the file put a second copy of every
    /// enabled rule in there, which then had to be read back and thrown away.</summary>
    [JsonIgnore]
    public IEnumerable<Rule> Ordered => Rules.Where(r => r.Enabled).OrderBy(r => r.Priority);

    /// <summary>First matching rule wins; an allow rule stops everything after it.</summary>
    public Rule? FirstMatch(ProcEvent evt)
    {
        foreach (var rule in Ordered)
        {
            if (!rule.Matches(evt)) continue;
            rule.MatchCount++;
            rule.LastMatch = DateTime.Now;
            return rule;
        }
        return null;
    }

    public static RuleSet Load(string path)
    {
        if (!File.Exists(path)) return new RuleSet();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RuleSet>(json, Format) ?? new RuleSet();
        }
        catch (Exception ex)
        {
            Log.Error("rules file unreadable, starting empty", ex);
            return new RuleSet();
        }
    }

    /// <summary>
    /// What the window and the command line show and edit. Rules from before 0.4.2 sit in the
    /// open folder until an elevated step moves them; they are still shown, so the next save
    /// carries them across instead of dropping them.
    /// </summary>
    public static RuleSet LoadCurrent()
    {
        if (!AppPaths.IsShared) return Load(AppPaths.Rules);

        if (File.Exists(AppPaths.Rules))
        {
            // An elevated "watch --enforce" reads this too. A file in the shared folder that
            // somebody else could have written gets the same treatment as the old open one.
            return FileTrust.IsAdminOnly(AppPaths.Rules) ? Load(AppPaths.Rules) : Disarm(Load(AppPaths.Rules));
        }

        return File.Exists(AppPaths.LegacyRules) ? Disarm(Load(AppPaths.LegacyRules)) : new RuleSet();
    }

    /// <summary>
    /// The old rules file sat where any user could write it, so nobody can say who wrote what is
    /// in it. Its rules are kept, but every one that does something to a process - or shields a
    /// process from the others - is switched off until an administrator turns it on again.
    /// </summary>
    internal static RuleSet Disarm(RuleSet legacy)
    {
        foreach (var rule in legacy.Rules.Where(rule => rule.Action is not (RuleAction.Log or RuleAction.Notify)))
        {
            if (rule.Enabled) rule.Note ??= "from before 0.4.2 - switched off until enabled again";
            rule.Enabled = false;
        }

        return legacy;
    }

    /// <summary>Moves the old rules into the protected folder, disarmed. Elevated callers only.</summary>
    internal static void CarryOverLegacy()
    {
        if (!AppPaths.IsShared || !File.Exists(AppPaths.LegacyRules)) return;

        if (!File.Exists(AppPaths.Rules))
        {
            LoadCurrent().Save(AppPaths.Rules);
            SharedRoot.Seal(AppPaths.Rules);
            Log.Warn("rules from before 0.4.2 were carried over; those that act on a process are switched off until enabled again");
        }

        File.Delete(AppPaths.LegacyRules);
    }

    /// <summary>
    /// What the service enforces. A rules file that a non-administrator can change is ignored:
    /// the service runs as LocalSystem, and whoever writes its rules decides what it kills.
    /// </summary>
    public static RuleSet LoadTrusted(string path)
    {
        if (!File.Exists(path)) return new RuleSet();
        if (FileTrust.IsAdminOnly(path)) return Load(path);

        Log.Error($"{path} can be changed by non-administrators; its rules are ignored");
        return new RuleSet();
    }

    /// <summary>Unlike Load, a file that does not parse is an error here - it is about to replace the real one.</summary>
    public static RuleSet Parse(string json)
        => JsonSerializer.Deserialize<RuleSet>(json, Format) ?? throw new JsonException("empty rule set");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
    }

    /// <summary>
    /// Saves to where rules live on this machine. In the shared folder that takes an
    /// administrator, so an unelevated caller hands the new set to an elevated copy of itself.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The elevation prompt was declined.</exception>
    public void SaveCurrent()
    {
        if (!AppPaths.IsShared)
        {
            Save(AppPaths.Rules);
            return;
        }

        if (Elevation.IsElevated)
        {
            if (!SharedRoot.Ensure()) throw new IOException("the shared data folder could not be prepared");
            Save(AppPaths.Rules);
            SharedRoot.Seal(AppPaths.Rules);
            return;
        }

        var handover = Path.Combine(Path.GetTempPath(), $"wymcmd-rules-{Guid.NewGuid():n}.json");
        try
        {
            File.WriteAllText(handover, JsonSerializer.Serialize(this, Format));

            var code = Elevation.Relaunch("rules", "apply", handover, "--lang", Loc.Language)
                       ?? throw new UnauthorizedAccessException(Loc.T("cli.error.needs_admin"));
            if (code != 0) throw new IOException($"the elevated save failed with exit code {code}");
        }
        finally
        {
            try { File.Delete(handover); } catch (IOException) { /* temp file */ }
        }
    }
}
