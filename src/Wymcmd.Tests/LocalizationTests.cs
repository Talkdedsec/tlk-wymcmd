using Wymcmd.Cli.Commands;
using Wymcmd.Core.Localization;
using Xunit;

namespace Wymcmd.Tests;

[Collection("language")]
public class LocalizationTests
{
    [Fact]
    public void Both_languages_answer_for_the_same_key()
    {
        Loc.Use("en");
        var english = Loc.T("app.subtitle");

        Loc.Use("tr");
        var turkish = Loc.T("app.subtitle");

        Assert.NotEqual(english, turkish);
        Assert.NotEqual("app.subtitle", english);
        Assert.NotEqual("app.subtitle", turkish);
    }

    [Fact]
    public void Turkish_keeps_its_letters()
    {
        Loc.Use("tr");

        Assert.Contains("ç", Loc.T("app.subtitle"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_comes_back_as_itself_rather_than_blank()
    {
        Loc.Use("en");

        Assert.Equal("nothing.like.this", Loc.T("nothing.like.this"));
    }

    [Fact]
    public void Placeholders_are_filled_in()
    {
        Loc.Use("en");

        Assert.Contains("42", Loc.T("kill.done", 42, 1));
    }

    [Fact]
    public void A_template_with_the_wrong_arguments_still_returns_text()
    {
        Loc.Use("en");

        var text = Loc.T("kill.done");

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void Relative_time_picks_singular_and_plural()
    {
        Loc.Use("en");

        Assert.Contains("1 hour ago", Loc.Ago(DateTime.Now.AddHours(-1)));
        Assert.Contains("3 hours ago", Loc.Ago(DateTime.Now.AddHours(-3)));
        Assert.Contains("ago", Loc.Ago(DateTime.Now.AddSeconds(-5)));
    }

    [Fact]
    public void Durations_read_in_the_right_unit()
    {
        Loc.Use("en");

        Assert.Contains("ms", Loc.Duration(TimeSpan.FromMilliseconds(42)));
        Assert.Contains("s", Loc.Duration(TimeSpan.FromSeconds(3)));
        Assert.Contains("m", Loc.Duration(TimeSpan.FromMinutes(2)));
        Assert.Contains("h", Loc.Duration(TimeSpan.FromHours(5)));
    }

    [Fact]
    public void An_unknown_language_falls_back_to_english()
    {
        Loc.Use("de");

        Assert.Equal("en", Loc.Language);
    }
}

[Collection("language")]
public class ArgumentParsingTests
{
    [Theory]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("1d", 1440)]
    [InlineData("90s", 1.5)]
    [InlineData("nonsense", 24 * 60)]
    public void Time_spans_accept_the_shapes_people_type(string input, double expectedMinutes)
    {
        Assert.Equal(expectedMinutes, List.ParseSpan(input).TotalMinutes, 3);
    }

    [Fact]
    public void A_bare_clock_time_means_today_or_yesterday_but_never_the_future()
    {
        var moment = Timeline.ParseMoment("23:59");

        Assert.NotNull(moment);
        Assert.True(moment <= DateTime.Now.AddMinutes(1));
    }

    [Fact]
    public void Now_and_a_full_date_both_parse()
    {
        Assert.NotNull(Timeline.ParseMoment("now"));
        Assert.NotNull(Timeline.ParseMoment(null));
        Assert.Equal(new DateTime(2026, 8, 24, 14, 22, 0), Timeline.ParseMoment("2026-08-24 14:22"));
    }

    [Fact]
    public void Gibberish_is_rejected_instead_of_guessed()
    {
        Assert.Null(Timeline.ParseMoment("half past something"));
    }

    [Fact]
    public void Options_read_flags_values_and_positionals()
    {
        var options = new Wymcmd.Cli.CliOptions(["list", "--last", "6h", "--json", "--lang", "tr"]);

        Assert.True(options.Json);
        Assert.Equal("6h", options.Value("--last"));
        Assert.Equal("list", options.Command());
        Assert.Equal(7, options.Number("--missing", 7));
    }

    [Fact]
    public void A_global_flag_value_is_not_mistaken_for_a_command()
    {
        // "wymcmd --lang en" opens the window; "en" is a value, not a command.
        Assert.Null(new Wymcmd.Cli.CliOptions(["--lang", "en"]).Command());
    }
}

[CollectionDefinition("language", DisableParallelization = true)]
public class LanguageCollection;
