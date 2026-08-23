using System.Security;
using System.Text;
using Microsoft.Win32;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Forensic;

public sealed record ExecutionTrace(
    string ImageName,
    string? ImagePath,
    DateTime? LastRun,
    int? RunCount,
    EvidenceSource Source);

/// <summary>
/// Windows keeps several "this program ran" ledgers that survive reboots and outlive the
/// process itself. They cannot say who started something, but they are decisive about whether
/// a binary is a regular on this machine or showed up for the first time an hour ago.
/// </summary>
public static class ExecutionHistory
{
    /// <summary>
    /// Prefetch, read from file metadata only. The .pf write time is when the program last ran;
    /// the format itself is deliberately not parsed, because half-decoded fields would be worse
    /// than an honest approximation.
    /// </summary>
    public static IReadOnlyList<ExecutionTrace> Prefetch()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        if (!Directory.Exists(folder)) return [];

        try
        {
            return Directory.EnumerateFiles(folder, "*.pf")
                .Select(file =>
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var dash = name.LastIndexOf('-');
                    var image = dash > 0 ? name[..dash] : name;
                    return new ExecutionTrace(image, null, File.GetLastWriteTime(file), null, EvidenceSource.Prefetch);
                })
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Debug("prefetch folder unreadable: " + ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Background Activity Moderator: exact last execution time per user, per binary.
    /// Readable by administrators only, so without rights this simply stays empty.
    /// </summary>
    public static IReadOnlyList<ExecutionTrace> BackgroundActivity()
    {
        string[] roots =
        [
            @"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings",
            @"SYSTEM\CurrentControlSet\Services\bam\UserSettings"
        ];

        foreach (var root in roots)
        {
            var found = ReadBamRoot(root);
            if (found.Count > 0) return found;
        }

        return [];
    }

    private static List<ExecutionTrace> ReadBamRoot(string root)
    {
        var results = new List<ExecutionTrace>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) return results;

            foreach (var sid in key.GetSubKeyNames())
            {
                using var user = key.OpenSubKey(sid);
                if (user is null) continue;

                foreach (var valueName in user.GetValueNames())
                {
                    if (!valueName.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (user.GetValue(valueName) is not byte[] data || data.Length < 8) continue;

                    DateTime stamp;
                    try
                    {
                        stamp = DateTime.FromFileTime(BitConverter.ToInt64(data, 0));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        continue;
                    }

                    var path = DevicePathToDrive(valueName);
                    results.Add(new ExecutionTrace(Path.GetFileName(path), path, stamp, null, EvidenceSource.Bam));
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            Log.Debug("bam ledger needs administrator rights");
        }

        return results;
    }

    /// <summary>UserAssist: what the person actually launched from the shell, with run counts.</summary>
    public static IReadOnlyList<ExecutionTrace> UserLaunched()
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
        var results = new List<ExecutionTrace>();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(root);
            if (key is null) return results;

            foreach (var guid in key.GetSubKeyNames())
            {
                using var counts = key.OpenSubKey(guid + @"\Count");
                if (counts is null) continue;

                foreach (var encoded in counts.GetValueNames())
                {
                    var path = Rot13(encoded);
                    if (!path.Contains('\\')) continue;

                    var (runCount, lastRun) = ReadUserAssistValue(counts.GetValue(encoded) as byte[]);
                    results.Add(new ExecutionTrace(Path.GetFileName(path), path, lastRun, runCount, EvidenceSource.UserAssist));
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            Log.Debug("userassist ledger unreadable: " + ex.Message);
        }

        return results;
    }

    private static (int? RunCount, DateTime? LastRun) ReadUserAssistValue(byte[]? data)
    {
        if (data is null || data.Length < 68) return (null, null);

        var runCount = BitConverter.ToInt32(data, 4);
        var raw = BitConverter.ToInt64(data, 60);
        if (raw <= 0) return (runCount, null);

        try
        {
            return (runCount, DateTime.FromFileTime(raw));
        }
        catch (ArgumentOutOfRangeException)
        {
            return (runCount, null);
        }
    }

    /// <summary>Everything the ledgers know about one binary, newest first.</summary>
    public static IReadOnlyList<ExecutionTrace> For(string imageName, string? imagePath = null)
    {
        var traces = new List<ExecutionTrace>();
        traces.AddRange(Prefetch());
        traces.AddRange(BackgroundActivity());
        traces.AddRange(UserLaunched());

        return traces
            .Where(trace => trace.ImageName.Equals(imageName, StringComparison.OrdinalIgnoreCase) ||
                            (imagePath is not null && trace.ImagePath is not null &&
                             trace.ImagePath.Equals(imagePath, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(trace => trace.LastRun ?? DateTime.MinValue)
            .ToList();
    }

    private static string DevicePathToDrive(string devicePath)
    {
        // \Device\HarddiskVolume4\Windows\System32\cmd.exe -> C:\Windows\System32\cmd.exe
        var parts = devicePath.Split('\\', 4);
        return parts.Length == 4
            ? Path.Combine(Environment.SystemDirectory[..3], parts[3])
            : devicePath;
    }

    private static string Rot13(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            text.Append(character switch
            {
                >= 'a' and <= 'z' => (char)('a' + (character - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (character - 'A' + 13) % 26),
                _ => character
            });
        }
        return Environment.ExpandEnvironmentVariables(text.ToString());
    }
}
