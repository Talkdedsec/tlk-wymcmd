using Wymcmd.Core.Localization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Why;
using Xunit;

namespace Wymcmd.Tests;

[Collection("language")]
public class AttributionTests
{
    private static AttributionEngine Engine(params AutostartEntry[] entries)
    {
        var index = new AutostartIndex();
        index.Seed(entries);
        return new AttributionEngine(index);
    }

    private static ProcEvent Launch(string image, string commandLine, params string[] ancestors)
    {
        var evt = new ProcEvent
        {
            Pid = 4242,
            ParentPid = 800,
            StartTime = DateTime.Now,
            ImageName = image,
            ImagePath = $"C:\\Windows\\System32\\{image}",
            CommandLine = commandLine
        };

        var pid = 800;
        foreach (var ancestor in ancestors)
        {
            evt.Chain.Add(new AncestorLink { Pid = pid, ImageName = ancestor });
            pid -= 100;
        }

        evt.ParentImageName = ancestors.FirstOrDefault() ?? "";
        return evt;
    }

    [Fact]
    public void A_console_started_from_explorer_is_credited_to_you()
    {
        var evt = Launch("cmd.exe", "cmd.exe", "explorer.exe", "userinit.exe");

        Engine().Attribute(evt);

        Assert.Equal(LaunchSourceKind.UserShell, evt.Source?.Kind);
    }

    [Fact]
    public void A_run_key_entry_wins_over_the_explorer_that_actually_spawned_it()
    {
        var entry = new AutostartEntry(LaunchSourceKind.RunKey, "Updater", "HKCU\\...\\Run",
            "C:\\Tools\\updater.exe --silent", "C:\\Tools\\updater.exe");

        var evt = Launch("updater.exe", "C:\\Tools\\updater.exe --silent", "explorer.exe");
        evt.ImagePath = "C:\\Tools\\updater.exe";

        Engine(entry).Attribute(evt);

        Assert.Equal(LaunchSourceKind.RunKey, evt.Source?.Kind);
        Assert.Equal("Updater", evt.Source?.Name);
    }

    [Fact]
    public void A_service_parent_means_a_service()
    {
        var evt = Launch("agent.exe", "agent.exe", "services.exe", "wininit.exe");

        Engine().Attribute(evt);

        Assert.Equal(LaunchSourceKind.Service, evt.Source?.Kind);
    }

    [Fact]
    public void An_office_document_child_is_marked_as_such()
    {
        var evt = Launch("cmd.exe", "cmd.exe /c calc", "winword.exe", "explorer.exe");

        Engine().Attribute(evt);

        Assert.Equal(LaunchSourceKind.OfficeMacro, evt.Source?.Kind);
    }

    [Fact]
    public void A_terminal_parent_is_a_terminal_not_a_mystery()
    {
        var evt = Launch("cmd.exe", "cmd.exe", "WindowsTerminal.exe", "explorer.exe");

        Engine().Attribute(evt);

        Assert.Equal(LaunchSourceKind.Terminal, evt.Source?.Kind);
    }

    [Fact]
    public void Nothing_known_stays_unknown_instead_of_guessing()
    {
        var evt = Launch("weird.exe", "weird.exe", "alsoweird.exe");

        Engine().Attribute(evt);

        Assert.Equal(LaunchSourceKind.Unknown, evt.Source?.Kind);
        Assert.Equal(Confidence.Inferred, evt.Source?.Confidence);
    }

    [Fact]
    public void The_verdict_reads_as_a_sentence_in_both_languages()
    {
        var evt = Launch("cmd.exe", "cmd.exe", "explorer.exe");
        Engine().Attribute(evt);

        Loc.Use("en");
        var english = AttributionEngine.Verdict(evt);
        Loc.Use("tr");
        var turkish = AttributionEngine.Verdict(evt);

        Assert.Contains("explorer.exe", english);
        Assert.Contains("explorer.exe", turkish);
        Assert.NotEqual(english, turkish);
    }

    [Fact]
    public void The_chain_reads_oldest_first_and_ends_with_the_process_itself()
    {
        var evt = Launch("cmd.exe", "cmd.exe", "explorer.exe", "userinit.exe");

        var chain = AttributionEngine.ChainText(evt);

        Assert.EndsWith("cmd.exe", chain);
        Assert.True(chain.IndexOf("userinit.exe", StringComparison.Ordinal)
                    < chain.IndexOf("explorer.exe", StringComparison.Ordinal));
    }
}
