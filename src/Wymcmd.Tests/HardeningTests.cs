using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using Wymcmd.Cli;
using Wymcmd.Cli.Commands;
using Wymcmd.Core.Actions;
using Wymcmd.Core.Capture;
using Wymcmd.Core.Ipc;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Setup;
using Wymcmd.Core.Store;
using Xunit;

namespace Wymcmd.Tests;

/// <summary>
/// The parts that decide what a LocalSystem service may be told to do, and what gets killed.
/// None of these need administrator rights: the decisions are tested, not the privileged calls.
/// </summary>
public sealed class HardeningTests
{
    private static FileSecurity Sddl(string sddl)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    [Fact]
    public void A_file_only_administrators_can_write_is_trusted()
        => Assert.True(FileTrust.IsAdminOnly(Sddl("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)")));

    [Theory]
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1301bf;;;BU)")]   // Users may modify
    [InlineData("O:BUG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)")]                     // owned by a user, who can rewrite the ACL
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;GA;;;WD)")]                     // Everyone, as a generic right
    [InlineData("O:BAG:BAD:P(A;;FA;;;SY)(A;;WD;;;AU)")]                     // may change permissions
    public void A_file_somebody_else_can_change_is_not_trusted(string sddl)
        => Assert.False(FileTrust.IsAdminOnly(Sddl(sddl)));

    [Fact]
    public void A_file_with_no_access_list_at_all_is_not_trusted()
        => Assert.False(FileTrust.IsAdminOnly(Sddl("O:BAG:BAD:NO_ACCESS_CONTROL")));

    /// <summary>The two folders setup creates: one every user fills, one only administrators write.</summary>
    [Fact]
    public void The_protected_folder_is_admin_only_and_the_shared_one_is_not()
    {
        Assert.True(FileTrust.IsAdminOnly(SharedRoot.ProtectedSecurity()));
        Assert.False(FileTrust.IsAdminOnly(SharedRoot.RootSecurity()));
    }

    /// <summary>Users may put files in the shared folder but not delete or rename the folder itself.</summary>
    [Fact]
    public void Users_cannot_delete_the_shared_folder_itself()
    {
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);

        var onTheFolder = SharedRoot.RootSecurity()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(users) && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly));

        Assert.All(onTheFolder, rule =>
            Assert.Equal((FileSystemRights)0, rule.FileSystemRights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles)));
    }

    /// <summary>
    /// The hole this closes: a rules file in a folder any user can write, read by a service that
    /// kills as LocalSystem. A temp file is exactly such a file.
    /// </summary>
    [Fact]
    public void The_service_ignores_rules_a_normal_user_could_have_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wymcmd-untrusted-{Guid.NewGuid():n}.json");
        try
        {
            new RuleSet { Rules = [new Rule { Name = "everything", Image = "*", Action = RuleAction.Kill }] }.Save(path);

            Assert.Single(RuleSet.Load(path).Rules);
            Assert.Empty(RuleSet.LoadTrusted(path).Rules);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Rules from the old, open location are kept but cannot arrive armed: anyone could have
    /// written them, and that includes an "allow" that shields something from the other rules.
    /// </summary>
    [Fact]
    public void Rules_carried_over_from_the_open_folder_arrive_switched_off()
    {
        var legacy = new RuleSet
        {
            Rules =
            [
                new Rule { Name = "kill", Image = "*", Action = RuleAction.Kill },
                new Rule { Name = "tree", Image = "a.exe", Action = RuleAction.KillTree },
                new Rule { Name = "allow", Image = "evil.exe", Action = RuleAction.Allow },
                new Rule { Name = "log", Image = "cmd.exe", Action = RuleAction.Log },
                new Rule { Name = "notify", Image = "cmd.exe", Action = RuleAction.Notify }
            ]
        };

        var carried = RuleSet.Disarm(legacy);

        Assert.Equal(5, carried.Rules.Count);
        Assert.Equal(["log", "notify"], carried.Rules.Where(rule => rule.Enabled).Select(rule => rule.Name));
    }

    [Fact]
    public void A_rule_set_that_does_not_parse_is_refused_instead_of_saved_empty()
        => Assert.ThrowsAny<System.Text.Json.JsonException>(() => RuleSet.Parse("{ not json"));

    [Theory]
    [InlineData(true, false, 0)]     // already on: not ours, and nothing to run
    [InlineData(false, true, 1)]     // off: we turn it on and own it
    [InlineData(null, false, 1)]     // could not tell: turn it on, but never claim it
    public void Uninstall_only_owns_auditing_it_actually_switched_on(bool? before, bool owned, int runs)
    {
        var ran = 0;
        var result = AuditPolicySetup.EnableProcessCreationAudit(() => before, _ => { ran++; return true; });

        Assert.Equal(owned, result);
        Assert.Equal(runs, ran);
    }

    /// <summary>
    /// A client that connects and never reads used to hold the capture worker in a write for as
    /// long as it liked. It gets a quarter second and is dropped.
    /// </summary>
    [Fact]
    public async Task A_pipe_client_that_stops_reading_is_dropped_instead_of_waited_for()
    {
        var name = $"wymcmd-test-{Guid.NewGuid():n}";
        using var server = new PipeServer(name);
        server.Start();

        await using var silent = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        await silent.ConnectAsync(5000);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.ClientCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(1, server.ClientCount);

        var evt = new ProcEvent { Pid = 1, ImageName = "cmd.exe", CommandLine = new string('x', 4096), StartTime = DateTime.Now };

        var clock = Stopwatch.StartNew();
        await Task.Run(() => { for (var i = 0; i < 200; i++) server.Broadcast(evt); });

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"broadcast took {clock.Elapsed}");
        Assert.Equal(0, server.ClientCount);
    }

    [Fact]
    public void Only_sessions_nobody_reads_any_more_are_stopped()
    {
        var active = new[] { "WymcmdLive", "WymcmdLive-100", "WymcmdLive-200", "WymcmdLive-300", "WymcmdLive-abc", "NT Kernel Logger", "WymcmdBlackBox" };

        var stale = EtwCollector.Stale(active, own: "WymcmdLive-300", isRunningWymcmd: pid => pid == 100).ToList();

        Assert.Equal(["WymcmdLive", "WymcmdLive-200", "WymcmdLive-300", "WymcmdLive-abc"], stale);
    }

    [Fact]
    public void A_rule_saved_while_capture_runs_reaches_the_running_engine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wymcmd-rules-{Guid.NewGuid():n}.json");
        try
        {
            new RuleSet().Save(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));

            var live = RuleSet.Load(path);
            using var watcher = new RuleWatcher(live, path, () => RuleSet.Load(path));

            new RuleSet { Rules = [new Rule { Name = "late", Image = "cmd.exe" }] }.Save(path);
            watcher.Check();

            Assert.Equal("late", Assert.Single(live.Rules).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Catching up must add what the database lacks and nothing it has: the black box and a live
    /// capture stamp the same launch a hair apart, and that must not become two rows.
    /// </summary>
    [Fact]
    public void Catching_up_adds_only_launches_the_database_does_not_have()
    {
        var at = new DateTime(2026, 11, 26, 9, 0, 0, DateTimeKind.Local);

        var known = new[]
        {
            new ProcEvent { RowId = 1, Pid = 100, ImageName = "cmd.exe", StartTime = at }
        };

        var harvested = new[]
        {
            new ProcEvent { Pid = 100, ImageName = "CMD.EXE", StartTime = at.AddSeconds(4) },     // same launch, on a drifted clock
            new ProcEvent { Pid = 100, ImageName = "ping.exe", StartTime = at.AddSeconds(4) },    // same pid, already another program
            new ProcEvent { Pid = 100, ImageName = "cmd.exe", StartTime = at.AddMinutes(5) },     // same pid, later process
            new ProcEvent { Pid = 200, ImageName = "cmd.exe", StartTime = at },                   // before anyone logged on
            new ProcEvent { RowId = 1, Pid = 300, ImageName = "cmd.exe", StartTime = at }         // came out of the database itself
        };

        var fresh = Wymcmd.Core.Forensic.ForensicHarvester.NotYetKnown(harvested, known);

        Assert.Equal(
            [(100, "ping.exe"), (100, "cmd.exe"), (200, "cmd.exe")],
            fresh.Select(evt => (evt.Pid, evt.ImageName)));
    }

    [Fact]
    public void The_clock_offset_follows_drift_and_ignores_a_stray_sample()
    {
        var clock = new ClockOffset();

        // Drift arrives a little at a time.
        for (var ms = 1; ms <= 300; ms++) clock.Sample(TimeSpan.FromMilliseconds(ms));
        Assert.Equal(300, clock.Value.TotalMilliseconds, 25.0);

        // A pid that now belongs to a process started 40 seconds later.
        clock.Sample(TimeSpan.FromSeconds(40));
        Assert.Equal(300, clock.Value.TotalMilliseconds, 25.0);

        // Close but wrong: it may nudge the offset, never drag it.
        clock.Sample(TimeSpan.FromMilliseconds(700));
        Assert.Equal(300, clock.Value.TotalMilliseconds, 25.0);
    }

    [Fact]
    public void The_clock_offset_accepts_a_jump_once_enough_samples_agree()
    {
        var clock = new ClockOffset();
        var jump = TimeSpan.FromHours(1);

        for (var i = 0; i < 4; i++) clock.Sample(jump);
        Assert.Equal(TimeSpan.Zero, clock.Value);

        clock.Sample(jump);
        Assert.Equal(jump, clock.Value);
    }

    /// <summary>A typo in the rules file must not leave a running service with no rules at all.</summary>
    [Fact]
    public void A_rules_file_that_stops_parsing_leaves_the_loaded_rules_alone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wymcmd-rules-{Guid.NewGuid():n}.json");
        try
        {
            new RuleSet { Rules = [new Rule { Name = "kept", Image = "cmd.exe" }] }.Save(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));

            var live = RuleSet.Load(path);
            using var watcher = new RuleWatcher(live, path, () => RuleSet.Load(path));

            File.WriteAllText(path, "{ \"Rules\": [ { \"Name\": ");
            watcher.Check();

            Assert.Equal("kept", Assert.Single(live.Rules).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Many silent clients share one budget; they used to get a quarter second each, in turn.</summary>
    [Fact]
    public async Task Many_silent_pipe_clients_cost_no_more_than_one()
    {
        var name = $"wymcmd-test-{Guid.NewGuid():n}";
        using var server = new PipeServer(name);
        server.Start();

        var clients = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
                await client.ConnectAsync(5000);
                clients.Add(client);
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (server.ClientCount < 20 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(20, server.ClientCount);

            var evt = new ProcEvent { Pid = 1, ImageName = "cmd.exe", CommandLine = new string('x', 4096), StartTime = DateTime.Now };

            var clock = Stopwatch.StartNew();
            await Task.Run(() => { for (var i = 0; i < 100; i++) server.Broadcast(evt); });

            // Twenty clients at a quarter second each, one after the other, was five seconds.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"broadcast took {clock.Elapsed}");
            Assert.Equal(0, server.ClientCount);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    /// <summary>
    /// Seen on a real machine: a console read back from the black box, where a launch is just
    /// "cmd.exe" with no folder, was reported as started by the first scheduled task that happens
    /// to run cmd.exe. Sharing a file name with a task is not being started by it.
    /// </summary>
    [Fact]
    public void A_task_that_runs_cmd_does_not_claim_every_console()
    {
        var index = new Wymcmd.Core.Why.AutostartIndex();
        index.Seed(
        [
            new Wymcmd.Core.Why.AutostartEntry(LaunchSourceKind.ScheduledTask, @"\Vendor\Monitoring", "Task Scheduler",
                @"C:\Windows\System32\cmd.exe /c C:\Vendor\monitor.cmd", @"C:\Windows\System32\cmd.exe")
        ]);

        Assert.Null(index.Match("cmd.exe", @"""C:\Windows\system32\cmd.exe"" /c ping -n 2 127.0.0.1 >nul"));

        // The task's own launch is still recognised: same name, and its command line is in there.
        Assert.NotNull(index.Match("cmd.exe", @"C:\Windows\System32\cmd.exe /c C:\Vendor\monitor.cmd"));
    }

    [Theory]
    [InlineData(4, "anything.exe")]
    [InlineData(5000, "lsass.exe")]
    [InlineData(5000, "  CSRSS.EXE ")]
    [InlineData(5000, "svchost.exe")]
    public void Nothing_opens_the_door_to_a_protected_process(int pid, string image)
        => Assert.True(KillGuard.IsProtected(pid, image));

    [Fact]
    public void The_tool_never_kills_itself()
        => Assert.True(KillGuard.IsProtected(Environment.ProcessId, "wymcmd.exe"));

    /// <summary>
    /// A pid is handed to the next process as soon as the last one exits. A kill that names when
    /// its target started must leave a process with the same pid and another start time alone.
    /// </summary>
    [Fact]
    public void A_kill_meant_for_an_earlier_process_leaves_the_one_that_has_its_pid_now()
    {
        using var target = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;

        try
        {
            var stale = ProcessActions.Kill(target.Id, "cmd.exe", target.StartTime.AddMinutes(-10));
            Assert.Equal(ActionOutcome.NotFound, stale.Outcome);
            Assert.False(target.HasExited);

            var suspended = ProcessActions.Suspend(target.Id, target.StartTime.AddMinutes(-10));
            Assert.Equal(ActionOutcome.NotFound, suspended.Outcome);

            var real = ProcessActions.Kill(target.Id, "cmd.exe", target.StartTime);
            Assert.Equal(ActionOutcome.Done, real.Outcome);
            Assert.True(target.WaitForExit(5000));
        }
        finally
        {
            try { target.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>"trap --path X" on its own was quietly turned into "trap cmd.exe that is also at X".</summary>
    [Fact]
    public void A_trap_on_a_path_alone_is_not_narrowed_to_cmd()
    {
        var rule = Trap.BuildRule(new CliOptions(["--path", @"C:\Tools\*.exe"]));

        Assert.Null(rule.Image);
        Assert.Equal(@"C:\Tools\*.exe", rule.ImagePath);
    }

    [Fact]
    public void A_trap_with_no_filter_still_watches_consoles()
        => Assert.Equal("cmd.exe", Trap.BuildRule(new CliOptions([])).Image);

    /// <summary>A task named "=cmd|' /c calc'!A1" must arrive in a spreadsheet as text, not as a formula.</summary>
    [Fact]
    public void An_exported_cell_cannot_start_a_formula()
    {
        var csv = Exporter.Csv(
        [
            new ProcEvent
            {
                Pid = 7,
                ImageName = "cmd.exe",
                CommandLine = "-enc AAAA\r\nsecond line",
                StartTime = DateTime.Now,
                Source = new LaunchSource { Kind = LaunchSourceKind.ScheduledTask, Name = "=cmd|' /c calc'!A1" }
            }
        ]);

        Assert.Contains(",'=cmd|' /c calc'!A1,", csv);
        Assert.Contains("\"'-enc AAAA\r\nsecond line\"", csv);
    }
}
