using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Store;

public sealed record MaintenanceResult(int RemovedByAge, int RemovedBySize, long BeforeBytes, long AfterBytes)
{
    public int Removed => RemovedByAge + RemovedBySize;
    public long Reclaimed => Math.Max(0, BeforeBytes - AfterBytes);
}

/// <summary>
/// A recorder that never forgets eventually fills the disk. Age comes first, then a hard size
/// ceiling that drops the oldest events until the file fits again.
/// </summary>
public static class Maintenance
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(6);
    private static DateTime _lastRun = DateTime.MinValue;
    private static readonly Lock Sync = new();

    public static MaintenanceResult Run(EventStore store, Settings? settings = null)
    {
        settings ??= Settings.Current;

        var before = FileSize(store);
        var byAge = settings.RetentionDays > 0
            ? store.Prune(TimeSpan.FromDays(settings.RetentionDays))
            : 0;

        var bySize = 0;
        if (settings.MaxDatabaseMb > 0)
        {
            var ceiling = (long)settings.MaxDatabaseMb * 1024 * 1024;

            // Each pass drops the oldest tenth; a file well over the ceiling needs a few rounds.
            for (var pass = 0; pass < 8 && FileSize(store) > ceiling; pass++)
            {
                var removed = store.PruneOldest(0.10);
                if (removed == 0) break;
                bySize += removed;
            }
        }

        var after = FileSize(store);
        if (byAge + bySize > 0)
            Log.Info($"maintenance removed {byAge + bySize} events, database {before / 1024} KB -> {after / 1024} KB");

        lock (Sync) _lastRun = DateTime.Now;
        return new MaintenanceResult(byAge, bySize, before, after);
    }

    /// <summary>Runs at most every few hours, in the background, and never throws at the caller.</summary>
    public static void RunInBackground(EventStore store)
    {
        lock (Sync)
        {
            if (DateTime.Now - _lastRun < MinimumInterval) return;
            _lastRun = DateTime.Now;
        }

        Task.Run(() =>
        {
            try
            {
                Run(store);
            }
            catch (Exception ex)
            {
                Log.Warn("maintenance failed: " + ex.Message);
            }
        });
    }

    private static long FileSize(EventStore store)
        => File.Exists(store.DatabasePath) ? new FileInfo(store.DatabasePath).Length : 0;
}
