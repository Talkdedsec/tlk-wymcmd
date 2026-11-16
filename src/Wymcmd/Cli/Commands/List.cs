using Wymcmd.Core.Localization;
using Wymcmd.Core.Store;

namespace Wymcmd.Cli.Commands;

public static class List
{
    public static int Run(CliOptions options)
    {
        var filter = new EventFilter
        {
            From = DateTime.Now - ParseSpan(options.Value("--last") ?? "24h"),
            ConsoleOnly = options.Has("--console"),
            HiddenOnly = options.Has("--hidden"),
            UnsignedOnly = options.Has("--unsigned"),
            MinRisk = options.Number("--risk", 0),
            Text = options.Value("--text"),
            Limit = options.Number("--limit", 100)
        };

        using var store = new EventStore();
        var events = store.Query(filter);

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

        foreach (var evt in events.Reverse())
            ConsoleHost.Line(EventFormatter.OneLine(evt));

        ConsoleHost.Line();
        ConsoleHost.Dim(Loc.T("list.count", events.Count));
        return CommandRouter.ExitOk;
    }

    /// <summary>
    /// Accepts 30m, 6h, 3d, 90s - the shapes people actually type - and a bare number as hours.
    /// Anything else is refused: "--last 7x" quietly meaning a day hides the mistake.
    /// </summary>
    public static TimeSpan ParseSpan(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());

        if (digits.Length > 0 && double.TryParse(digits, out var amount))
        {
            switch (text[digits.Length..])
            {
                case "s": return TimeSpan.FromSeconds(amount);
                case "m": return TimeSpan.FromMinutes(amount);
                case "h" or "": return TimeSpan.FromHours(amount);
                case "d": return TimeSpan.FromDays(amount);
            }
        }

        throw new ArgumentException(Loc.T("cli.error.bad_argument", "duration", value));
    }
}
