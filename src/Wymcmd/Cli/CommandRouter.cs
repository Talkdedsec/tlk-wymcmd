using Wymcmd.Core.Localization;

namespace Wymcmd.Cli;

public sealed record CliOptions(string[] Args)
{
    public bool Json => Has("--json");
    public bool Help => Has("--help") || Has("-h") || Has("/?");

    public bool Has(string flag) => Args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    public string? Value(string flag)
    {
        for (var i = 0; i < Args.Length - 1; i++)
            if (string.Equals(Args[i], flag, StringComparison.OrdinalIgnoreCase))
                return Args[i + 1];
        return null;
    }

    public int Number(string flag, int fallback)
        => int.TryParse(Value(flag), out var value) ? value : fallback;

    /// <summary>
    /// The command word, if the caller gave one. Values that belong to a global flag
    /// ("--lang en") are not commands, which is what tells "wymcmd --lang en" to open the window.
    /// </summary>
    public string? Command()
    {
        for (var i = 0; i < Args.Length; i++)
        {
            if (GlobalValueFlags.Contains(Args[i], StringComparer.OrdinalIgnoreCase)) { i++; continue; }
            if (Args[i].StartsWith('-')) continue;
            return Args[i];
        }
        return null;
    }

    private static readonly string[] GlobalValueFlags = ["--lang"];

    /// <summary>Positional arguments, flags and their values removed.</summary>
    public string[] Positional(params string[] flagsWithValues)
    {
        var result = new List<string>();
        for (var i = 0; i < Args.Length; i++)
        {
            var arg = Args[i];
            if (flagsWithValues.Contains(arg, StringComparer.OrdinalIgnoreCase)) { i++; continue; }
            if (arg.StartsWith('-')) continue;
            result.Add(arg);
        }
        return [.. result];
    }
}

public static class CommandRouter
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitNeedsAdmin = 2;
    public const int ExitSourceDisabled = 3;
    public const int ExitNotFound = 4;

    public static async Task<int> RunAsync(string[] args)
    {
        var options = new CliOptions(args);
        Loc.Use(options.Value("--lang") ?? Loc.DetectSystemLanguage());

        var command = options.Command()?.ToLowerInvariant() ?? "help";
        var rest = new CliOptions(args.SkipWhile(a => !a.Equals(command, StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray());

        if (options.Has("--version") && command is "help" or "") return Version();
        if (options.Help && command is "help" or "") { Usage(); return ExitOk; }

        return command switch
        {
            "help" => Usage(),
            "version" or "--version" => Version(),
            "doctor" => Commands.Doctor.Run(rest),
            "list" => Commands.List.Run(rest),
            "timeline" => Commands.Timeline.Run(rest),
            "watch" => await Commands.Watch.RunAsync(rest),
            "trap" => await Commands.Trap.RunAsync(rest),
            "rules" => Commands.Rules.Run(rest),
            "export" => Commands.Export.Run(rest),
            "tree" => Commands.Tree.Run(rest),
            "kill" => Commands.Kill.Run(rest),
            "why" => Commands.WhyCommand.Run(rest),
            "sources" => Commands.Sources.Run(rest),
            "blackbox" => Commands.BlackBox.Run(rest),
            "service" => Commands.Service.Run(rest),
            "uninstall" => Commands.Uninstall.Run(rest),
            _ => Unknown(command)
        };
    }

    private static int Usage()
    {
        ConsoleHost.Strong(Loc.T("cli.usage_header"));
        ConsoleHost.Dim(Loc.T("app.subtitle"));
        ConsoleHost.Line();
        ConsoleHost.Line(Loc.T("cli.usage_line"));
        ConsoleHost.Line();
        ConsoleHost.Strong(Loc.T("cli.commands_header"));

        foreach (var name in new[] { "why", "timeline", "list", "watch", "trap", "tree", "kill", "rules", "blackbox", "sources", "export", "service", "doctor", "uninstall" })
            ConsoleHost.Line($"  {name,-10} {Loc.T("cli.cmd." + name)}");

        ConsoleHost.Line();
        ConsoleHost.Strong(Loc.T("cli.options_header"));
        ConsoleHost.Line($"  {"--lang",-10} {Loc.T("cli.opt.lang")}");
        ConsoleHost.Line($"  {"--json",-10} {Loc.T("cli.opt.json")}");
        ConsoleHost.Line($"  {"--help",-10} {Loc.T("cli.opt.help")}");
        return ExitOk;
    }

    private static int Version()
    {
        var version = typeof(CommandRouter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        ConsoleHost.Line($"wymcmd {version}");
        return ExitOk;
    }

    private static int Unknown(string command)
    {
        ConsoleHost.Bad(Loc.T("cli.error.unknown_command", command));
        Usage();
        return ExitError;
    }
}
