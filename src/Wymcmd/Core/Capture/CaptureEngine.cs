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

    private readonly Channel<RawStart> _incoming = Channel.CreateUnbounded<RawStart>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private ICollector? _collector;

    public CaptureEngine(EventStore store, ProcessTree tree, AttributionEngine attribution, RuleSet? rules = null)
    {
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

    public void Start()
    {
        if (_worker is not null) return;

        _tree.Seed();
        Log.Info($"process tree seeded with {_tree.Count} records");

        _collector = SelectCollector();
        _collector.Started += OnStarted;
        _collector.Stopped += OnStopped;
        _collector.Start();

        _worker = Task.Run(() => EnrichLoopAsync(_shutdown.Token));
    }

    private ICollector SelectCollector()
    {
        var etw = new EtwCollector();
        if (etw.Available) return etw;

        etw.Dispose();
        Log.Warn("no administrator rights - falling back to WMI, short-lived processes will be missed");
        return new WmiCollector();
    }

    private void OnStarted(RawStart raw) => _incoming.Writer.TryWrite(raw);

    private void OnStopped(RawStop raw)
    {
        _tree.MarkExit(raw.Pid, raw.TimeStamp, raw.ExitCode);
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
                var raw = await reader.ReadAsync(token);
                var evt = Enrich(raw);

                if (EnforceRules) Apply(evt);

                _store.Enqueue(evt);
                Observed?.Invoke(evt);

                if (evt.IsConsoleHost) _ = ResolveWindowLaterAsync(evt);
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
        var imagePath = raw.ImagePath;
        if (string.IsNullOrEmpty(imagePath) || !Path.IsPathRooted(imagePath))
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
    private async Task ResolveWindowLaterAsync(ProcEvent evt)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), _shutdown.Token);
            var visibility = WindowFinder.ConsoleVisibility(evt.Pid, pid => _tree.Resolve(pid)?.ParentPid);
            if (visibility == evt.Window) return;

            evt.Window = visibility;
            if (visibility == WindowVisibility.Hidden)
            {
                var decoded = CommandLineDecoder.Decode(evt.ImageName, evt.CommandLine);
                RiskScorer.Score(evt, decoded.Traits);
            }

            Observed?.Invoke(evt);
            await _store.UpdateWindowAsync(evt.Pid, evt.StartTime, evt.Window, evt.Risk);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void Apply(ProcEvent evt)
    {
        var rule = _rules.FirstMatch(evt);
        if (rule is null) return;

        RuleFired?.Invoke(evt, rule);

        var result = rule.Action switch
        {
            RuleAction.Kill => ProcessActions.Kill(evt.Pid, evt.ImageName),
            RuleAction.KillTree => ProcessActions.KillTree(_tree, evt.Pid),
            RuleAction.Suspend => ProcessActions.Suspend(evt.Pid),
            RuleAction.Hide => ProcessActions.HideWindow(evt.Pid),
            _ => new ActionResult(ActionOutcome.Done)
        };

        if (rule.Action is RuleAction.Allow or RuleAction.Log or RuleAction.Notify) return;
        Log.Info($"rule '{rule.Name}' -> {rule.Action} on {evt.ImageName} ({evt.Pid}): {result.Outcome}");
    }

    public void Stop()
    {
        _collector?.Stop();
        _collector?.Dispose();
        _collector = null;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _shutdown.CancelAsync();
        _incoming.Writer.TryComplete();
        if (_worker is not null)
        {
            try { await _worker; } catch { /* shutdown */ }
        }
        _shutdown.Dispose();
    }
}
