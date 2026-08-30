using Wymcmd.Core.Model;
using Wymcmd.Core.Why;
using Xunit;

namespace Wymcmd.Tests;

public class AutostartIndexTests
{
    private static AutostartIndex Built()
    {
        var index = new AutostartIndex();
        index.Rebuild();
        return index;
    }

    /// <summary>
    /// Winlogon is on every Windows install and its Shell value reads explorer.exe, so finding it
    /// proves the scan actually ran rather than swallowing an exception and reporting nothing.
    /// </summary>
    [Fact]
    public void The_winlogon_shell_is_found_on_any_windows_machine()
    {
        var entries = Built().Entries.Where(e => e.Kind == LaunchSourceKind.WinlogonHook).ToList();

        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => (e.TargetImage ?? "").Contains("explorer.exe", StringComparison.OrdinalIgnoreCase)
                                      || (e.TargetImage ?? "").Contains("userinit.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Active_setup_is_scanned_and_its_entries_carry_where_they_came_from()
    {
        foreach (var entry in Built().Entries.Where(e => e.Kind == LaunchSourceKind.ActiveSetup))
        {
            Assert.Contains("Active Setup", entry.Location, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(entry.Command));
        }
    }

    [Fact]
    public void Every_entry_says_which_hive_or_folder_it_came_from()
    {
        Assert.All(Built().Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Location)));
    }

    /// <summary>The index is asked for on the UI thread when a launch needs explaining.</summary>
    [Fact]
    public void Rebuilding_the_whole_index_stays_quick()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Built();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"rebuild took {clock.Elapsed}");
    }
}
