namespace Wymcmd.Core.Store;

/// <summary>
/// Machine-wide data lives in ProgramData so the service and the UI see the same database.
/// When that is not writable (portable use, locked-down box) everything drops to LocalAppData.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> RootPath = new(ResolveRoot);

    public static string Root => RootPath.Value;
    public static string Database => Path.Combine(Root, "events.db");
    public static string Rules => Path.Combine(Root, "rules.json");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "wymcmd.log");
    public static string BlackBoxTrace => Path.Combine(Root, "blackbox.etl");

    /// <summary>The second black box: a system trace session, which carries command lines.</summary>
    public static string BlackBoxSystemTrace => Path.Combine(Root, "blackbox-system.etl");
    public static string AutostartBaseline => Path.Combine(Root, "autostart-baseline.json");
    public static string ExportDirectory => Path.Combine(Root, "exports");

    public const string PipeName = "wymcmd";
    public const string BlackBoxSessionName = "WymcmdBlackBox";
    public const string BlackBoxSystemSessionName = "WymcmdBlackBoxSystem";
    public const string LiveSessionName = "WymcmdLive";

    /// <summary>Point everything at a folder of your choosing - a stick, a case folder, a sandbox.</summary>
    public const string HomeVariable = "WYMCMD_HOME";

    private static string ResolveRoot()
    {
        // An explicit home wins over everything, so the tool can be pointed at a USB stick or a
        // folder for one investigation without touching the machine's own data.
        if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home && TryPrepare(home))
            return home;

        // Only use the machine-wide folder when it already exists and lets us write:
        // creating it properly (with an ACE for Users) is an explicit, elevated setup step.
        var shared = SharedRoot.Path;
        if (Directory.Exists(shared) && TryPrepare(shared)) return shared;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wymcmd");
        Directory.CreateDirectory(local);
        return local;
    }

    private static bool TryPrepare(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
