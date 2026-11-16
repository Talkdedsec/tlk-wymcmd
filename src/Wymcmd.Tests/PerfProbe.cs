using System.Diagnostics;
using System.Text;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Store;
using Xunit;

namespace Wymcmd.Tests;

/// <summary>
/// Times the paths the window and the CLI lean on, against a database the size a busy machine
/// reaches in a month. Opt-in, because a timing is a measurement and not an assertion:
///
///   $env:WYMCMD_PERF = "perf.txt"; dotnet test --filter PerfProbe
/// </summary>
public class PerfProbe
{
    private const int Rows = 100_000;

    private static readonly string[] Images =
    [
        "cmd.exe", "powershell.exe", "conhost.exe", "svchost.exe", "chrome.exe", "git.exe", "node.exe",
        "dllhost.exe", "RuntimeBroker.exe", "SearchProtocolHost.exe", "backgroundTaskHost.exe", "pwsh.exe"
    ];

    [Fact]
    public async Task Report()
    {
        var report = Environment.GetEnvironmentVariable("WYMCMD_PERF");
        if (string.IsNullOrEmpty(report)) return;

        var file = Path.Combine(Path.GetTempPath(), $"wymcmd-perf-{Guid.NewGuid():n}.db");
        var lines = new StringBuilder();
        void Note(string what, double ms) => lines.AppendLine($"{what,-46} {ms,10:F1} ms");

        using (var store = new EventStore(file))
        {
            var random = new Random(7);
            var oldest = DateTime.Now.AddDays(-30);
            var step = TimeSpan.FromDays(30) / Rows;

            var clock = Stopwatch.StartNew();
            for (var i = 0; i < Rows; i++)
                store.Enqueue(Event(i, oldest + step * i, random));
            await store.FlushAsync(TimeSpan.FromMinutes(5));
            Note($"insert {Rows:N0} events", clock.Elapsed.TotalMilliseconds);

            Assert.Equal(Rows, store.CountAll());

            Note("window: default list (3 days, consoles, 500)", Best(() => store.Query(new EventFilter
            {
                From = DateTime.Now.AddDays(-3), ConsoleOnly = true, Limit = 500
            })));
            Note("window: text search", Best(() => store.Query(new EventFilter
            {
                From = DateTime.Now.AddDays(-3), Text = "payload", Limit = 500
            })));
            Note("window: hidden only + min risk 50", Best(() => store.Query(new EventFilter
            {
                From = DateTime.Now.AddDays(-3), HiddenOnly = true, MinRisk = 50, Limit = 500
            })));
            Note("window: status line count", Best(() => store.CountAll()));
            Note("cli: list --last 30d --risk 70 (100k cap)", Best(() => store.Query(new EventFilter
            {
                From = oldest, MinRisk = 70, Limit = 100_000
            })));
            Note("cli: why <pid>", Best(() => store.FindByPid(5000 + Rows / 2)));
            Note("stats page (7 days)", Best(() => EventStats.Collect(store, TimeSpan.FromDays(7))));

            var rules = Enumerable.Range(0, 10).Select(i => new Rule
            {
                Image = i % 2 == 0 ? "cmd.exe" : "power*.exe",
                CommandLine = i % 3 == 0 ? "download(string|file)" : null,
                HiddenWindow = i % 4 == 0 ? true : null
            }).ToList();
            Note("rules page: dry run, 10 rules over 7 days", Best(() =>
            {
                var recent = store.Query(new EventFilter { From = DateTime.Now.AddDays(-7), Limit = 5000 });
                foreach (var rule in rules) _ = recent.Count(rule.Matches);
            }));

            clock.Restart();
            for (var i = 0; i < 1000; i++)
                store.UpdateExit(5000 + i, oldest + step * i, DateTime.Now, 0);
            Note("1,000 exit updates", clock.Elapsed.TotalMilliseconds);

            Note("prune oldest tenth", Best(() => store.PruneOldest(0.10), 1));
        }

        lines.AppendLine($"{"database file",-46} {new FileInfo(file).Length / 1048576.0,10:F1} MB");
        File.WriteAllText(report, lines.ToString());

        try { File.Delete(file); } catch { /* the pool may still hold it */ }
    }

    private static double Best(Action action, int runs = 5)
    {
        var best = double.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var clock = Stopwatch.StartNew();
            action();
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static ProcEvent Event(int i, DateTime start, Random random)
    {
        var image = Images[random.Next(Images.Length)];
        var evt = new ProcEvent
        {
            Pid = 5000 + i,
            ParentPid = 800,
            StartTime = start,
            ImageName = image,
            ImagePath = $"C:\\Windows\\System32\\{image}",
            CommandLine = $"\"C:\\Windows\\System32\\{image}\" /c \"C:\\ProgramData\\vendor\\job-{i % 400}.cmd\" --quiet {(i % 50 == 0 ? "payload" : "run")}",
            UserName = "PC\\user",
            SessionId = 1,
            ParentImageName = "svchost.exe",
            Window = i % 5 == 0 ? WindowVisibility.Hidden : WindowVisibility.Visible,
            Risk = random.Next(0, 100),
            Sources = EvidenceSource.Etw,
            Confidence = Confidence.Certain,
            Signature = new SignatureInfo { Status = SignatureStatus.Valid, Publisher = "Microsoft Windows" },
            Source = new LaunchSource { Kind = LaunchSourceKind.ScheduledTask, Name = $"\\vendor\\job-{i % 400}" }
        };

        evt.Chain.Add(new AncestorLink { Pid = 800, ImageName = "svchost.exe" });
        evt.Chain.Add(new AncestorLink { Pid = 700, ImageName = "services.exe" });
        evt.Chain.Add(new AncestorLink { Pid = 600, ImageName = "wininit.exe" });
        evt.RiskFactors.Add(new RiskFactor("hidden_window", 25));
        return evt;
    }
}
