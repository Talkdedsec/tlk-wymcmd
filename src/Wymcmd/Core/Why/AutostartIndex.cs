using System.Xml.Linq;
using Microsoft.Win32;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Why;

public sealed record AutostartEntry(
    LaunchSourceKind Kind,
    string Name,
    string Location,
    string Command,
    string? TargetImage);

/// <summary>
/// Everything on this machine that can launch a program without a human clicking it.
/// Built once, refreshed on a timer, and matched against a process command line to answer
/// "why did this start" for launches that have no live parent left to inspect.
/// </summary>
public sealed class AutostartIndex
{
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(2);

    private List<AutostartEntry> _entries = [];
    private DateTime _builtAt = DateTime.MinValue;
    private readonly Lock _sync = new();

    public IReadOnlyList<AutostartEntry> Entries
    {
        get { lock (_sync) return _entries; }
    }

    public DateTime BuiltAt => _builtAt;

    /// <summary>Replaces the index with a known set - used by tests and by offline analysis.</summary>
    public void Seed(IEnumerable<AutostartEntry> entries)
    {
        lock (_sync)
        {
            _entries = entries.ToList();
            _builtAt = DateTime.Now;
        }
    }

    public void EnsureFresh(TimeSpan? maxAge = null)
    {
        if (DateTime.Now - _builtAt < (maxAge ?? DefaultMaxAge)) return;
        Rebuild();
    }

    public void Rebuild()
    {
        var collected = new List<AutostartEntry>(256);

        Safely(() => collected.AddRange(RunKeys()), "run keys");
        Safely(() => collected.AddRange(StartupFolders()), "startup folders");
        Safely(() => collected.AddRange(ScheduledTasks()), "scheduled tasks");
        Safely(() => collected.AddRange(Services()), "services");
        Safely(() => collected.AddRange(ExecutionOptions()), "image file execution options");
        Safely(() => collected.AddRange(WmiConsumers()), "wmi consumers");

        lock (_sync)
        {
            _entries = collected;
            _builtAt = DateTime.Now;
        }
        Log.Debug($"autostart index rebuilt: {collected.Count} entries");
    }

    /// <summary>Best matching entry for a process, or null when nothing in the index explains it.</summary>
    public AutostartEntry? Match(string imagePath, string commandLine)
    {
        var image = Normalize(Path.GetFileName(imagePath));
        var fullImage = Normalize(imagePath);
        if (image.Length == 0 && commandLine.Length == 0) return null;

        AutostartEntry? best = null;
        var bestScore = 0;

        foreach (var entry in Entries)
        {
            var score = ScoreMatch(entry, image, fullImage, commandLine);
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }

        return bestScore >= 2 ? best : null;
    }

    private static int ScoreMatch(AutostartEntry entry, string image, string fullImage, string commandLine)
    {
        var target = Normalize(entry.TargetImage ?? "");
        var command = Normalize(entry.Command);
        var score = 0;

        if (target.Length > 0)
        {
            if (target == fullImage) score += 4;
            else if (Normalize(Path.GetFileName(entry.TargetImage ?? "")) == image) score += 2;
        }

        if (command.Length > 0 && commandLine.Length > 0)
        {
            var normalizedCommandLine = Normalize(commandLine);
            if (normalizedCommandLine.Contains(command)) score += 3;
            else if (command.Contains(normalizedCommandLine)) score += 2;
        }

        return score;
    }

    private static string Normalize(string value)
        => value.Trim().Trim('"').Replace('/', '\\').ToLowerInvariant();

