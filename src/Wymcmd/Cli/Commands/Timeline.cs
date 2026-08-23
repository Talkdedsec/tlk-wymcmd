using System.Globalization;
using Wymcmd.Core.Forensic;
using Wymcmd.Core.Localization;
using Wymcmd.Core.Store;

namespace Wymcmd.Cli.Commands;

public static class Timeline
{
    public static int Run(CliOptions options)
    {
        var positional = options.Positional("--radius", "--limit");
        var moment = ParseMoment(positional.FirstOrDefault());
        if (moment is null)
        {
            ConsoleHost.Bad(Loc.T("cli.error.bad_argument", "time", positional.FirstOrDefault() ?? ""));
            return CommandRouter.ExitError;
        }

        var radius = List.ParseSpan(options.Value("--radius") ?? "60s");

        using var store = new EventStore();
        var harvester = new ForensicHarvester(store);
        var events = harvester.Around(moment.Value, radius);

        if (options.Has("--console"))
            events = events.Where(e => e.IsConsoleHost).ToList();

        if (events.Count == 0)
        {
            ConsoleHost.Dim(Loc.T("cli.error.not_found"));
            return CommandRouter.ExitNotFound;
        }

        if (options.Json)
        {
            foreach (var evt in events) ConsoleHost.Line(EventFormatter.Json(evt));
            return CommandRouter.ExitOk;
        }

        ConsoleHost.Strong(Loc.T("timeline.header",
            moment.Value.ToString("g", Loc.Culture),
            Loc.Duration(radius)));
        ConsoleHost.Line();

        foreach (var evt in events)
        {
            var marker = Math.Abs((evt.StartTime - moment.Value).TotalSeconds) < 2 ? ">" : " ";
            ConsoleHost.Line($"{marker} {EventFormatter.OneLine(evt)}");
        }

        ConsoleHost.Line();
        ConsoleHost.Dim(Loc.T("list.count", events.Count));
        return CommandRouter.ExitOk;
    }

    /// <summary>Accepts "14:22", "14:22:05", a full date, or "now".</summary>
    public static DateTime? ParseMoment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("now", StringComparison.OrdinalIgnoreCase))
            return DateTime.Now;

        value = value.Trim();

        if (TimeSpan.TryParseExact(value, ["h\\:mm", "hh\\:mm", "h\\:mm\\:ss", "hh\\:mm\\:ss"],
                CultureInfo.InvariantCulture, out var timeOfDay))
        {
            var today = DateTime.Today + timeOfDay;
            // A time in the future means the user meant yesterday.
            return today > DateTime.Now.AddMinutes(1) ? today.AddDays(-1) : today;
        }

        if (DateTime.TryParse(value, Loc.Culture, DateTimeStyles.None, out var full)) return full;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant)) return invariant;

        return null;
    }
}
