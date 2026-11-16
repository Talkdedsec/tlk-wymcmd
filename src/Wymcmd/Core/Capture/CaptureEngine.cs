using System.Threading.Channels;
using Wymcmd.Core.Actions;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Sign;
using Wymcmd.Core.Store;
using Wymcmd.Core.Tree;
using Wymcmd.Core.Why;
using Wymcmd.Core.Windows;

namespace Wymcmd.Core.Capture;

/// <summary>
/// The live path: collector -> queue -> enrichment -> rules -> storage -> subscribers.
/// The collector callback only writes to the queue, so nothing slow ever runs on the ETW
/// pump and a burst of 500 processes cannot stall the kernel session.
/// </summary>
public sealed class CaptureEngine : IAsyncDisposable
{
    private readonly EventStore _store;
    private readonly ProcessTree _tree;
    private readonly AttributionEngine _attribution;
    private readonly RuleSet _rules;

    // Starts and stops share one queue so a stop is never handled before its own start. A
    // process that lives 40 ms exits long before its start has been enriched.
    private readonly Channel<object> _incoming = Channel.CreateUnbounded<object>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Launches whose exit has not been seen yet, by pid. Only the worker touches it.</summary>
    private readonly Dictionary<int, ProcEvent> _running = [];
    private readonly ICollector? _injected;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private ICollector? _collector;
    private int _enriched;

    private WatchLedger? _ledger;
    private System.Threading.Timer? _beat;
    private long _watchId;

    /// <param name="collector">A collector to use instead of ETW or WMI - what the tests feed events through.</param>
    public CaptureEngine(EventStore store, ProcessTree tree, AttributionEngine attribution, RuleSet? rules = null,
        ICollector? collector = null)
    {
        _injected = collector;
        _store = store;
        _tree = tree;
        _attribution = attribution;
        _rules = rules ?? new RuleSet();
    }

    public event Action<ProcEvent>? Observed;
    public event Action<int, DateTime>? Ended;
    public event Action<ProcEvent, Rule>? RuleFired;

    public bool Lossless => _collector?.Lossless ?? false;
    public EvidenceSource ActiveSource => _collector?.Source ?? EvidenceSource.None;

    /// <summary>Rules are only enforced when asked - watching should not change the machine.</summary>
    public bool EnforceRules { get; set; }

    /// <summary>Which watcher this is, for the coverage record. The window is Live, the service Service.</summary>
    public WatchKind Kind { get; init; } = WatchKind.Live;

    public void Start()
    {
        if (_worker is not null) return;

        _tree.Seed();
        Log.Info($"process tree seeded with {_tree.Count} records");

        Maintenance.RunInBackground(_store);

        _collector = SelectCollector();
        _collector.Started += OnStarted;
        _collector.Stopped += OnStopped;
        _collector.Start();

        _worker = Task.Run(() => EnrichLoopAsync(_shutdown.Token));

        OpenWatch();
    }

    /// <summary>
    /// Records that something was watching from here on. Losing the ledger is not a reason to
    /// stop capturing, so a failure here is noted and swallowed.
    /// </summary>
    private void OpenWatch()
    {
        try
        {
            _ledger = new WatchLedger(_store.DatabasePath);
            _watchId = _ledger.Begin(Kind);
            _beat = new System.Threading.Timer(_ => Beat(), null, WatchLedger.BeatInterval, WatchLedger.BeatInterval);
        }
        catch (Exception ex)
        {
            Log.Warn("coverage could not be recorded: " + ex.Message);
            _ledger = null;
        }
    }

    private void Beat()
    {
        try { _ledger?.Beat(_watchId); }
        catch (Exception ex) { Log.Warn("coverage heartbeat failed: " + ex.Message); }
    }

    private void CloseWatch()
    {
        _beat?.Dispose();
        _beat = null;

        try { _ledger?.End(_watchId); }
        catch (Exception ex) { Log.Warn("coverage could not be closed: " + ex.Message); }

        _ledger = null;
        _watchId = 0;
    }

    private ICollector SelectCollector()
    {
        if (_injected is not null) return _injected;

        var etw = new EtwCollector();
        if (etw.Available) return etw;

        etw.Dispose();
        Log.Warn("no administrator rights - falling back to WMI, short-lived processes will be missed");
        return new WmiCollector();
    }

    private void OnStarted(RawStart raw) => _incoming.Writer.TryWrite(raw);

    private void OnStopped(RawStop raw) => _incoming.Writer.TryWrite(raw);

    private void Finish(RawStop raw)
    {
        _tree.MarkExit(raw.Pid, raw.TimeStamp, raw.ExitCode);

        // The row may still be waiting in the store's write queue, where an UPDATE finds nothing.
        // Setting the exit on the event itself covers that case: the insert carries it.
        if (_running.Remove(raw.Pid, out var evt))
        {
            evt.ExitTime = raw.TimeStamp;
            evt.ExitCode = raw.ExitCode;
        }

        var record = _tree.Resolve(raw.Pid, raw.TimeStamp);
        if (record is not null)
            _store.UpdateExit(raw.Pid, record.StartTime, raw.TimeStamp, raw.ExitCode);

        Ended?.Invoke(raw.Pid, raw.TimeStamp);
    }

