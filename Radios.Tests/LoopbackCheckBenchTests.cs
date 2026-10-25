using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// Noel's Loopback Check bench, 2026-10-25, on the 8600: the check keyed and
/// put everything back, but he never heard himself, and from the second run on
/// it left slice A muted.
///
/// The existing round-seven fake answers every "slice set" with a status echo,
/// so FlexLib's Slice.Mute is always right in a test and neither defect could
/// show. <see cref="BenchRadio"/> answers the way that radio did: a mute it is
/// told to set is echoed, an unmute is only acknowledged, and "sub slice all"
/// reports what the radio really holds.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class LoopbackCheckBenchTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(LoopbackCheckBenchTests));
    public void Dispose() => _scope.Dispose();

    private static readonly Regex MuteSet = new(@"^slice set (\d+) audio_mute=([01])$", RegexOptions.CultureInvariant);

    private sealed class BenchRadio
    {
        internal readonly RadioCase C = new();
        internal readonly Dictionary<int, bool> Truth = new();
        internal bool EarsCreatedMuted = true;
        internal Action<string>? Extra;

        internal BenchRadio()
        {
            Field(C.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
            C.F.Vendor.Transport.CommandWritten = Answer;
        }

        private void Answer(string command)
        {
            Extra?.Invoke(command);
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                C.Ears = C.Add(0, 1.8);
                // The radio copies the slice it clones from, and the arrangement
                // has just muted that one.
                Field(C.Ears, "_mute", EarsCreatedMuted);
                Truth[0] = EarsCreatedMuted;
                Field(C.F.Rig, "mySliceAdded", true);
                Field(C.F.Rig, "_RXVFO", 1); Field(C.F.Rig, "_TXVFO", 1);
                C.F.Vendor.Reply(C.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
                return;
            }
            var mute = MuteSet.Match(command);
            if (mute.Success)
            {
                int index = int.Parse(mute.Groups[1].Value);
                bool muted = mute.Groups[2].Value == "1";
                Truth[index] = muted;
                if (muted) C.F.Vendor.Status($"slice {index} audio_mute=1");
                C.F.Vendor.ReplyOk(C.F.Vendor.LastSequence);
                return;
            }
            if (command == "sub slice all")
            {
                foreach (var slice in C.F.Vendor.Radio.SliceList)
                {
                    bool muted = Truth.TryGetValue(slice.Index, out bool held) ? held : slice.Mute;
                    C.F.Vendor.Status($"slice {slice.Index} audio_mute={(muted ? 1 : 0)}");
                }
                C.F.Vendor.ReplyOk(C.F.Vendor.LastSequence);
                return;
            }
            C.Respond(command);
        }

        internal IReadOnlyList<string> Commands => C.F.Vendor.Transport.Commands;
    }

    [Fact]
    public async Task The_Loopback_Check_unmutes_the_listening_slice_the_radio_created_muted()
    {
        var b = new BenchRadio();
        using var _ = b.C;
        Assert.True(await b.C.Pump(b.C.F.Rig.StartLoopbackArrangementAsync()));
        Assert.Contains("slice set 0 audio_mute=0", b.Commands);
        Assert.False(b.Truth[0]);
        Assert.True(b.Truth[1]); // slice A, the operator's own, is the one muted
    }

    [Fact]
    public async Task The_off_air_arrangement_leaves_the_listening_slice_mute_to_its_own_caller()
    {
        // Positive control for the test above: the same arrangement without
        // the Loopback Check's flag sends no ears unmute, so "Hear yourself
        // during the take" (off by default) still decides it.
        var b = new BenchRadio();
        using var _ = b.C;
        var arrange = typeof(FlexBase).GetMethod("ArrangeLoopbackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<bool>)arrange.Invoke(b.C.F.Rig, new object?[] { null, false })!;
        Assert.True(await b.C.Pump(task));
        Assert.DoesNotContain("slice set 0 audio_mute=0", b.Commands);
        Assert.True(b.Truth[0]);
    }

    [Fact]
    public async Task An_unmute_the_radio_only_acknowledges_still_leaves_FlexLib_reading_unmuted()
    {
        var b = new BenchRadio();
        using var _ = b.C;
        Assert.True(await b.C.Pump(b.C.F.Rig.StartLoopbackArrangementAsync()));
        Assert.True(b.C.Tx.Mute); // the setup mute was echoed
        Assert.Equal("", await b.C.Pump(b.C.F.Rig.EndLoopbackArrangementAsync()));
        Assert.False(b.Truth[1]);
        // The bench failure: FlexLib kept True here, and the station layout and
        // the next check both read it.
        Assert.False(b.C.Tx.Mute);
    }

    [Fact]
    public async Task A_stale_muted_cache_does_not_make_the_check_leave_slice_A_muted()
    {
        // Run two and three on the bench: the radio has A unmuted, FlexLib
        // still says muted. The check must mute A for the loop and unmute it
        // after, rather than skip the mute and "restore" one A never had.
        var b = new BenchRadio();
        using var _ = b.C;
        Field(b.C.Tx, "_mute", true);
        b.Truth[1] = false;
        Assert.True(await b.C.Pump(b.C.F.Rig.StartLoopbackArrangementAsync()));
        Assert.Contains("slice set 1 audio_mute=1", b.Commands);
        Assert.Equal("", await b.C.Pump(b.C.F.Rig.EndLoopbackArrangementAsync()));
        Assert.False(b.Truth[1]);
        Assert.Equal("slice set 1 audio_mute=0", b.Commands.Last(c => c.StartsWith("slice set 1 audio_mute=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stopping_on_the_listening_slice_goes_back_to_slice_A_and_removes_it()
    {
        var b = new BenchRadio();
        using var _ = b.C;
        b.Extra = command =>
        {
            // The radio's ActiveSlice report, which moves RXVFO.
            if (command == "slice set 1 active=1") Field(b.C.F.Rig, "_RXVFO", 1);
        };
        Assert.True(await b.C.Pump(b.C.F.Rig.StartLoopbackArrangementAsync()));
        Field(b.C.F.Rig, "_RXVFO", 0); // the operator moved to the listening slice (index 0 sorts first)
        Assert.Same(b.C.Ears, Rx(b.C.F.Rig));
        string trouble = await b.C.Pump(b.C.F.Rig.EndLoopbackArrangementAsync());
        Assert.DoesNotContain("kept", trouble);
        Assert.Contains("slice set 1 active=1", b.Commands);
        Assert.Contains("slice remove 0", b.Commands);
    }

    [Fact]
    public void Going_back_to_slice_A_for_the_operator_plays_no_CW_identity()
    {
        // Noel, 2026-10-25: the return must not sound like he changed slices.
        var savedPlay = ScreenReaderOutput.PlayCwText;
        bool savedCw = ScreenReaderOutput.CwNotificationsEnabled, savedMode = ScreenReaderOutput.CwModeAnnounceEnabled;
        var played = new List<string>();
        try
        {
            ScreenReaderOutput.ResetCwLastSent();
            ScreenReaderOutput.PlayCwText = text => { played.Add(text); return Task.CompletedTask; };
            ScreenReaderOutput.CwNotificationsEnabled = true;
            ScreenReaderOutput.CwModeAnnounceEnabled = true;
            var c = new RadioCase();
            using var _ = c;
            Field(c.Tx, "_letter", "A"); // the identity needs a letter to say
            var announce = typeof(FlexBase).GetMethod("AnnounceSliceIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var silence = typeof(FlexBase).GetMethod("SilenceNextActivation", BindingFlags.Instance | BindingFlags.NonPublic)!;

            silence.Invoke(c.F.Rig, new object[] { c.Tx });
            announce.Invoke(c.F.Rig, new object[] { c.Tx });
            Assert.Empty(played);

            // Positive control: the marker is spent, so a real arrival speaks.
            announce.Invoke(c.F.Rig, new object[] { c.Tx });
            Assert.Single(played);
        }
        finally
        {
            ScreenReaderOutput.PlayCwText = savedPlay;
            ScreenReaderOutput.CwNotificationsEnabled = savedCw;
            ScreenReaderOutput.CwModeAnnounceEnabled = savedMode;
            ScreenReaderOutput.ResetCwLastSent();
        }
    }

    private static Slice? Rx(FlexBase rig) =>
        (Slice?)typeof(FlexBase).GetMethod("VFOToSlice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig, new object[] { rig.RXVFO });
}
