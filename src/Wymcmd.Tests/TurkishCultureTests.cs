using System.Globalization;
using Wymcmd.Core.Model;
using Wymcmd.Core.Rules;
using Wymcmd.Core.Why;
using Xunit;

namespace Wymcmd.Tests;

/// <summary>
/// On a Turkish system "I" lower-cases to a dotless "ı", so culture-aware case folding stops
/// "INVOKE" from matching "invoke". File names and command lines are not Turkish text; every
/// case-insensitive match on them has to ignore the machine's language.
/// </summary>
public sealed class TurkishCultureTests : IDisposable
{
    private readonly CultureInfo _before = CultureInfo.CurrentCulture;

    public TurkishCultureTests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

    public void Dispose() => CultureInfo.CurrentCulture = _before;

    private static ProcEvent Launch(string image, string commandLine) => new()
    {
        Pid = 1,
        ImageName = image,
        ImagePath = $"C:\\Tools\\{image}",
        CommandLine = commandLine
    };

    [Fact]
    public void A_command_line_pattern_matches_whatever_the_case()
    {
        var rule = new Rule { CommandLine = "invoke-webrequest" };

        Assert.True(rule.Matches(Launch("powershell.exe", "powershell INVOKE-WEBREQUEST http://x")));
    }

    [Fact]
    public void An_image_glob_matches_whatever_the_case()
    {
        var rule = new Rule { Image = "*install*.exe" };

        Assert.True(rule.Matches(Launch("INSTALLER.EXE", "INSTALLER.EXE /S")));
    }

    [Fact]
    public void An_upper_case_hidden_window_switch_is_still_seen()
    {
        var decoded = CommandLineDecoder.Decode("powershell.exe", "powershell -WINDOWSTYLE HIDDEN -NOPROFILE -FILE C:\\X.PS1");

        Assert.True(decoded.Traits.HasFlag(CommandTraits.HiddenWindow));
    }

    [Fact]
    public void An_upper_case_download_is_still_seen()
    {
        var decoded = CommandLineDecoder.Decode("powershell.exe", "powershell INVOKE-WEBREQUEST X; BITSADMIN /TRANSFER");

        Assert.True(decoded.Traits.HasFlag(CommandTraits.DownloadsContent));
    }
}
