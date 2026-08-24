using Wymcmd.Core.Model;
using Wymcmd.Core.Why;
using Wymcmd.Core.Windows;
using Xunit;

namespace Wymcmd.Tests;

public class RiskScorerTests
{
    private static ProcEvent Event(string image = "cmd.exe", string path = "C:\\Windows\\System32\\cmd.exe")
        => new()
        {
            Pid = 10,
            StartTime = DateTime.Now,
            ImageName = image,
            ImagePath = path,
            CommandLine = image,
            Signature = new SignatureInfo { Status = SignatureStatus.Valid, Publisher = "Microsoft Windows" },
            Source = new LaunchSource { Kind = LaunchSourceKind.UserShell },
            Window = WindowVisibility.Visible
        };

    [Fact]
    public void A_signed_visible_attributed_console_scores_zero()
    {
        var evt = Event();

        RiskScorer.Score(evt, CommandTraits.None);

        Assert.Equal(0, evt.Risk);
        Assert.Empty(evt.RiskFactors);
    }

    [Fact]
    public void An_unsigned_hidden_console_from_temp_scores_high_and_says_why()
    {
        var evt = Event(path: "C:\\Users\\me\\AppData\\Local\\Temp\\thing.exe");
        evt.Signature = new SignatureInfo { Status = SignatureStatus.Unsigned };
        evt.Window = WindowVisibility.Hidden;
        evt.Source = null;

        RiskScorer.Score(evt, CommandTraits.Encoded);

        Assert.True(evt.Risk >= 70);
        Assert.Contains(evt.RiskFactors, factor => factor.Key == "unsigned");
        Assert.Contains(evt.RiskFactors, factor => factor.Key == "hidden_window");
        Assert.Contains(evt.RiskFactors, factor => factor.Key == "temp_path");
        Assert.Contains(evt.RiskFactors, factor => factor.Key == "encoded_command");
        Assert.Contains(evt.RiskFactors, factor => factor.Key == "unknown_source");
    }

    [Fact]
    public void The_score_never_passes_one_hundred()
    {
        var evt = Event(path: "C:\\Users\\me\\Downloads\\thing.exe");
        evt.Signature = new SignatureInfo { Status = SignatureStatus.Unsigned };
        evt.Window = WindowVisibility.Hidden;
        evt.Elevated = true;
        evt.Source = null;
        evt.ExitTime = evt.StartTime.AddMilliseconds(40);

        RiskScorer.Score(evt, CommandTraits.Encoded | CommandTraits.DownloadsContent | CommandTraits.LivingOffTheLand);

        Assert.Equal(100, evt.Risk);
    }

    [Fact]
    public void Scoring_twice_does_not_double_the_reasons()
    {
        var evt = Event();
        evt.Signature = new SignatureInfo { Status = SignatureStatus.Unsigned };

        RiskScorer.Score(evt, CommandTraits.None);
        RiskScorer.Score(evt, CommandTraits.None);

        Assert.Single(evt.RiskFactors, factor => factor.Key == "unsigned");
    }
}

public class PathNamesTests
{
    [Theory]
    [InlineData(@"\Device\HarddiskVolume4\Windows\System32\cmd.exe", @"Windows\System32\cmd.exe")]
    [InlineData(@"\SystemRoot\System32\smss.exe", @"System32\smss.exe")]
    [InlineData(@"\??\C:\Windows\System32\conhost.exe", @"C:\Windows\System32\conhost.exe")]
    public void Kernel_spellings_become_paths_a_person_can_read(string input, string endsWith)
    {
        var result = PathNames.Normalize(input);

        Assert.EndsWith(endsWith, result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\Device\", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\SystemRoot\", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_ordinary_path_is_left_alone()
    {
        Assert.Equal(@"C:\Program Files\App\a.exe", PathNames.Normalize(@"C:\Program Files\App\a.exe"));
    }

    [Fact]
    public void Nothing_in_nothing_out()
    {
        Assert.Equal("", PathNames.Normalize(null));
        Assert.Equal("", PathNames.Normalize("   "));
        Assert.Equal("", PathNames.FileName(null));
    }

    [Fact]
    public void Reads_the_file_name_out_of_a_kernel_path()
    {
        Assert.Equal("cmd.exe", PathNames.FileName(@"\Device\HarddiskVolume4\Windows\System32\cmd.exe"));
    }
}
