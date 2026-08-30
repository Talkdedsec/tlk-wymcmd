using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;

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
                if (!Regex.IsMatch(evt.CommandLine, CommandLine, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)))
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

    private static bool Glob(string pattern, string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
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

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
    }
}
