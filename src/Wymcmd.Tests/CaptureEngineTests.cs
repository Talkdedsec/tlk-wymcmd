using System.Diagnostics;
using Wymcmd.Core.Capture;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Store;
using Wymcmd.Core.Tree;
using Wymcmd.Core.Why;
using Xunit;

namespace Wymcmd.Tests;

/// <summary>The live path, driven by a collector that says exactly what the test tells it to.</summary>
public sealed class CaptureEngineTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wymcmd-engine-{Guid.NewGuid():n}.db");

    private sealed class Script : ICollector
    {
        public EvidenceSource Source => EvidenceSource.Etw;
        public bool Available => true;
        public bool Lossless => true;
        public event Action<RawStart>? Started;
        public event Action<RawStop>? Stopped;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void Launch(int pid, string image, DateTime when, int parentPid = 0)
            => Started?.Invoke(new RawStart(pid, parentPid, 0, image, $"C:\\Windows\\System32\\{image}", image, 1, when, EvidenceSource.Etw));

        public void Exit(int pid, DateTime when, int code) => Stopped?.Invoke(new RawStop(pid, when, code));
    }

    private static AttributionEngine Attribution()
    {
        var index = new AutostartIndex();
        index.Seed([]);
        return new AttributionEngine(index);
    }

    /// <summary>
    /// The flash this tool is named after: a process that is gone before anything has looked at
    /// it. Its exit used to be handled ahead of its own start and was lost.
    /// </summary>
    [Fact]
    public async Task A_process_that_exits_at_once_still_gets_its_exit_recorded()
    {
        await using var store = new EventStore(_path);
        var script = new Script();
        await using var engine = new CaptureEngine(store, new ProcessTree(), Attribution(), collector: script);
        engine.Start();

        var started = DateTime.Now;
        for (var i = 0; i < 50; i++)
        {
            script.Launch(900_000 + i, "flash.exe", started.AddMilliseconds(i));
            script.Exit(900_000 + i, started.AddMilliseconds(i + 40), 7);
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        IReadOnlyList<ProcEvent> rows = [];
        while (DateTime.UtcNow < deadline)
        {
            await store.FlushAsync();
            rows = store.Query(new EventFilter { Text = "flash.exe", Limit = 100 });
            if (rows.Count == 50 && rows.All(row => row.ExitTime is not null)) break;
            await Task.Delay(100);
        }

        Assert.Equal(50, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(7, row.ExitCode);
            Assert.Equal(40, row.Lifetime!.Value.TotalMilliseconds, 1.0);
        });
    }

    /// <summary>
    /// The watch button: on, off, on. The second "on" used to return at its first line because
    /// the worker from the first one was still there, while the window said it was watching.
    /// </summary>
    [Fact]
    public async Task Capture_that_was_stopped_can_be_started_again()
    {
        await using var store = new EventStore(_path);
        var script = new Script();
        await using var engine = new CaptureEngine(store, new ProcessTree(), Attribution(), collector: script);

        var seen = new List<int>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Observed += evt =>
        {
            lock (seen) seen.Add(evt.Pid);
            if (evt.Pid == 920_002) second.TrySetResult();
        };

        engine.Start();
        engine.Stop();

        // Nothing is listening now, so this one is rightly lost.
        script.Launch(920_001, "while-stopped.exe", DateTime.Now);

        engine.Start();
        script.Launch(920_002, "after-restart.exe", DateTime.Now);

        var winner = await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(second.Task, winner);
        lock (seen) Assert.Equal([920_002], seen);
    }

    /// <summary>
    /// Whether a console has a window is only known a moment after it starts. A rule that asks
    /// for a hidden window has to be asked again then, or it never matches anything.
    /// </summary>
    [Fact]
    public async Task A_hidden_window_rule_fires_once_the_window_state_is_known()
    {
        using var hidden = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;

        try
        {
            await using var store = new EventStore(_path);
            var script = new Script();
            var rules = new RuleSet { Rules = [new Rule { Name = "hidden", Image = "cmd.exe", HiddenWindow = true }] };
            await using var engine = new CaptureEngine(store, new ProcessTree(), Attribution(), rules, script);

            var fired = new TaskCompletionSource<ProcEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.RuleFired += (evt, _) => fired.TrySetResult(evt);
            engine.Start();

            script.Launch(hidden.Id, "cmd.exe", DateTime.Now);

            var winner = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(fired.Task, winner);

            var evt = await fired.Task;
            Assert.Equal(WindowVisibility.Hidden, evt.Window);
            Assert.Contains(evt.RiskFactors, factor => factor.Key == "hidden_window" && factor.Weight == 25);
        }
        finally
        {
            try { hidden.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>
    /// A trace session's clock drifts from the system's over a long run, and a kill compares the
    /// launch's start time with what Windows reports. Once the engine has seen the two clocks
    /// disagree the same way a few times, launches are put on the system's clock and can be killed.
    /// </summary>
    [Fact]
    public async Task Launches_stay_killable_when_the_trace_clock_has_drifted()
    {
        using var real = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;

        try
        {
            await using var store = new EventStore(_path);
            var script = new Script();
            await using var engine = new CaptureEngine(store, new ProcessTree(), Attribution(), collector: script);

            var seen = new List<ProcEvent>();
            var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Observed += evt =>
            {
                if (evt.Pid != real.Id) return;
                lock (seen)
                {
                    seen.Add(evt);
                    if (seen.Count == 6) enough.TrySetResult();
                }
            };
            engine.Start();

            // A trace clock three seconds ahead, as far as the engine can tell from six launches.
            var drifted = real.StartTime.AddSeconds(3);
            for (var i = 0; i < 6; i++) script.Launch(real.Id, "cmd.exe", drifted);

            var winner = await Task.WhenAny(enough.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(enough.Task, winner);

            ProcEvent first, last;
            lock (seen) { first = seen[0]; last = seen[5]; }

            Assert.Equal(drifted, first.StartTime);          // one odd sample changes nothing
            Assert.Equal(real.StartTime, last.StartTime);    // five that agree do

            Assert.Equal(Wymcmd.Core.Actions.ActionOutcome.Done,
                Wymcmd.Core.Actions.ProcessActions.Kill(last.Pid, last.ImageName, last.StartTime).Outcome);
        }
        finally
        {
            try { real.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>
    /// Without administrator rights starts arrive in batches, child before parent as often as
    /// not. The chain has to name the parent anyway, or the answer is "nothing could be attributed".
    /// </summary>
    [Fact]
    public async Task A_child_reported_before_its_parent_still_gets_its_chain()
    {
        await using var store = new EventStore(_path);
        var script = new Script();
        await using var engine = new CaptureEngine(store, new ProcessTree(), Attribution(), collector: script);

        var seen = new TaskCompletionSource<ProcEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Observed += evt => { if (evt.Pid == 910_000) seen.TrySetResult(evt); };
        engine.Start();

        // Started after the engine took its snapshot, and never announced through the collector.
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;

        try
        {
            await Task.Delay(300);
            script.Launch(910_000, "child.exe", DateTime.Now, parent.Id);

            var winner = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(seen.Task, winner);

            var evt = await seen.Task;
            Assert.Equal("cmd.exe", evt.ParentImageName, ignoreCase: true);
            Assert.Equal("cmd.exe", evt.Chain[0].ImageName, ignoreCase: true);
        }
        finally
        {
            try { parent.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* the pool may still hold it */ }
    }
}
