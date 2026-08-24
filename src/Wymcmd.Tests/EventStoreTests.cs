using Wymcmd.Core.Model;
using Wymcmd.Core.Store;
using Xunit;

namespace Wymcmd.Tests;

public sealed class EventStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wymcmd-test-{Guid.NewGuid():n}.db");

    private static ProcEvent Event(int pid, DateTime start, string image = "cmd.exe", int risk = 0)
    {
        var evt = new ProcEvent
        {
            Pid = pid,
            ParentPid = 4,
            StartTime = start,
            ImageName = image,
            ImagePath = $"C:\\Windows\\System32\\{image}",
            CommandLine = $"{image} /c echo {pid}",
            UserName = "PC\\user",
            SessionId = 1,
            ParentImageName = "svchost.exe",
            Window = WindowVisibility.Visible,
            Risk = risk,
            Sources = EvidenceSource.Etw,
            Confidence = Confidence.Certain,
            Signature = new SignatureInfo { Status = SignatureStatus.Valid, Publisher = "Microsoft Windows" },
            Source = new LaunchSource { Kind = LaunchSourceKind.ScheduledTask, Name = "\\demo", Location = "Task Scheduler" }
        };

        evt.Chain.Add(new AncestorLink { Pid = 4, ImageName = "svchost.exe", CommandLine = "svchost.exe -k netsvcs" });
        evt.RiskFactors.Add(new RiskFactor("hidden_window", 25));
        return evt;
    }

    private async Task<EventStore> Filled(int count, DateTime? oldest = null)
    {
        var store = new EventStore(_path);
        var start = oldest ?? DateTime.Now.AddMinutes(-count);

        for (var i = 0; i < count; i++)
            store.Enqueue(Event(1000 + i, start.AddSeconds(i)));

        await store.FlushAsync();
        return store;
    }

    [Fact]
    public async Task Writes_and_reads_an_event_back_whole()
    {
        await using var store = await Filled(1);

        var read = store.Query(new EventFilter { Limit = 10 }).Single();

        Assert.Equal(1000, read.Pid);
        Assert.Equal("cmd.exe", read.ImageName);
        Assert.Equal(LaunchSourceKind.ScheduledTask, read.Source?.Kind);
        Assert.Equal("\\demo", read.Source?.Name);
        Assert.Equal(SignatureStatus.Valid, read.Signature.Status);
        Assert.Equal(Confidence.Certain, read.Confidence);
        Assert.Single(read.Chain);
        Assert.Single(read.RiskFactors);
        Assert.Equal("hidden_window", read.RiskFactors[0].Key);
    }

    [Fact]
    public async Task The_stored_chain_leaves_out_command_lines()
    {
        // They are the bulk of a row and the interface never shows them for ancestors.
        await using var store = await Filled(1);

        var read = store.Query(new EventFilter { Limit = 1 }).Single();

        Assert.Equal("svchost.exe", read.Chain[0].ImageName);
        Assert.True(string.IsNullOrEmpty(read.Chain[0].CommandLine));
    }

    [Fact]
    public async Task Filters_narrow_the_result()
    {
        await using var store = new EventStore(_path);
        var now = DateTime.Now;

        store.Enqueue(Event(1, now.AddMinutes(-1), "cmd.exe", risk: 10));
        var hidden = Event(2, now, "powershell.exe", risk: 80);
        hidden.Window = WindowVisibility.Hidden;
        store.Enqueue(hidden);
        store.Enqueue(Event(3, now, "notepad.exe"));
        await store.FlushAsync();

        Assert.Equal(2, store.Query(new EventFilter { ConsoleOnly = true, Limit = 50 }).Count);
        Assert.Single(store.Query(new EventFilter { HiddenOnly = true, Limit = 50 }));
        Assert.Single(store.Query(new EventFilter { MinRisk = 50, Limit = 50 }));
        Assert.Single(store.Query(new EventFilter { Text = "notepad", Limit = 50 }));
        Assert.Empty(store.Query(new EventFilter { From = now.AddHours(1), Limit = 50 }));
    }

    [Fact]
    public async Task Records_an_exit_and_a_late_window_answer()
    {
        await using var store = await Filled(1);
        var evt = store.Query(new EventFilter { Limit = 1 }).Single();

        store.UpdateExit(evt.Pid, evt.StartTime, evt.StartTime.AddMilliseconds(120), 0);
        await store.UpdateWindowAsync(evt.Pid, evt.StartTime, WindowVisibility.Hidden, 55);

        var updated = store.Query(new EventFilter { Limit = 1 }).Single();

        Assert.NotNull(updated.ExitTime);
        Assert.Equal(0, updated.ExitCode);
        Assert.Equal(WindowVisibility.Hidden, updated.Window);
        Assert.Equal(55, updated.Risk);
    }

    [Fact]
    public async Task The_same_launch_is_never_stored_twice()
    {
        await using var store = new EventStore(_path);
        var moment = DateTime.Now;

        store.Enqueue(Event(4242, moment));
        store.Enqueue(Event(4242, moment));
        await store.FlushAsync();

        Assert.Single(store.Query(new EventFilter { Limit = 50 }));
    }

    [Fact]
    public async Task Retention_removes_what_is_older_than_the_window()
    {
        await using var store = new EventStore(_path);
        store.Enqueue(Event(1, DateTime.Now.AddDays(-40)));
        store.Enqueue(Event(2, DateTime.Now));
        await store.FlushAsync();

        var removed = store.Prune(TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
        Assert.Single(store.Query(new EventFilter { Limit = 50 }));
    }

    [Fact]
    public async Task The_size_ceiling_drops_the_oldest_first()
    {
        await using var store = await Filled(20);

        var removed = store.PruneOldest(0.25);
        var left = store.Query(new EventFilter { Limit = 100 });

        Assert.Equal(5, removed);
        Assert.Equal(15, left.Count);
        Assert.DoesNotContain(left, evt => evt.Pid == 1000);
        Assert.Contains(left, evt => evt.Pid == 1019);
    }

    [Fact]
    public async Task Bounds_report_the_span_that_is_actually_stored()
    {
        await using var store = await Filled(5);

        var (count, oldest, newest) = store.Bounds();

        Assert.Equal(5, count);
        Assert.NotNull(oldest);
        Assert.NotNull(newest);
        Assert.True(newest >= oldest);
    }

    [Fact]
    public async Task Maintenance_applies_the_policy_it_is_given()
    {
        await using var store = new EventStore(_path);
        store.Enqueue(Event(1, DateTime.Now.AddDays(-90)));
        store.Enqueue(Event(2, DateTime.Now));
        await store.FlushAsync();

        var result = Maintenance.Run(store, new Settings { RetentionDays = 7, MaxDatabaseMb = 0 });

        Assert.Equal(1, result.RemovedByAge);
        Assert.Single(store.Query(new EventFilter { Limit = 50 }));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { /* left for the temp folder */ }
        }
    }
}
