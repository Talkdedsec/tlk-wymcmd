using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using Microsoft.Win32;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Setup;

public enum SourceState { Ok, Degraded, Missing, Unknown }

public sealed record SourceStatus(string Key, SourceState State, string? Detail = null);

/// <summary>
/// What this machine can currently tell us. Every capability the tool depends on is checked
/// the same way the user would check it by hand, and nothing is enabled as a side effect.
/// </summary>
public static class SourceInspector
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static IReadOnlyList<SourceStatus> Inspect() =>
    [
        Admin(),
        Etw(),
        BlackBox(),
        SecurityAudit(),
        CommandLineAudit(),
        ScriptBlockLogging(),
        TaskLog(),
        Sysmon(),
        Prefetch(),
        Database()
    ];

    private static SourceStatus Admin()
        => new("admin", IsAdministrator() ? SourceState.Ok : SourceState.Missing);

    private static SourceStatus Etw()
        => new("etw", IsAdministrator() ? SourceState.Ok : SourceState.Missing);

    public static SourceStatus BlackBox()
    {
        if (!BlackBoxInstaller.IsInstalled()) return new SourceStatus("blackbox", SourceState.Missing);

        var size = BlackBoxInstaller.TraceSizeBytes();
        var detail = size > 0 ? $"{size / (1024 * 1024)} MB" : null;

        return BlackBoxInstaller.IsEnabled() switch
        {
            true => new SourceStatus("blackbox", SourceState.Ok, detail),
            false => new SourceStatus("blackbox", SourceState.Degraded, detail),
            null => new SourceStatus("blackbox", SourceState.Ok, detail ?? Loc.T("doctor.detail.installed"))
        };
    }

    /// <summary>
    /// Asking for the newest 4688 backwards costs a walk of the whole Security log when process
    /// auditing was never switched on, and that log is routinely hundreds of megabytes. The read
    /// gets a budget and says Unknown rather than holding whoever asked.
    /// </summary>
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);

    private static SourceStatus SecurityAudit()
    {
        var clock = Stopwatch.StartNew();

        try
        {
            var query = new EventLogQuery("Security", PathType.LogName, "*[System[EventID=4688]]")
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent(ReadBudget);

            if (record is null) return clock.Elapsed >= ReadBudget ? TimedOut() : new SourceStatus("security_audit", SourceState.Missing);

            var age = DateTime.Now - (record.TimeCreated ?? DateTime.MinValue);
            return new SourceStatus("security_audit",
                age < TimeSpan.FromDays(2) ? SourceState.Ok : SourceState.Degraded,
                record.TimeCreated?.ToString("g"));
        }
        catch (UnauthorizedAccessException)
        {
            return new SourceStatus("security_audit", SourceState.Degraded, Loc.T("doctor.detail.needs_admin"));
        }
        catch (EventLogException)
        {
            return clock.Elapsed >= ReadBudget ? TimedOut() : new SourceStatus("security_audit", SourceState.Missing);
        }

        static SourceStatus TimedOut()
            => new("security_audit", SourceState.Unknown, Loc.T("doctor.detail.timed_out"));
    }

    private static SourceStatus CommandLineAudit()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit");
        var enabled = key?.GetValue("ProcessCreationIncludeCmdLine_Enabled") is int value && value == 1;
        return new SourceStatus("cmdline_audit", enabled ? SourceState.Ok : SourceState.Missing);
    }

    private static SourceStatus ScriptBlockLogging()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Policies\Microsoft\Windows\PowerShell\ScriptBlockLogging");
        var enabled = key?.GetValue("EnableScriptBlockLogging") is int value && value == 1;
        return new SourceStatus("script_block", enabled ? SourceState.Ok : SourceState.Missing);
    }

    /// <summary>
    /// The Task Scheduler operational log ships disabled on Windows 10 and 11, and it is the
    /// only source that names the task behind a launch when svchost will not talk to us.
    /// </summary>
    public static SourceStatus TaskLog()
    {
        try
        {
            var configuration = new EventLogConfiguration(TaskLogName);
            return new SourceStatus("task_log", configuration.IsEnabled ? SourceState.Ok : SourceState.Missing);
        }
        catch (Exception)
        {
            return new SourceStatus("task_log", SourceState.Missing);
        }
    }

    public const string TaskLogName = "Microsoft-Windows-TaskScheduler/Operational";

    private static SourceStatus Sysmon()
    {
        foreach (var name in new[] { "Sysmon64", "Sysmon" })
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            if (key is not null) return new SourceStatus("sysmon", SourceState.Ok, name);
        }
        return new SourceStatus("sysmon", SourceState.Missing);
    }

    private static SourceStatus Prefetch()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        if (!Directory.Exists(folder)) return new SourceStatus("prefetch", SourceState.Missing);

        try
        {
            var count = Directory.EnumerateFiles(folder, "*.pf").Take(1).Count();
            return new SourceStatus("prefetch", count > 0 ? SourceState.Ok : SourceState.Degraded);
        }
        catch (UnauthorizedAccessException)
        {
            return new SourceStatus("prefetch", SourceState.Degraded, Loc.T("doctor.detail.needs_admin"));
        }
    }

    private static SourceStatus Database()
    {
        if (!File.Exists(AppPaths.Database)) return new SourceStatus("database", SourceState.Degraded, Loc.T("doctor.detail.no_events"));

        var size = new FileInfo(AppPaths.Database).Length / 1024;

        try
        {
            using var store = new EventStore();
            var (count, oldest, newest) = store.Bounds();

            var detail = count == 0
                ? $"{size} KB, {Loc.T("doctor.detail.no_events")}"
                : Loc.T("doctor.detail.database", size, count, oldest, newest);

            return new SourceStatus("database", count == 0 ? SourceState.Degraded : SourceState.Ok, detail);
        }
        catch (Exception ex)
        {
            return new SourceStatus("database", SourceState.Degraded, ex.Message);
        }
    }
}
