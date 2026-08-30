using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Xunit;

namespace Wymcmd.Tests;

public class RuleSetRoundTripTests
{
    /// <summary>
    /// Whatever the tool writes, it has to be able to read back. This failed in the field: every
    /// run logged "rules file unreadable, starting empty", which silently turns every rule off.
    /// </summary>
    [Fact]
    public void A_saved_rule_set_loads_again()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wymcmd-rules-{Guid.NewGuid():N}.json");

        try
        {
            var saved = new RuleSet();
            saved.Rules.Add(new Rule { Image = "cmd.exe", Action = RuleAction.Log });
            saved.Save(file);

            var loaded = RuleSet.Load(file);

            Assert.Single(loaded.Rules);
            Assert.Equal("cmd.exe", loaded.Rules[0].Image);
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
    }

    /// <summary>The exact file that was on disk when the field logs said it was unreadable.</summary>
    [Fact]
    public void The_file_that_was_reported_unreadable_loads()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wymcmd-rules-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(file, "{\r\n  \"Rules\": [],\r\n  \"Ordered\": []\r\n}");

            var loaded = RuleSet.Load(file);

            Assert.Empty(loaded.Rules);
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
    }

    [Fact]
    public void An_empty_rule_set_survives_the_same_trip()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wymcmd-rules-{Guid.NewGuid():N}.json");

        try
        {
            new RuleSet().Save(file);

            var text = File.ReadAllText(file);
            var loaded = RuleSet.Load(file);

            Assert.Empty(loaded.Rules);

            // The computed view of the rules must not be written to the file: it is a second copy
            // of the same data, and reading it back is what broke.
            Assert.DoesNotContain("Ordered", text, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
    }
}
