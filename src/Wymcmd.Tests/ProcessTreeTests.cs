using Wymcmd.Core.Tree;
using Xunit;

namespace Wymcmd.Tests;

public class ProcessTreeTests
{
    private static ProcRecord Record(int pid, int parent, string image, DateTime start)
        => new() { Pid = pid, ParentPid = parent, ImageName = image, StartTime = start };

    [Fact]
    public void Builds_the_chain_up_to_the_root()
    {
        var tree = new ProcessTree();
        var now = DateTime.Now;

        tree.Add(Record(4, 0, "System", now.AddMinutes(-10)));
        tree.Add(Record(100, 4, "services.exe", now.AddMinutes(-9)));
        tree.Add(Record(200, 100, "svchost.exe", now.AddMinutes(-8)));
        var child = tree.Add(Record(300, 200, "cmd.exe", now));

        var chain = tree.BuildChain(child);

        Assert.Equal(["svchost.exe", "services.exe", "System"], chain.Select(link => link.ImageName));
    }

    [Fact]
    public void A_parent_recorded_a_moment_late_is_still_the_parent()
    {
        // WMI reports creation times with one-second resolution, so this happens constantly.
        var tree = new ProcessTree();
        var now = DateTime.Now;

        tree.Add(Record(100, 4, "explorer.exe", now.AddMilliseconds(900)));
        var child = tree.Add(Record(200, 100, "cmd.exe", now));

        var chain = tree.BuildChain(child);

        Assert.Equal("explorer.exe", chain[0].ImageName);
    }

    [Fact]
    public void A_parent_that_started_much_later_is_a_recycled_pid()
    {
        var tree = new ProcessTree();
        var now = DateTime.Now;

        tree.Add(Record(100, 4, "explorer.exe", now.AddMinutes(5)));
        var child = tree.Add(Record(200, 100, "cmd.exe", now));

        var chain = tree.BuildChain(child);

        Assert.Single(chain);
        Assert.Equal(100, chain[0].Pid);
        Assert.False(chain[0].Alive);
    }

    [Fact]
    public void A_new_start_on_the_same_pid_closes_out_the_previous_tenant()
    {
        var tree = new ProcessTree();
        var start = DateTime.Now.AddMinutes(-5);

        tree.Add(Record(500, 4, "old.exe", start));
        tree.Add(Record(500, 4, "new.exe", start.AddMinutes(1)));

        var records = tree.AllRecords().Where(record => record.Pid == 500).ToList();

        Assert.Equal(2, records.Count);
        Assert.NotNull(records[0].ExitTime);
        Assert.Null(records[1].ExitTime);
        Assert.Equal("new.exe", tree.Resolve(500)?.ImageName);
    }

    [Fact]
    public void Resolves_the_record_that_covers_a_moment()
    {
        var tree = new ProcessTree();
        var start = DateTime.Now.AddMinutes(-10);

        tree.Add(Record(700, 4, "first.exe", start));
        tree.Add(Record(700, 4, "second.exe", start.AddMinutes(5)));

        Assert.Equal("first.exe", tree.Resolve(700, start.AddMinutes(1))?.ImageName);
        Assert.Equal("second.exe", tree.Resolve(700, start.AddMinutes(6))?.ImageName);
    }

    [Fact]
    public void Finds_live_descendants_without_looping_on_a_cycle()
    {
        var tree = new ProcessTree();
        var now = DateTime.Now;

        tree.Add(Record(10, 4, "root.exe", now));
        tree.Add(Record(11, 10, "child.exe", now));
        tree.Add(Record(12, 11, "grandchild.exe", now));
        tree.Add(Record(13, 13, "self-parented.exe", now));

        var descendants = tree.LiveDescendants(10);

        Assert.Equal(2, descendants.Count);
        Assert.Contains(descendants, record => record.ImageName == "grandchild.exe");
    }

    [Fact]
    public void Pruning_drops_dead_records_and_keeps_live_ones()
    {
        var tree = new ProcessTree();
        var now = DateTime.Now;

        tree.Add(Record(20, 4, "alive.exe", now.AddHours(-5)));
        var dead = tree.Add(Record(21, 4, "dead.exe", now.AddHours(-5)));
        dead.ExitTime = now.AddHours(-4);

        tree.Prune(TimeSpan.FromHours(1));

        Assert.Single(tree.AllRecords());
        Assert.Equal("alive.exe", tree.AllRecords()[0].ImageName);
    }

    [Fact]
    public void Marking_an_exit_records_the_code()
    {
        var tree = new ProcessTree();
        tree.Add(Record(30, 4, "thing.exe", DateTime.Now.AddSeconds(-2)));

        tree.MarkExit(30, DateTime.Now, 3);

        var record = tree.Resolve(30);
        Assert.NotNull(record!.ExitTime);
        Assert.Equal(3, record.ExitCode);
        Assert.False(record.Alive);
    }
}
