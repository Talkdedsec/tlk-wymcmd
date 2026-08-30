using Wymcmd.Core.Model;
using Wymcmd.Core.Why;
using Xunit;

namespace Wymcmd.Tests;

public class AttackMapTests
{
    private static ProcEvent Event(
        string image = "cmd.exe",
        string commandLine = "cmd.exe",
        LaunchSourceKind kind = LaunchSourceKind.UserShell)
        => new()
        {
            Pid = 10,
            StartTime = DateTime.Now,
            ImageName = image,
            ImagePath = @"C:\Windows\System32\" + image,
            CommandLine = commandLine,
            Source = new LaunchSource { Kind = kind }
        };

    [Fact]
    public void A_scheduled_task_is_named_as_one()
    {
        var techniques = AttackMap.For(Event(kind: LaunchSourceKind.ScheduledTask));

        Assert.Contains(techniques, t => t.Id == "T1053.005");
    }

    [Fact]
    public void The_interpreter_that_ran_is_named_from_the_image_not_the_arguments()
    {
        Assert.Contains(AttackMap.For(Event("powershell.exe")), t => t.Id == "T1059.001");
        Assert.Contains(AttackMap.For(Event("cmd.exe")), t => t.Id == "T1059.003");
        Assert.Contains(AttackMap.For(Event("rundll32.exe")), t => t.Id == "T1218.011");
    }

    /// <summary>The trait has to be established by the decoder first - nothing here guesses.</summary>
    [Fact]
    public void An_encoded_command_is_labelled_as_obfuscated()
    {
        var evt = Event("powershell.exe",
            "powershell -nop -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA");

        var techniques = AttackMap.For(evt);

        Assert.Contains(techniques, t => t.Id == "T1027");
        Assert.Contains(techniques, t => t.Id == "T1564.003");
    }

    [Fact]
    public void An_ordinary_launch_from_a_shell_gets_only_the_interpreter()
    {
        var techniques = AttackMap.For(Event("cmd.exe", "cmd.exe"));

        Assert.Single(techniques);
        Assert.Equal("T1059.003", techniques[0].Id);
    }

    [Fact]
    public void A_launch_with_nothing_to_say_about_it_is_left_alone()
    {
        Assert.Empty(AttackMap.For(Event("setup.exe", "setup.exe /quiet")));
    }

    [Fact]
    public void The_same_technique_is_not_listed_twice()
    {
        var evt = Event("cmd.exe", "cmd.exe", LaunchSourceKind.StartupFolder);
        evt.Source = new LaunchSource { Kind = LaunchSourceKind.RunKey };

        var techniques = AttackMap.For(evt);

        Assert.Equal(techniques.Select(t => t.Id).Distinct().Count(), techniques.Count);
    }

    [Fact]
    public void A_sub_technique_links_under_its_parent()
    {
        var technique = AttackMap.For(Event(kind: LaunchSourceKind.Service)).First(t => t.Id == "T1543.003");

        Assert.Equal("https://attack.mitre.org/techniques/T1543/003/", technique.Url);
    }
}
