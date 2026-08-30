using Wymcmd.Core.Store;
using Xunit;

namespace Wymcmd.Tests;

public class AppPathsTests
{
    /// <summary>
    /// The suite sets WYMCMD_HOME before anything resolves a path. If that stopped working the
    /// tests would go back to writing into the machine's own log and database, and a test that
    /// deliberately breaks a rules file would leave its error in the user's log.
    /// </summary>
    [Fact]
    public void An_explicit_home_is_where_everything_goes()
    {
        var home = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);

        Assert.False(string.IsNullOrWhiteSpace(home));
        Assert.Equal(home, AppPaths.Root);
    }

    [Fact]
    public void The_tests_are_not_pointed_at_the_machines_own_folder()
    {
        Assert.DoesNotContain("ProgramData", AppPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wymcmd-tests", AppPaths.Root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_file_the_tool_keeps_sits_under_that_home()
    {
        foreach (var path in new[] { AppPaths.Database, AppPaths.Rules, AppPaths.Settings, AppPaths.LogFile, AppPaths.BlackBoxTrace })
            Assert.StartsWith(AppPaths.Root, path, StringComparison.OrdinalIgnoreCase);
    }
}
