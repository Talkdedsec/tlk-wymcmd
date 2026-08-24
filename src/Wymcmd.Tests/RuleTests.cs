using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Xunit;

namespace Wymcmd.Tests;

public class RuleTests
{
    private static ProcEvent Console(string image = "cmd.exe", string commandLine = "cmd.exe /c dir")
    {
        var evt = new ProcEvent
        {
            Pid = 1234,
            ParentPid = 100,
            StartTime = DateTime.Now,
            ImageName = image,
            ImagePath = $"C:\\Windows\\System32\\{image}",
            CommandLine = commandLine,
            UserName = "PC\\user",
            Window = WindowVisibility.Visible,
            Signature = new SignatureInfo { Status = SignatureStatus.Valid, Publisher = "Microsoft Windows" }
        };

        evt.Chain.Add(new AncestorLink { Pid = 100, ImageName = "explorer.exe" });
        evt.Chain.Add(new AncestorLink { Pid = 10, ImageName = "userinit.exe" });
        return evt;
    }

    [Fact]
    public void Matches_an_image_glob()
    {
        Assert.True(new Rule { Image = "cmd.exe" }.Matches(Console()));
        Assert.True(new Rule { Image = "cmd.*" }.Matches(Console()));
        Assert.False(new Rule { Image = "powershell.exe" }.Matches(Console()));
    }

    [Fact]
    public void Matches_a_command_line_pattern()
    {
        Assert.True(new Rule { CommandLine = "/c\\s+dir" }.Matches(Console()));
        Assert.False(new Rule { CommandLine = "downloadstring" }.Matches(Console()));
    }

    [Fact]
    public void An_invalid_pattern_never_matches_instead_of_throwing()
    {
        Assert.False(new Rule { CommandLine = "([unclosed" }.Matches(Console()));
    }

    [Fact]
    public void Matches_the_parent_and_any_ancestor()
    {
        Assert.True(new Rule { Parent = "explorer.exe" }.Matches(Console()));
        Assert.False(new Rule { Parent = "userinit.exe" }.Matches(Console()));
        Assert.True(new Rule { Ancestor = "userinit.exe" }.Matches(Console()));
    }

    [Fact]
    public void Signature_and_window_conditions_work_in_both_directions()
    {
        var signed = Console();
        Assert.False(new Rule { Unsigned = true }.Matches(signed));
        Assert.True(new Rule { Unsigned = false }.Matches(signed));

        var hidden = Console();
        hidden.Window = WindowVisibility.Hidden;
        hidden.Signature = new SignatureInfo { Status = SignatureStatus.Unsigned };
        Assert.True(new Rule { Unsigned = true, HiddenWindow = true }.Matches(hidden));
    }

    [Fact]
    public void Temp_path_condition_looks_at_the_image_path()
    {
        var evt = Console();
        evt.ImagePath = "C:\\Users\\me\\AppData\\Local\\Temp\\tool.exe";

        Assert.True(new Rule { InTempPath = true }.Matches(evt));
        Assert.False(new Rule { InTempPath = false }.Matches(evt));
    }

    [Fact]
    public void A_disabled_rule_never_matches()
    {
        Assert.False(new Rule { Image = "cmd.exe", Enabled = false }.Matches(Console()));
    }

    [Fact]
    public void First_match_follows_priority_order()
    {
        var set = new RuleSet
        {
            Rules =
            [
                new Rule { Id = "low", Image = "cmd.exe", Priority = 200, Action = RuleAction.Kill },
                new Rule { Id = "high", Image = "cmd.exe", Priority = 10, Action = RuleAction.Allow }
            ]
        };

        Assert.Equal("high", set.FirstMatch(Console())?.Id);
    }

    [Fact]
    public void Round_trips_through_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wymcmd-rules-{Guid.NewGuid():n}.json");
        try
        {
            var set = new RuleSet { Rules = [new Rule { Name = "test", Image = "cmd.exe", Action = RuleAction.KillTree }] };
            set.Save(path);

            var loaded = RuleSet.Load(path);
            Assert.Single(loaded.Rules);
            Assert.Equal(RuleAction.KillTree, loaded.Rules[0].Action);
            Assert.Equal("cmd.exe", loaded.Rules[0].Image);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_or_broken_file_loads_as_an_empty_set()
    {
        Assert.Empty(RuleSet.Load(Path.Combine(Path.GetTempPath(), "wymcmd-does-not-exist.json")).Rules);

        var broken = Path.Combine(Path.GetTempPath(), $"wymcmd-broken-{Guid.NewGuid():n}.json");
        File.WriteAllText(broken, "{ not json");
        try
        {
            Assert.Empty(RuleSet.Load(broken).Rules);
        }
        finally
        {
            File.Delete(broken);
        }
    }
}
