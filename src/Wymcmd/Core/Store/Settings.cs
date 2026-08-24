using System.Text.Json;
using System.Text.Json.Serialization;
using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Store;

/// <summary>
/// The few knobs worth keeping between runs. Written as plain JSON next to the database so it
/// can be read and edited without the tool.
/// </summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static Settings? _current;

    /// <summary>How long recorded launches are kept. Zero means keep everything.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Hard ceiling for the database; the oldest events go first when it is reached.</summary>
    public int MaxDatabaseMb { get; set; } = 256;

    /// <summary>Interface language, or null to follow Windows.</summary>
    public string? Language { get; set; }

    public bool Notifications { get; set; } = true;

    public int BlackBoxSizeMb { get; set; } = 64;

    public static Settings Current => _current ??= Load();

    public static Settings Load()
    {
        if (!File.Exists(AppPaths.Settings)) return new Settings();

        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.Settings), Format) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Warn("settings unreadable, using defaults: " + ex.Message);
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Format));
            _current = this;
        }
        catch (Exception ex)
        {
            Log.Warn("settings could not be saved: " + ex.Message);
        }
    }
}
