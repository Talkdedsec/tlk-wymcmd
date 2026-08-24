using System.Security;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Setup;

/// <summary>
/// The black box: ETW AutoLoggers that Windows itself starts at boot and writes into circular
/// files. Nothing of ours stays resident - there is no wymcmd process, no service and no CPU
/// cost - yet when you open the tool later the history is sitting in the trace.
///
/// Two sessions, on purpose. The manifest one (Microsoft-Windows-Kernel-Process) is dependable
/// everywhere but its start events carry no command line. The system trace one does carry it,
/// and is the newer arrangement; if a machine refuses to start it, the first session still has
/// the launch recorded and only the command line is missing.
/// </summary>
public static class BlackBoxInstaller
{
    private const string AutologgerRoot = @"SYSTEM\CurrentControlSet\Control\WMI\Autologger";
    private const string KernelProcessProvider = "{22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716}";
    private const long KeywordProcess = 0x10;

    private const int LogFileModeCircular = 0x00000002;
    private const int LogFileModeSystemLogger = 0x02000000;
    private const int EnableFlagProcess = 0x00000001;

    private static string SessionKey => $@"{AutologgerRoot}\{AppPaths.BlackBoxSessionName}";
    private static string SystemSessionKey => $@"{AutologgerRoot}\{AppPaths.BlackBoxSystemSessionName}";

