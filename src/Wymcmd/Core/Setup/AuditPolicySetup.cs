using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
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

    // Kept where only administrators can write: whoever edits this decides what uninstall turns off.
    private static string JournalPath => AppPaths.SetupJournal;

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

        // In the shared folder the journal is only believed if nobody but an administrator could
        // have written it: a journal that says "we turned this on" gets it turned off.
        if (AppPaths.IsShared && !FileTrust.IsAdminOnly(JournalPath))
        {
            Log.Error($"{JournalPath} can be changed by non-administrators; it is ignored");
            return new Journal();
        }

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
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
        if (AppPaths.IsShared) SharedRoot.Seal(JournalPath);
    }

    public static IReadOnlyList<string> EnableAll()
    {
        SharedRoot.Ensure();

        var journal = ReadJournal();
        var changed = new List<string>();

        if (EnableProcessCreationAudit(SuccessAuditOn, RunAuditPol))
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

        // Only success auditing was ours to turn on; failure auditing is left as it was found.
        if (journal.EnabledProcessAudit && RunAuditPol("/success:disable"))
            reverted.Add("security_audit");

        if (journal.EnabledCommandLine && SetDword(Registry.LocalMachine, CommandLineKey, "ProcessCreationIncludeCmdLine_Enabled", 0))
            reverted.Add("cmdline_audit");

        if (journal.EnabledScriptBlock && SetDword(Registry.LocalMachine, ScriptBlockKey, "EnableScriptBlockLogging", 0))
            reverted.Add("script_block");

        if (journal.EnabledTaskLog && SetEventLog(SourceInspector.TaskLogName, false))
            reverted.Add("task_log");

        foreach (var path in new[] { JournalPath, AppPaths.LegacySetupJournal })
            if (File.Exists(path)) File.Delete(path);
        return reverted;
    }

    /// <summary>
    /// True only when success auditing was off and this call turned it on. Auditing that was
    /// already on is not ours, and neither is auditing whose earlier state could not be read:
    /// uninstall switches off what this returns true for.
    /// </summary>
    internal static bool EnableProcessCreationAudit(Func<bool?> successAuditOn, Func<string, bool> auditPol)
    {
        var before = successAuditOn();
        if (before == true) return false;

        return auditPol("/success:enable") && before == false;
    }

    private const uint AuditSuccess = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIT_POLICY_INFORMATION
    {
        public Guid AuditSubCategoryGuid;
        public uint AuditingInformation;
        public Guid AuditCategoryGuid;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AuditQuerySystemPolicy(ref Guid subCategory, uint count, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);

    /// <summary>Asks Windows directly: auditpol prints its answer in the system language.</summary>
    private static bool? SuccessAuditOn()
    {
        var subcategory = Guid.Parse(ProcessCreationSubcategory);
        if (!AuditQuerySystemPolicy(ref subcategory, 1, out var buffer) || buffer == IntPtr.Zero) return null;

        try
        {
            return (Marshal.PtrToStructure<AUDIT_POLICY_INFORMATION>(buffer).AuditingInformation & AuditSuccess) != 0;
        }
        finally
        {
            AuditFree(buffer);
        }
    }

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
