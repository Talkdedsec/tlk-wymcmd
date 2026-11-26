using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Rules;

/// <summary>
/// Rules used to be read once, when capture started. A rule added while the service was running
/// was saved, listed, and enforced by nobody until the next restart. This looks at the file every
/// few seconds and swaps the new rules into the set the engine is already using.
/// </summary>
public sealed class RuleWatcher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly RuleSet _live;
    private readonly string _path;
    private readonly Func<RuleSet> _load;
    private readonly System.Threading.Timer _timer;
    private DateTime _seen;

    /// <param name="live">The set the capture engine holds. It is updated in place.</param>
    /// <param name="load">How to read the file - the service only accepts one administrators wrote.</param>
    public RuleWatcher(RuleSet live, string path, Func<RuleSet> load)
    {
        _live = live;
        _path = path;
        _load = load;
        _seen = Stamp();
        _timer = new System.Threading.Timer(_ => Check(), null, Interval, Interval);
    }

    private DateTime Stamp() => File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;

    internal void Check()
    {
        try
        {
            var stamp = Stamp();
            if (stamp == _seen) return;
            _seen = stamp;

            // Somebody is halfway through an edit, or made a typo. Loading would hand back an
            // empty set and the service would go on with no rules; keep the ones it has instead.
            if (File.Exists(_path)) RuleSet.Parse(File.ReadAllText(_path));

            var fresh = _load();

            // The engine takes the same lock while it matches, so a launch is judged by the old
            // rules or the new ones, never by half of each.
            lock (_live) _live.Rules = fresh.Rules;

            Log.Info($"rules reloaded: {fresh.Rules.Count}");
        }
        catch (Exception ex)
        {
            Log.Warn("rules file changed but could not be read; keeping the rules already loaded: " + ex.Message);
        }
    }

    public void Dispose() => _timer.Dispose();
}
