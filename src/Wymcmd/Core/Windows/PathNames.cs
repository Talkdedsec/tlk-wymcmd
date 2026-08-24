namespace Wymcmd.Core.Windows;

/// <summary>
/// The kernel spells paths its own way. ETW hands us \Device\HarddiskVolume4\Windows\... and
/// \SystemRoot\..., and a path in that shape matches nothing a person or an API expects.
/// </summary>
public static class PathNames
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        path = path.Trim();

        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[12..]);

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            path = path[4..];

        if (!path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return path;

        var parts = path.Split('\\', 4);
        return parts.Length == 4
            ? Path.Combine(Environment.SystemDirectory[..3], parts[3])
            : path;
    }

    public static string FileName(string? path)
    {
        var normalized = Normalize(path);
        return normalized.Length == 0 ? "" : Path.GetFileName(normalized);
    }
}
