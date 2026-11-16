using Wymcmd.Core.Localization;
using Wymcmd.Core.Store;

namespace Wymcmd.Cli.Commands;

public static class Prune
{
    public static int Run(CliOptions options)
    {
        var settings = Settings.Load();
        var changed = false;

        if (options.Value("--days") is not null && options.Number("--days", 0) is var parsedDays)
        {
            settings.RetentionDays = Math.Max(0, parsedDays);
            changed = true;
        }

        if (options.Value("--max-mb") is not null && options.Number("--max-mb", 0) is var parsedSize)
        {
            settings.MaxDatabaseMb = Math.Max(0, parsedSize);
            changed = true;
        }

        if (changed) settings.Save();

        using var store = new EventStore();
        var before = store.Bounds();
        var result = Maintenance.Run(store, settings);
        var after = store.Bounds();

        if (options.Json)
        {
            ConsoleHost.Line(System.Text.Json.JsonSerializer.Serialize(new
            {
                retentionDays = settings.RetentionDays,
                maxDatabaseMb = settings.MaxDatabaseMb,
                removed = result.Removed,
                remaining = after.Count,
                reclaimedKb = result.Reclaimed / 1024
            }));
            return CommandRouter.ExitOk;
        }

        ConsoleHost.Line(Loc.T("prune.policy",
            settings.RetentionDays == 0 ? Loc.T("prune.forever") : settings.RetentionDays.ToString(),
            settings.MaxDatabaseMb));

        if (result.Removed == 0)
        {
            ConsoleHost.Dim(Loc.T("prune.nothing", before.Count));
            return CommandRouter.ExitOk;
        }

        ConsoleHost.Good(Loc.T("prune.done",
            result.Removed,
            after.Count,
            result.Reclaimed / 1024));

        return CommandRouter.ExitOk;
    }
}
