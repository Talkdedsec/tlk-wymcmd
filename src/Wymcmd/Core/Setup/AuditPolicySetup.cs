using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using Microsoft.Win32;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Setup;

/// <summary>
/// Turns on the recording Windows can do for us. Every change is written to a journal first,
/// so uninstall puts back exactly what it found and never disables something the user had on.
/// </summary>
public static class AuditPolicySetup
{
    // Locale independent: the subcategory GUID works on a Turkish install too.
    private const string ProcessCreationSubcategory = "{0CCE922B-69AE-11D9-BED3-505054503030}";

    private const string CommandLineKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit";
    private const string ScriptBlockKey = @"SOFTWARE\Policies\Microsoft\Windows\PowerShell\ScriptBlockLogging";

    private static string JournalPath => Path.Combine(AppPaths.Root, "setup-journal.json");

    public sealed record Journal
    {
        public bool EnabledProcessAudit { get; set; }
        public bool EnabledCommandLine { get; set; }
        public bool EnabledScriptBlock { get; set; }
        public bool EnabledTaskLog { get; set; }
        public DateTime? ChangedAt { get; set; }
    }

    public static Journal ReadJournal()
    {
        if (!File.Exists(JournalPath)) return new Journal();
        try
        {
            return JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath)) ?? new Journal();
        }
        catch (Exception)
        {
            return new Journal();
        }
    }

    private static void WriteJournal(Journal journal)
    {
        journal.ChangedAt = DateTime.Now;
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IReadOnlyList<string> EnableAll()
    {
        SharedRoot.Ensure();

        var journal = ReadJournal();
        var changed = new List<string>();

        if (EnableProcessCreationAudit())
        {
            journal.EnabledProcessAudit = true;
            changed.Add("security_audit");
        }

        if (SetDword(Registry.LocalMachine, CommandLineKey, "ProcessCreationIncludeCmdLine_Enabled", 1))
        {
            journal.EnabledCommandLine = true;
            changed.Add("cmdline_audit");
        }

        if (SetDword(Registry.LocalMachine, ScriptBlockKey, "EnableScriptBlockLogging", 1))
        {
            journal.EnabledScriptBlock = true;
            changed.Add("script_block");
        }

        if (SetEventLog(SourceInspector.TaskLogName, true))
        {
            journal.EnabledTaskLog = true;
            changed.Add("task_log");
        }

        WriteJournal(journal);
        return changed;
    }

    /// <summary>Undoes only what we turned on ourselves.</summary>
    public static IReadOnlyList<string> RevertAll()
    {
        var journal = ReadJournal();
        var reverted = new List<string>();

        if (journal.EnabledProcessAudit && RunAuditPol("/success:disable /failure:disable"))
            reverted.Add("security_audit");

        if (journal.EnabledCommandLine && SetDword(Registry.LocalMachine, CommandLineKey, "ProcessCreationIncludeCmdLine_Enabled", 0))
            reverted.Add("cmdline_audit");

        if (journal.EnabledScriptBlock && SetDword(Registry.LocalMachine, ScriptBlockKey, "EnableScriptBlockLogging", 0))
            reverted.Add("script_block");

        if (journal.EnabledTaskLog && SetEventLog(SourceInspector.TaskLogName, false))
            reverted.Add("task_log");

        if (File.Exists(JournalPath)) File.Delete(JournalPath);
        return reverted;
    }

    private static bool EnableProcessCreationAudit() => RunAuditPol("/success:enable");

    private static bool RunAuditPol(string switches)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo("auditpol.exe",
                $"/set /subcategory:{ProcessCreationSubcategory} {switches}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null) return false;
            process.WaitForExit(10_000);

            if (process.ExitCode != 0)
            {
                Log.Warn($"auditpol failed ({process.ExitCode}): {process.StandardError.ReadToEnd().Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("auditpol could not be started", ex);
            return false;
        }
    }

    private static bool SetEventLog(string logName, bool enabled)
    {
        try
        {
            var configuration = new EventLogConfiguration(logName);
            if (configuration.IsEnabled == enabled) return false;

            configuration.IsEnabled = enabled;
            configuration.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not change the {logName} log: {ex.Message}");
            return false;
        }
    }

    private static bool SetDword(RegistryKey root, string path, string name, int value)
    {
        try
        {
            using var key = root.CreateSubKey(path, true);
            if (key is null) return false;

            if (key.GetValue(name) is int current && current == value) return false;
            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn($"no permission to write {path}\\{name}");
            return false;
        }
    }
}
