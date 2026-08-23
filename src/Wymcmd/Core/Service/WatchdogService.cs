using System.Diagnostics;
using System.ServiceProcess;
using Wymcmd.Core.Capture;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Ipc;
using Wymcmd.Core.Rules;
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

    public WatchdogService() => ServiceName = ServiceName_;

    public static void RunAsService() => Run(new WatchdogService());

    protected override void OnStart(string[] args)
    {
        Log.Info("watchdog service starting");

        _store = new EventStore();
        var tree = new ProcessTree();
        var rules = RuleSet.Load(AppPaths.Rules);

        _engine = new CaptureEngine(_store, tree, new AttributionEngine(new AutostartIndex()), rules)
        {
            EnforceRules = true
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

    public static bool Install()
    {
        var executable = Environment.ProcessPath;
        if (executable is null) return false;

        // Without this the service writes to its own profile and the UI never sees the events.
        SharedRoot.Ensure();

        return Sc($"create {ServiceName_} binPath= \"\\\"{executable}\\\" --service\" start= delayed-auto DisplayName= \"{DisplayName}\"")
               && Sc($"description {ServiceName_} \"Records what launches console windows and enforces wymcmd rules.\"")
               && Sc($"failure {ServiceName_} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
    }

    public static bool Uninstall()
    {
        StopService();
        return Sc($"delete {ServiceName_}");
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
