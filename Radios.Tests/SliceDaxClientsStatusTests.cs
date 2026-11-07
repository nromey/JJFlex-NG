using System;
using System.IO;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// A slice status carrying a DAX client count must not touch the recorder's
/// record time. Our FlexLib patch for <c>record_time</c> (MIGRATION.md item 20)
/// put its body directly under the stock, empty <c>dax_clients</c> label, so
/// C# fell through: <c>dax_clients=1</c> was stored as a one-second take.
/// </summary>
public sealed class SliceDaxClientsStatusTests
{
    private static Slice NewSlice()
    {
        var fixture = new VendorRadioFixture("1234-5678-6300-0875");
        return fixture.NewSlice(0, 0x1u, 7.1, "USB");
    }

    [Fact]
    public void A_dax_client_count_leaves_the_record_time_alone()
    {
        var slice = NewSlice();
        slice.StatusUpdate("record_time=12.5");
        Assert.Equal("12.5", slice.RecordTimeRaw);

        int recordTimeChanges = 0;
        slice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Slice.RecordTimeRaw)) recordTimeChanges++;
        };

        slice.StatusUpdate("dax_clients=1");
        slice.StatusUpdate("dax_clients=0 play=0");

        Assert.Equal("12.5", slice.RecordTimeRaw);
        Assert.Equal(0, recordTimeChanges);
    }

    [Fact]
    public void Record_time_is_still_kept_when_it_arrives_beside_a_dax_client_count()
    {
        // Positive control: the same feed does reach the record_time body, so
        // the test above is not green merely because nothing was parsed.
        var slice = NewSlice();
        slice.StatusUpdate("dax_clients=2 record_time=30.0");
        Assert.Equal("30.0", slice.RecordTimeRaw);
        slice.StatusUpdate("record_time=31.5 dax_clients=1");
        Assert.Equal("31.5", slice.RecordTimeRaw);
    }

    [Fact]
    public void The_vendored_source_gives_dax_clients_its_own_break()
    {
        // A FlexLib upgrade re-applies item 20 by hand. If the record_time body
        // is pasted back under a bare dax_clients label, this fails before any
        // radio does.
        string path = Path.Combine(FlexBasePartials.RepoRoot(), "FlexLib_API", "FlexLib", "Slice.cs");
        string[] lines = File.ReadAllLines(path);
        int label = Array.FindIndex(lines, l => l.Trim() == "case \"dax_clients\":");
        Assert.True(label >= 0, "Slice.cs no longer has a dax_clients case; re-read MIGRATION.md item 20");

        string next = lines.Skip(label + 1)
            .Select(l => l.Trim())
            .First(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));
        Assert.Equal("break;", next);

        int patchComment = Array.FindIndex(lines, label + 1, l => l.Contains("JJFlex patch"));
        Assert.True(patchComment > label && patchComment < label + 5,
            "the dax_clients break must carry its JJFlex patch comment so an upgrade keeps it");
    }
}
