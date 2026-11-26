using System.Diagnostics;
using System.ServiceProcess;
using Wymcmd.Core.Capture;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Ipc;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;
using Wymcmd.Core.Tree;
using Wymcmd.Core.Why;

namespace Wymcmd.Core.Service;

/// <summary>
/// The optional always-on mode. It ships disabled: the tool is useful with nothing resident,
/// and a service only earns its place when you want rules enforced while you are away.
/// </summary>
public sealed class WatchdogService : ServiceBase
{
    public const string ServiceName_ = "wymcmd";
    private const string DisplayName = "wymcmd watchdog";

    private EventStore? _store;
    private CaptureEngine? _engine;
    private PipeServer? _feed;
    private RuleWatcher? _ruleWatcher;

    public WatchdogService() => ServiceName = ServiceName_;

    public static void RunAsService() => Run(new WatchdogService());

    protected override void OnStart(string[] args)
    {
        Log.Info("watchdog service starting");

        _store = new EventStore();
        var tree = new ProcessTree();
        // After an update from before 0.4.2 the protected folder may not exist yet. The service
        // is allowed to make it, and doing so here beats running with no rules and saying nothing.
        SharedRoot.Ensure();

        var rules = RuleSet.LoadTrusted(AppPaths.Rules);
        _ruleWatcher = new RuleWatcher(rules, AppPaths.Rules, () => RuleSet.LoadTrusted(AppPaths.Rules));

        _engine = new CaptureEngine(_store, tree, new AttributionEngine(new AutostartIndex()), rules)
        {
            EnforceRules = true,
            Kind = WatchKind.Service
        };
        _feed = new PipeServer();
        _feed.Start();
        _engine.Observed += evt => _feed.Broadcast(evt);

        _engine.Start();

        Log.Info("watchdog service started");
    }

    protected override void OnStop()
    {
        Log.Info("watchdog service stopping");
        _ruleWatcher?.Dispose();
        _engine?.Stop();
        _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _feed?.Dispose();
        _store?.Dispose();
    }

    public static bool IsInstalled()
    {
        try
        {
            return ServiceController.GetServices().Any(s =>
                s.ServiceName.Equals(ServiceName_, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string? State()
    {
        try
        {
            using var controller = new ServiceController(ServiceName_);
            return controller.Status.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the service's own copy of the program lives. Program Files can only be written by
    /// administrators; the per-user install folder cannot hold a binary that runs as LocalSystem,
    /// because whoever can replace that file becomes LocalSystem at the next start.
    /// </summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "wymcmd");

    public static bool Install() => Install(InstallDirectory);

    internal static bool Install(string directory)
    {
        var installed = IsInstalled();
        var wasRunning = installed && State() == nameof(ServiceControllerStatus.Running);
        if (installed) StopAndWait();      // the running service holds its own file open

        var executable = Path.Combine(directory, "wymcmd.exe");
        try
        {
            Directory.CreateDirectory(directory);
            PathInstaller.CopyPayload(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("the service binary could not be copied", ex);
            return false;
        }

        if (!File.Exists(executable))
        {
            Log.Error($"no wymcmd.exe to install the service from (running as {Environment.ProcessPath})");
            return false;
        }

        // Without this the service writes to its own profile and the UI never sees the events.
        SharedRoot.Ensure();

        var binPath = $"binPath= \"\\\"{executable}\\\" --service\" start= delayed-auto DisplayName= \"{DisplayName}\"";

        // An install from an older version points at the per-user folder; this moves it.
        var done = Sc($"{(installed ? "config" : "create")} {ServiceName_} {binPath}")
               && Sc($"description {ServiceName_} \"Records what launches console windows and enforces wymcmd rules.\"")
               && Sc($"failure {ServiceName_} reset= 86400 actions= restart/5000/restart/10000/restart/30000");

        return wasRunning ? done && StartService() : done;
    }

    public static bool Uninstall()
    {
        StopAndWait();
        var deleted = Sc($"delete {ServiceName_}");

        try
        {
            var running = Path.GetDirectoryName(Environment.ProcessPath);
            if (Directory.Exists(InstallDirectory) &&
                !string.Equals(running, InstallDirectory, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(InstallDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("could not remove the service's copy of the program: " + ex.Message);
        }

        return deleted;
    }

    private static void StopAndWait()
    {
        try
        {
            using var controller = new ServiceController(ServiceName_);
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                if (controller.Status != ServiceControllerStatus.StopPending) controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception ex)
        {
            // Not installed, or it would not stop in time: the copy that follows reports the real problem.
            Log.Debug("service stop: " + ex.Message);
        }
    }

    public static bool StartService() => Sc($"start {ServiceName_}");

    public static bool StopService() => Sc($"stop {ServiceName_}");

    private static bool Sc(string arguments)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null) return false;
            process.WaitForExit(15_000);

            if (process.ExitCode == 0) return true;

            Log.Warn($"sc {arguments.Split(' ')[0]} failed ({process.ExitCode}): {process.StandardOutput.ReadToEnd().Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("sc.exe could not be started", ex);
            return false;
        }
    }
}
