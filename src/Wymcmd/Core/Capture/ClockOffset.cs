namespace Wymcmd.Core.Capture;

/// <summary>
/// How far the trace session's clock is from the system's. A session takes the time once, when
/// it opens, and counts from there on its own; Windows keeps correcting the system clock. Over
/// the weeks a service runs the two walk apart by seconds, and a kill that compares a launch's
/// start time with what Windows reports would refuse every one as "another process".
///
/// Each launch that is still running gives one sample: the start time Windows reports, minus the
/// one the trace gave. The offset follows those samples slowly and is added to every time the
/// trace hands over, so starts, exits and late-handled launches all sit on one clock.
/// </summary>
internal sealed class ClockOffset
{
    /// <summary>Further than this from the current offset is not drift - it is a sample from the wrong process.</summary>
    private static readonly TimeSpan Near = TimeSpan.FromMilliseconds(500);

    /// <summary>Drift is parts per million. No single sample needs to move the offset further than this.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(20);

    /// <summary>This many samples in a row that agree with each other: the system clock was set.</summary>
    private const int Agreement = 5;

    private TimeSpan _candidate;
    private int _agreeing;

    public TimeSpan Value { get; private set; }

    public void Sample(TimeSpan measured)
    {
        var off = measured - Value;

        if (off.Duration() <= Near)
        {
            // A pid handed to a new process within half a second gives a sample that is close but
            // wrong; moving a step at a time keeps one of those from mattering.
            Value += off > Step ? Step : off < -Step ? -Step : off;
            _agreeing = 0;
            return;
        }

        // Far off. One of these is a reused pid. Several that agree are the clock itself.
        if (_agreeing > 0 && (measured - _candidate).Duration() <= Near) _agreeing++;
        else _agreeing = 1;

        _candidate = measured;

        if (_agreeing >= Agreement)
        {
            Value = measured;
            _agreeing = 0;
        }
    }
}
