using System;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The mic input's report owner (round six, Codex's re-check E1; the state
/// table is in the junk box 2026-11-02 rulings report). Measured on the 8600
/// over LAN, 2026-11-06: a mic write is echoed to its writer before its reply,
/// and a refused one (<c>mic input NOSUCH</c>) is answered success and echoes
/// the UNCHANGED input. So an echo is ours only by VALUE, and an expectation
/// belongs to bytes that actually went out.
/// </summary>
public sealed class MicReportsTests
{
    private static CommandConnection AConnection() =>
        (CommandConnection)new VendorRadioFixture("mic-reports-" + Guid.NewGuid().ToString("N")).Radio.CurrentCommandConnection!;

    [Fact]
    public void Another_stations_report_is_counted_and_PC_never_is()
    {
        var c = AConnection();
        MicReports.Reported(c, "BAL");
        MicReports.Reported(c, "PC");
        Assert.Equal(1, MicReports.ChoicesReported(c));
    }

    [Fact]
    public void Our_echo_is_not_counted()
    {
        var c = AConnection();
        var w = MicReports.Expect(c, "BAL")!;
        MicReports.Reported(c, "BAL");
        w.Outcome(true);
        Assert.Equal(0, MicReports.ChoicesReported(c));
    }

    [Fact]
    public void A_write_that_never_went_out_expects_nothing_and_the_next_same_value_report_counts()
    {
        // Codex's E1, at the owner: the expectation is killed by its own outcome.
        var c = AConnection();
        var w = MicReports.Expect(c, "BAL")!;
        w.Outcome(false);
        MicReports.Reported(c, "BAL");
        Assert.Equal(1, MicReports.ChoicesReported(c));
    }

    [Fact]
    public void A_report_taken_as_the_echo_of_a_write_that_then_failed_is_counted_after_all()
    {
        var c = AConnection();
        var w = MicReports.Expect(c, "BAL")!;
        MicReports.Reported(c, "BAL");                 // arrived while the write's outcome was not yet known
        Assert.Equal(0, MicReports.ChoicesReported(c));
        w.Outcome(false);
        Assert.Equal(1, MicReports.ChoicesReported(c));
    }

    [Fact]
    public void Cancelling_one_write_cancels_that_write_and_not_another_of_the_same_value()
    {
        var c = AConnection();
        var first = MicReports.Expect(c, "BAL")!;
        var second = MicReports.Expect(c, "BAL")!;
        second.Outcome(false);                          // the SECOND never went out
        first.Outcome(true);
        MicReports.Reported(c, "BAL");                  // the first's echo
        Assert.Equal(0, MicReports.ChoicesReported(c));
        MicReports.Reported(c, "BAL");                  // a real choice now
        Assert.Equal(1, MicReports.ChoicesReported(c));
    }

    [Fact]
    public void A_refused_write_echoes_the_unchanged_value_which_retires_it_and_counts()
    {
        // micrefusedprobe.log: "mic input NOSUCH", R|0|, then mic_selection=MIC.
        var c = AConnection();
        var w = MicReports.Expect(c, "NOSUCH")!;
        w.Outcome(true);
        MicReports.Reported(c, "MIC");
        Assert.Equal(1, MicReports.ChoicesReported(c));
        MicReports.Reported(c, "NOSUCH");               // nothing is waiting any more: counted
        Assert.Equal(2, MicReports.ChoicesReported(c));
    }

    [Fact]
    public void An_expectation_lapses_and_then_a_same_value_report_counts()
    {
        var c = AConnection();
        long now = 1_000;
        var clock = MicReports.Clock;
        MicReports.Clock = () => now;
        try
        {
            var w = MicReports.Expect(c, "BAL")!;
            w.Outcome(true);
            now += MicReports.OwnEchoMs;
            MicReports.Reported(c, "BAL");
            Assert.Equal(1, MicReports.ChoicesReported(c));
        }
        finally { MicReports.Clock = clock; }
    }

    [Fact]
    public void Each_connection_has_its_own_reports()
    {
        var a = AConnection();
        var b = AConnection();
        MicReports.Expect(a, "BAL")!.Outcome(true);
        MicReports.Reported(b, "BAL");                  // another connection: counted there
        Assert.Equal(1, MicReports.ChoicesReported(b));
        Assert.Equal(0, MicReports.ChoicesReported(a));
    }

    [Fact]
    public void A_PC_write_registers_nothing()
    {
        Assert.Null(MicReports.Expect(AConnection(), "PC"));
    }
}
