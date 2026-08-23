using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Capture;

/// <summary>
/// Real-time kernel tracing. This is the only capture path that sees a 30 ms cmd.exe and
/// still hands over its full command line, so everything else in the app is a fallback.
/// Needs administrator rights.
/// </summary>
public sealed class EtwCollector : ICollector
{
    private readonly string _sessionName;
    private TraceEventSession? _session;
    private Thread? _pump;
    private volatile bool _running;
    private int _restarts;

    public EtwCollector(string? sessionName = null) => _sessionName = sessionName ?? AppPaths.LiveSessionName;

    public EvidenceSource Source => EvidenceSource.Etw;
    public bool Lossless => true;
    public bool Available => TraceEventSession.IsElevated() == true;

    public event Action<RawStart>? Started;
    public event Action<RawStop>? Stopped;

    public void Start()
    {
        if (_running) return;
        if (!Available) throw new UnauthorizedAccessException("ETW capture needs administrator rights.");

        DropStaleSession(_sessionName);

        _session = new TraceEventSession(_sessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 64
        };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);

        _session.Source.Kernel.ProcessStart += OnProcessStart;
        _session.Source.Kernel.ProcessStop += OnProcessStop;

        _running = true;
        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "wymcmd-etw"
        };
        _pump.Start();
        Log.Info($"etw session '{_sessionName}' started");
    }

    private void Pump()
    {
        while (_running)
        {
            try
            {
                _session!.Source.Process();
                if (!_running) return;

                // Process() returning on its own means the session died under us.
                throw new InvalidOperationException("trace session ended unexpectedly");
            }
            catch (Exception ex) when (_running)
            {
                _restarts++;
                var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(_restarts, 5))));
                Log.Warn($"etw session lost ({ex.Message}); restarting in {backoff.TotalSeconds:0}s");
                Thread.Sleep(backoff);
                if (!_running) return;
                try { Restart(); } catch (Exception restartFailure) { Log.Error("etw restart failed", restartFailure); }
            }
        }
    }

    private void Restart()
    {
        _session?.Dispose();
        DropStaleSession(_sessionName);

        _session = new TraceEventSession(_sessionName) { StopOnDispose = true, BufferSizeMB = 64 };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
        _session.Source.Kernel.ProcessStart += OnProcessStart;
        _session.Source.Kernel.ProcessStop += OnProcessStop;
    }

    private void OnProcessStart(ProcessTraceData data)
    {
        // Callback runs on the ETW pump: copy the fields, hand off, get out.
        Started?.Invoke(new RawStart(
            data.ProcessID,
            data.ParentID,
            StartKey(data.ProcessID, data.TimeStamp),
            SafeName(data.ImageFileName),
            data.ImageFileName ?? "",
            data.CommandLine ?? "",
            (int)data.SessionID,
            data.TimeStamp,
            EvidenceSource.Etw));
    }

    private void OnProcessStop(ProcessTraceData data)
        => Stopped?.Invoke(new RawStop(data.ProcessID, data.TimeStamp, data.ExitStatus));

    /// <summary>Classic kernel events carry no start key, so pid plus start time stands in for one.</summary>
    private static ulong StartKey(int pid, DateTime startTime)
        => (uint)pid | ((ulong)startTime.Ticks << 32);

    private static string SafeName(string? imageFileName)
    {
        if (string.IsNullOrEmpty(imageFileName)) return "";
        var slash = imageFileName.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? imageFileName[(slash + 1)..] : imageFileName;
    }

    public static void DropStaleSession(string name)
    {
        try
        {
            if (!TraceEventSession.GetActiveSessionNames().Contains(name)) return;
            using var stale = new TraceEventSession(name) { StopOnDispose = true };
            stale.Stop();
            Log.Info($"stale etw session '{name}' stopped");
        }
        catch (Exception ex)
        {
            Log.Warn($"could not stop stale session '{name}': {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _session?.Stop(); } catch { /* already gone */ }
        _session?.Dispose();
        _session = null;
        _pump?.Join(TimeSpan.FromSeconds(2));
        _pump = null;
        Log.Info($"etw session '{_sessionName}' stopped");
    }

    public void Dispose() => Stop();
}