    private static IEnumerable<AutostartEntry> RunKeys()
    {
        (RegistryKey Root, string Path, string Label)[] locations =
        [
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM\\...\\Run"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM\\...\\RunOnce"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM\\...\\WOW6432Node\\Run"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU\\...\\Run"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU\\...\\RunOnce")
        ];

        foreach (var (root, path, label) in locations)
        {
            using var key = root.OpenSubKey(path);
            if (key is null) continue;

            foreach (var valueName in key.GetValueNames())
            {
                var command = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(command)) continue;

                yield return new AutostartEntry(
                    LaunchSourceKind.RunKey,
                    valueName,
                    label,
                    command,
                    CommandLineDecoder.ImageFromCommandLine(command));
            }
        }
    }

    private static IEnumerable<AutostartEntry> StartupFolders()
    {
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        ];

        foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ShortcutTarget(file)
                    : file;

                yield return new AutostartEntry(
                    LaunchSourceKind.StartupFolder,
                    Path.GetFileNameWithoutExtension(file),
                    folder,
                    target ?? file,
                    target);
            }
        }
    }

    private static string? ShortcutTarget(string linkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            dynamic shortcut = shell.CreateShortcut(linkPath);
            string target = shortcut.TargetPath;
            string arguments = shortcut.Arguments;
            return string.IsNullOrWhiteSpace(arguments) ? target : $"{target} {arguments}";
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<AutostartEntry> ScheduledTasks()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            AutostartEntry? entry = null;
            try
            {
                var document = XDocument.Load(file);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var exec = document.Root?.Element(ns + "Actions")?.Element(ns + "Exec");
                var command = exec?.Element(ns + "Command")?.Value;
                if (string.IsNullOrWhiteSpace(command)) continue;

                var arguments = exec?.Element(ns + "Arguments")?.Value ?? "";
                var taskPath = "\\" + Path.GetRelativePath(root, file).Replace('/', '\\');

                entry = new AutostartEntry(
                    LaunchSourceKind.ScheduledTask,
                    taskPath,
                    "Task Scheduler",
                    string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}",
                    Environment.ExpandEnvironmentVariables(command.Trim('"')));
            }
            catch
            {
                // Unreadable or non-XML task file; skip it rather than failing the whole scan.
            }

            if (entry is not null) yield return entry;
        }
    }

    private static IEnumerable<AutostartEntry> Services()
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) yield break;

        foreach (var name in services.GetSubKeyNames())
        {
            AutostartEntry? entry = null;
            try
            {
                using var service = services.OpenSubKey(name);
                var imagePath = service?.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(imagePath)) continue;

                var expanded = Environment.ExpandEnvironmentVariables(imagePath);
                entry = new AutostartEntry(
                    LaunchSourceKind.Service,
                    service?.GetValue("DisplayName") as string ?? name,
                    @"HKLM\SYSTEM\CurrentControlSet\Services\" + name,
                    expanded,
                    CommandLineDecoder.ImageFromCommandLine(expanded));
            }
            catch
            {
                // Locked service key; ignore.
            }

            if (entry is not null) yield return entry;
        }
    }

    private static IEnumerable<AutostartEntry> ExecutionOptions()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        using var root = Registry.LocalMachine.OpenSubKey(path);
        if (root is null) yield break;

        foreach (var name in root.GetSubKeyNames())
        {
            AutostartEntry? entry = null;
            try
            {
                using var key = root.OpenSubKey(name);
                var debugger = key?.GetValue("Debugger") as string;
                if (string.IsNullOrWhiteSpace(debugger)) continue;

                entry = new AutostartEntry(
                    LaunchSourceKind.ImageFileExecutionOptions,
                    name,
                    @"HKLM\" + path + "\\" + name,
                    debugger,
                    CommandLineDecoder.ImageFromCommandLine(debugger));
            }
            catch
            {
                // ignore
            }

            if (entry is not null) yield return entry;
        }
    }

    private static IEnumerable<AutostartEntry> WmiConsumers()
    {
        var results = new List<AutostartEntry>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                new System.Management.ManagementScope(@"\\.\root\subscription"),
                new System.Management.ObjectQuery("SELECT * FROM CommandLineEventConsumer"));

            foreach (var item in searcher.Get())
            {
                using var consumer = item;
                var command = consumer["CommandLineTemplate"] as string ?? consumer["ExecutablePath"] as string;
                if (string.IsNullOrWhiteSpace(command)) continue;

                results.Add(new AutostartEntry(
                    LaunchSourceKind.WmiSubscription,
                    consumer["Name"] as string ?? "CommandLineEventConsumer",
                    @"root\subscription",
                    command,
                    CommandLineDecoder.ImageFromCommandLine(command)));
            }
        }
        catch
        {
            // WMI subscription namespace usually needs admin; absence is not an error.
        }

        return results;
    }

    private static void Safely(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Debug($"autostart scan skipped {what}: {ex.Message}");
        }
    }
}