    /// <summary>
    /// The autologger key is admin-readable only, so a normal user can see that it exists but
    /// not what is in it. Both answers are reported honestly instead of guessing.
    /// </summary>
    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionKey);
            return key is not null;
        }
        catch (SecurityException)
        {
            return true;
        }
    }

    public static bool? IsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionKey);
            if (key is null) return false;
            return key.GetValue("Start") is int start && start == 1;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    public sealed record SessionInfo(string Name, bool Installed, bool? Enabled, string File, long Bytes, bool CarriesCommandLine);

    /// <summary>What each session looks like right now. Reading the keys needs administrator.</summary>
    public static IReadOnlyList<SessionInfo> Describe() =>
    [
        Describe(AppPaths.BlackBoxSessionName, SessionKey, AppPaths.BlackBoxTrace, carriesCommandLine: false),
        Describe(AppPaths.BlackBoxSystemSessionName, SystemSessionKey, AppPaths.BlackBoxSystemTrace, carriesCommandLine: true)
    ];

    private static SessionInfo Describe(string name, string keyPath, string file, bool carriesCommandLine)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            var enabled = key is null ? (bool?)false : key.GetValue("Start") is int start && start == 1;
            return new SessionInfo(name, key is not null, enabled, file, Size(file), carriesCommandLine);
        }
        catch (SecurityException)
        {
            return new SessionInfo(name, true, null, file, Size(file), carriesCommandLine);
        }
    }

    public static long TraceSizeBytes()
        => Size(AppPaths.BlackBoxTrace) + Size(AppPaths.BlackBoxSystemTrace);

    private static long Size(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>Writes both autologger definitions. They take effect on the next boot.</summary>
    public static void Install(int maxFileSizeMb = 64)
    {
        SharedRoot.Ensure();
        Directory.CreateDirectory(AppPaths.Root);

        var half = Math.Max(16, maxFileSizeMb / 2);

        InstallManifestSession(half);
        InstallSystemSession(half);

        // The registry side only takes effect at boot; start the same sessions now so the
        // recorder is useful from this second, without leaving a process of ours behind.
        StartNow(half);

        Log.Info($"black box installed, two traces in {AppPaths.Root} capped at {half} MB each");
    }

    private static void InstallManifestSession(int maxFileSizeMb)
    {
        using var key = Registry.LocalMachine.CreateSubKey(SessionKey, true)
            ?? throw new UnauthorizedAccessException("cannot write the autologger key");

        WriteCommon(key, AppPaths.BlackBoxTrace, maxFileSizeMb, LogFileModeCircular);

        using var provider = key.CreateSubKey(KernelProcessProvider, true)
            ?? throw new UnauthorizedAccessException("cannot write the provider key");

        provider.SetValue("Enabled", 1, RegistryValueKind.DWord);
        provider.SetValue("EnableLevel", 5, RegistryValueKind.DWord);      // verbose
        provider.SetValue("MatchAnyKeyword", KeywordProcess, RegistryValueKind.QWord);
    }

    private static void InstallSystemSession(int maxFileSizeMb)
    {
        using var key = Registry.LocalMachine.CreateSubKey(SystemSessionKey, true)
            ?? throw new UnauthorizedAccessException("cannot write the autologger key");

        WriteCommon(key, AppPaths.BlackBoxSystemTrace, maxFileSizeMb,
            LogFileModeCircular | LogFileModeSystemLogger);

        // The system trace provider is switched on with flags rather than a provider subkey.
        key.SetValue("EnableFlags", EnableFlagProcess, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Starts both sessions immediately. They belong to the operating system, not to us:
    /// StopOnDispose stays off, so they keep recording after this process exits.
    /// </summary>
    private static void StartNow(int maxFileSizeMb)
    {
        Start(AppPaths.BlackBoxSessionName, AppPaths.BlackBoxTrace, maxFileSizeMb, session =>
            session.EnableProvider(new Guid(KernelProcessProvider), TraceEventLevel.Verbose, KeywordProcess));

        Start(AppPaths.BlackBoxSystemSessionName, AppPaths.BlackBoxSystemTrace, maxFileSizeMb, session =>
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process));
    }

    private static void Start(string name, string file, int maxFileSizeMb, Action<TraceEventSession> enable)
    {
        try
        {
            StopSession(name);

            var session = new TraceEventSession(name, file)
            {
                StopOnDispose = false,
                CircularBufferMB = maxFileSizeMb
            };

            enable(session);
            session.Dispose();
            Log.Info($"black box session '{name}' recording into {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            // A machine that refuses one session still has the other, and both come back at boot.
            Log.Warn($"could not start '{name}' now, it will start at the next boot: {ex.Message}");
        }
    }

    private static void StopSession(string name)
    {
        try
        {
            if (!TraceEventSession.GetActiveSessionNames().Contains(name)) return;
            using var existing = new TraceEventSession(name) { StopOnDispose = true };
            existing.Stop();
        }
        catch (Exception)
        {
            // Nothing to stop, or not ours to stop.
        }
    }

    private static void WriteCommon(RegistryKey key, string file, int maxFileSizeMb, int logFileMode)
    {
        key.SetValue("Start", 1, RegistryValueKind.DWord);
        key.SetValue("Guid", "{" + Guid.NewGuid().ToString("d") + "}", RegistryValueKind.String);
        key.SetValue("FileName", file, RegistryValueKind.String);
        key.SetValue("LogFileMode", logFileMode, RegistryValueKind.DWord);
        key.SetValue("MaxFileSize", maxFileSizeMb, RegistryValueKind.DWord);
        key.SetValue("BufferSize", 64, RegistryValueKind.DWord);           // KB per buffer
        key.SetValue("MinimumBuffers", 4, RegistryValueKind.DWord);
        key.SetValue("MaximumBuffers", 16, RegistryValueKind.DWord);
        key.SetValue("FlushTimer", 5, RegistryValueKind.DWord);
        key.SetValue("ClockType", 1, RegistryValueKind.DWord);             // QPC
    }

    public static void Uninstall(bool deleteTrace = true)
    {
        foreach (var key in new[] { SessionKey, SystemSessionKey })
            Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

        StopSession(AppPaths.BlackBoxSessionName);
        StopSession(AppPaths.BlackBoxSystemSessionName);

        if (deleteTrace)
        {
            foreach (var file in new[] { AppPaths.BlackBoxTrace, AppPaths.BlackBoxSystemTrace })
            {
                if (!File.Exists(file)) continue;

                try
                {
                    File.Delete(file);
                }
                catch (IOException ex)
                {
                    // The session still holds the file until the next boot.
                    Log.Warn("trace file still in use: " + ex.Message);
                }
            }
        }

        Log.Info("black box removed");
    }
}