    private async Task EnrichLoopAsync(CancellationToken token)
    {
        var reader = _incoming.Reader;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var item = await reader.ReadAsync(token).ConfigureAwait(false);
                if (item is RawStop stop)
                {
                    Finish(stop);
                    continue;
                }

                var evt = Enrich((RawStart)item);
                _running[evt.Pid] = evt;

                var fired = Apply(evt);

                _store.Enqueue(evt);
                if (++_enriched % 250 == 0) Log.Info($"events enriched and queued: {_enriched}");
                Observed?.Invoke(evt);

                if (evt.IsConsoleHost) _ = ResolveWindowLaterAsync(evt, fired);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("enrichment failed", ex);
            }
        }
    }

    private ProcEvent Enrich(RawStart raw)
    {
        var imagePath = PathNames.Normalize(raw.ImagePath);
        if (imagePath.Length == 0 || !Path.IsPathRooted(imagePath))
            imagePath = ProcessQuery.ImagePath(raw.Pid) ?? imagePath;

        var commandLine = string.IsNullOrEmpty(raw.CommandLine)
            ? ProcessQuery.CommandLine(raw.Pid) ?? ""
            : raw.CommandLine;

        var record = _tree.Add(new ProcRecord
        {
            Pid = raw.Pid,
            ParentPid = raw.ParentPid,
            StartKey = raw.StartKey,
            ImageName = raw.ImageName,
            ImagePath = imagePath,
            CommandLine = commandLine,
            StartTime = raw.TimeStamp,
            SessionId = raw.SessionId
        });

        var (sid, user) = ProcessQuery.User(raw.Pid);
        var parent = _tree.Resolve(raw.ParentPid, raw.TimeStamp);

        var evt = new ProcEvent
        {
            Pid = raw.Pid,
            ParentPid = raw.ParentPid,
            StartKey = raw.StartKey,
            StartTime = raw.TimeStamp,
            ImageName = raw.ImageName,
            ImagePath = imagePath,
            CommandLine = commandLine,
            WorkingDirectory = ProcessQuery.WorkingDirectory(raw.Pid),
            UserName = user,
            UserSid = sid,
            SessionId = raw.SessionId,
            Elevated = ProcessQuery.IsElevated(raw.Pid) ?? false,
            ParentImageName = parent?.ImageName ?? "",
            ParentCommandLine = parent?.CommandLine,
            Sources = raw.Source,
            Confidence = raw.Source == EvidenceSource.Etw ? Confidence.Certain : Confidence.High,
            Signature = SignatureVerifier.Check(imagePath)
        };

        record.UserName = user;
        evt.Chain.AddRange(_tree.BuildChain(record));

        var decoded = CommandLineDecoder.Decode(evt.ImageName, evt.CommandLine);
        evt.DecodedCommand = decoded.Payload;

        _attribution.Attribute(evt);
        RiskScorer.Score(evt, decoded.Traits);
        return evt;
    }

    /// <summary>
    /// A console window appears a beat after the process does, so the first read would always
    /// say "hidden". Give it a moment, then correct the record.
    /// </summary>
    private async Task ResolveWindowLaterAsync(ProcEvent evt, Rule? fired)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), _shutdown.Token).ConfigureAwait(false);
            var visibility = WindowFinder.ConsoleVisibility(evt.Pid, pid => _tree.Resolve(pid)?.ParentPid);
            if (visibility == evt.Window) return;

            evt.Window = visibility;
            if (visibility == WindowVisibility.Hidden)
            {
                var decoded = CommandLineDecoder.Decode(evt.ImageName, evt.CommandLine);
                RiskScorer.Score(evt, decoded.Traits);
            }

            // The first pass judged this launch before its window state was known, so a rule that
            // asks for a hidden window, or for the risk a hidden window adds, could not match yet.
            if (_rules.Ordered.FirstOrDefault(rule => rule.Matches(evt)) is { } now && now != fired)
                Apply(evt);

            Observed?.Invoke(evt);
            await _store.UpdateWindowAsync(evt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Rules are always matched so the caller can report what would happen; only the actions
    /// that change something wait for EnforceRules.
    /// </summary>
    private Rule? Apply(ProcEvent evt)
    {
        // Called from the worker and again from the window check, which runs on a pool thread.
        lock (_rules)
        {
            var rule = _rules.FirstMatch(evt);
            if (rule is null) return null;

            RuleFired?.Invoke(evt, rule);

            if (EnforceRules) Enforce(evt, rule);
            return rule;
        }
    }

    private void Enforce(ProcEvent evt, Rule rule)
    {
        if (rule.Action is RuleAction.Allow or RuleAction.Log or RuleAction.Notify) return;

        var result = rule.Action switch
        {
            RuleAction.Kill => ProcessActions.Kill(evt.Pid, evt.ImageName),
            RuleAction.KillTree => ProcessActions.KillTree(_tree, evt.Pid),
            RuleAction.Suspend => ProcessActions.Suspend(evt.Pid),
            RuleAction.Hide => ProcessActions.HideWindow(evt.Pid),
            _ => new ActionResult(ActionOutcome.Done)
        };

        Log.Info($"rule '{rule.Name}' -> {rule.Action} on {evt.ImageName} ({evt.Pid}): {result.Outcome}");
    }

    public void Stop()
    {
        _collector?.Stop();
        _collector?.Dispose();
        _collector = null;

        CloseWatch();
    }

    /// <summary>
    /// The window disposes the engine as it closes, so nothing here may need the caller's thread
    /// back to finish - see the same note on the event store.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _incoming.Writer.TryComplete();

        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); } catch { /* on the way out */ }
        }

        _shutdown.Dispose();
    }
}
