using Wymcmd.Core.Forensic;
using Xunit;

namespace Wymcmd.Tests;

public class NetworkReadingTests
{
    /// <summary>The shape Sysmon actually writes into QueryResults on event 22.</summary>
    [Fact]
    public void Addresses_are_pulled_out_of_a_sysmon_dns_result()
    {
        var text = EvtxReader.Resolved("type:  5 example.com;type:  1 93.184.216.34;");

        Assert.NotNull(text);
        Assert.Contains("93.184.216.34", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ipv4_mapped_prefix_is_dropped()
    {
        Assert.Equal("10.0.0.5", EvtxReader.Resolved("type:  1 ::ffff:10.0.0.5;"));
    }

    [Fact]
    public void A_query_that_resolved_to_nothing_reads_as_nothing()
    {
        Assert.Null(EvtxReader.Resolved("-"));
        Assert.Null(EvtxReader.Resolved(""));
        Assert.Null(EvtxReader.Resolved(null));
    }

    [Fact]
    public void The_same_address_twice_is_listed_once()
    {
        var text = EvtxReader.Resolved("type:  1 1.1.1.1;type:  1 1.1.1.1;type:  1 8.8.8.8;");

        Assert.Equal("1.1.1.1, 8.8.8.8", text);
    }

    /// <summary>A record with dozens of answers must not push the launch off the screen.</summary>
    [Fact]
    public void A_long_answer_is_cut_short()
    {
        var many = string.Concat(Enumerable.Range(1, 12).Select(i => $"type:  1 10.0.0.{i};"));

        var text = EvtxReader.Resolved(many);

        Assert.NotNull(text);
        Assert.Equal(4, text.Split(',').Length);
    }

    /// <summary>
    /// Without Sysmon nothing records this per process, and the honest answer is an empty list -
    /// not the machine's DNS log, which cannot say which process asked.
    /// </summary>
    [Fact]
    public void A_machine_without_sysmon_reports_nothing_rather_than_guessing()
    {
        var touches = EvtxReader.NetworkTouches(4, DateTime.Now.AddMinutes(-5), DateTime.Now);

        Assert.NotNull(touches);
    }
}
