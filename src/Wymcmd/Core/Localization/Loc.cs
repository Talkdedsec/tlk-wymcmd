using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Wymcmd.Core.Localization;

/// <summary>
/// Every user-facing string lives in Assets/i18n/*.json. English is the source language,
/// Turkish is a full translation; a missing key falls back to English and is reported by
/// scripts/i18n-check.ps1 rather than silently shipping.
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, string> Fallback = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _active = new(StringComparer.Ordinal);

    public static string Language { get; private set; } = "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public static event Action? Changed;

    public static IReadOnlyList<(string Code, string Name)> Available { get; } =
    [
        ("en", "English"),
        ("tr", "Türkçe")
    ];

    static Loc()
    {
        Fallback = Load("en") ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _active = Fallback;
    }

    public static string DetectSystemLanguage()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Available.Any(a => a.Code == two) ? two : "en";
    }

    public static void Use(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) language = "en";
        language = language.Trim().ToLowerInvariant();
        if (!Available.Any(a => a.Code == language)) language = "en";
        if (language == Language && _active.Count > 0) return;

        _active = language == "en" ? Fallback : Load(language) ?? Fallback;
        Language = language;
        Culture = CultureInfo.GetCultureInfo(language == "tr" ? "tr-TR" : "en-US");
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        if (_active.TryGetValue(key, out var value)) return value;
        if (Fallback.TryGetValue(key, out var english)) return english;
        return key;
    }

    public static string T(string key, params object?[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>"3 hours ago" / "3 saat önce", picking singular or plural from the resource file.</summary>
    public static string Ago(DateTime whenLocal)
    {
        var span = DateTime.Now - whenLocal;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalSeconds < 60) return T("time.ago.seconds", (int)span.TotalSeconds);
        if (span.TotalMinutes < 60) return Plural("time.ago.minute", (int)span.TotalMinutes);
        if (span.TotalHours < 24) return Plural("time.ago.hour", (int)span.TotalHours);
        return Plural("time.ago.day", (int)span.TotalDays);
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalMilliseconds < 1000) return T("time.duration.ms", (int)span.TotalMilliseconds);
        if (span.TotalSeconds < 60) return T("time.duration.sec", Math.Round(span.TotalSeconds, 1));
        if (span.TotalMinutes < 60) return T("time.duration.min", (int)span.TotalMinutes, span.Seconds);
        return T("time.duration.hour", (int)span.TotalHours, span.Minutes);
    }

    private static string Plural(string keyBase, int count)
        => T(count == 1 ? keyBase + ".one" : keyBase + ".many", count);

    private static Dictionary<string, string>? Load(string language)
    {
        var json = ReadFromDisk(language) ?? ReadFromAssembly(language);
        if (json is null) return null;

        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        Flatten(doc.RootElement, "", flat);
        return flat;
    }

    private static string? ReadFromDisk(string language)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "i18n", language + ".json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? ReadFromAssembly(string language)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"i18n.{language}.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object)
                Flatten(property.Value, key, into);
            else
                into[key] = property.Value.GetString() ?? "";
        }
    }
}
