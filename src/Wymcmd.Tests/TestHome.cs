using System.Runtime.CompilerServices;
using Wymcmd.Core.Store;

namespace Wymcmd.Tests;

/// <summary>
/// The suite gets its own folder before anything can look at the machine's.
///
/// Without this the tests wrote into the real log and the real database: a test that deliberately
/// feeds the rule loader a broken file left "rules file unreadable" in the user's log, which is
/// the kind of thing somebody later spends an afternoon chasing.
///
/// A module initializer runs before the first test touches any type, which matters because the
/// resolved home is cached the first time it is asked for.
/// </summary>
internal static class TestHome
{
    [ModuleInitializer]
    internal static void Use()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wymcmd-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);

        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, folder);
    }
}
