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

    /// <summary>
    /// Windows ships an Active Setup entry whose command is the single letter "U". Matching by
    /// containment credited it with every launch that had a u anywhere in its command line.
    /// </summary>
    [Fact]
    public void A_one_letter_command_does_not_claim_every_launch()
    {
        var index = new AutostartIndex();
        index.Seed(
        [
            new AutostartEntry(LaunchSourceKind.ActiveSetup, "Windows Desktop Update", "Active Setup", "U", null),
            new AutostartEntry(LaunchSourceKind.ActiveSetup, "Themes Setup", "Active Setup", "/UserInstall", null)
        ]);

        Assert.Null(index.Match(@"C:\Windows\System32\cmd.exe", "cmd.exe /c ping -n 5 127.0.0.1 >nul"));
        Assert.Null(index.Match(@"C:\Windows\System32\rundll32.exe", "rundll32.exe /UserInstall"));
    }

    [Fact]
    public void A_registered_command_still_claims_the_launch_it_describes()
    {
        var index = new AutostartIndex();
        index.Seed(
        [
            new AutostartEntry(LaunchSourceKind.ScheduledTask, @"\nightly", "Task Scheduler",
                @"wscript.exe //B C:\Jobs\nightly.vbs", "wscript.exe")
        ]);

        var match = index.Match(@"C:\Windows\System32\wscript.exe", @"""C:\Windows\System32\wscript.exe"" //B C:\Jobs\nightly.vbs");

        Assert.Equal(@"\nightly", match?.Name);
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
