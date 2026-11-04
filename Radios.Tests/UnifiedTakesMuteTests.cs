using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, part 4a (D2, ruled by Noel 2026-11-02 19:52):
/// with saving on, a playback mutes the other audible slices, silently, and
/// puts each back on every exit; the station layout never records a take's
/// temporary mute as the operator's; and Loopback, which shares the restore,
/// behaves and speaks exactly as before.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesMuteTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(UnifiedTakesMuteTests));
    public void Dispose() => _scope.Dispose();

    /// <summary>
    /// Transmit slice 1 (A, plays the take), slice 2 audible, slice 3 already
    /// muted by the operator. The radio answers "sub slice all" with the mute it
    /// was last told.
    /// </summary>
    internal static RadioCase ThreeSlices(string? serial = null)
    {
        var c = new RadioCase(serial);
        Field(c.Tx, "_letter", "A");
        var b = c.Add(2, 7.1); Field(b, "_letter", "B");
        var d = c.Add(3, 21.2); Field(d, "_letter", "C");
        Field(d, "_mute", true); c.HeldMute[3] = true;
        return c;
    }

    internal static Slice Slice(RadioCase c, int index) => c.F.Vendor.Radio.SliceList.First(s => s.Index == index);

    private static void Mox(FlexBase rig, bool on) =>
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, new object[] { on });

    private static async Task<bool> Restored(RadioCase c, PlaybackTakeMutes mutes)
    {
        mutes.Release();
        return await c.Pump(mutes.Restored);
    }

    [Fact]
    public async Task A_saved_playback_mutes_only_the_other_audible_slices_and_puts_each_back()
    {
        using var c = ThreeSlices();
        var mutes = await c.Pump(c.F.Rig.MuteOtherSlicesForPlaybackTakeAsync(SliceRecorderTarget.TransmitSlice));

        Assert.True(mutes.Isolated);
        Assert.False(mutes.PlaybackSliceMuted);
        Assert.Contains("slice set 2 audio_mute=1", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 1 audio_mute=1", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 3 audio_mute=1", c.F.Vendor.Transport.Commands);
        Assert.True(c.HeldMute[2]);
        var b = Assert.Single(mutes.Slices, s => s.SliceIndex == 2);
        Assert.True(b.MutedForTake && b.Confirmed && !b.PriorMuted);
        var already = Assert.Single(mutes.Slices, s => s.SliceIndex == 3);
        Assert.True(already.PriorMuted && !already.MutedForTake);
        Assert.DoesNotContain(mutes.Slices, s => s.SliceIndex == 1);

        // Held until the capture closes: nothing is put back before Release.
        await Task.Delay(200);
        Assert.True(c.HeldMute[2]);
        Assert.True(await Restored(c, mutes));
        Assert.False(c.HeldMute[2]);
        Assert.True(c.HeldMute[3]); // the operator's own mute is untouched
        Assert.DoesNotContain("slice set 3 audio_mute=0", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task With_nothing_else_audible_nothing_is_muted_and_nothing_is_owed()
    {
        using var c = new RadioCase();
        var mutes = await c.Pump(c.F.Rig.MuteOtherSlicesForPlaybackTakeAsync(SliceRecorderTarget.TransmitSlice));
        Assert.True(mutes.Isolated);
        Assert.False(mutes.Owes);
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.Contains("audio_mute=1"));
        Assert.True(await mutes.Restored);
    }

    [Fact]
    public async Task Keyed_during_the_playback_the_mutes_wait_for_receive_and_then_go_back()
    {
        using var c = ThreeSlices();
        var mutes = await c.Pump(c.F.Rig.MuteOtherSlicesForPlaybackTakeAsync(SliceRecorderTarget.TransmitSlice));
        Mox(c.F.Rig, true);
        mutes.Release();
        var settle = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < settle) { while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a(); await Task.Delay(20); }
        Assert.False(mutes.Restored.IsCompleted);
        Assert.True(c.HeldMute[2]);
        Assert.True(c.F.Rig.CheckRestorations.WithheldByTransmit);
        Mox(c.F.Rig, false);
        Assert.True(await c.Pump(mutes.Restored));
        Assert.False(c.HeldMute[2]);
    }

    [Fact]
    public async Task A_teardown_before_a_disconnect_puts_held_mutes_back_without_waiting_for_the_take()
    {
        using var c = ThreeSlices();
        var mutes = await c.Pump(c.F.Rig.MuteOtherSlicesForPlaybackTakeAsync(SliceRecorderTarget.TransmitSlice));
        c.F.Rig.CheckRestorations.BeginReleasingEverything();
        try { Assert.True(await c.Pump(mutes.Restored)); }
        finally { c.F.Rig.CheckRestorations.EndReleasingEverything(); }
        Assert.False(c.HeldMute[2]);
        // And after a disconnect, slices owe the station nothing.
        Assert.Empty(c.F.Rig.CheckPutBacksOwed());
    }

    [Fact]
    public async Task A_mute_the_radio_refuses_keeps_the_mix_and_puts_back_what_was_muted()
    {
        using var c = ThreeSlices();
        var e = c.Add(4, 3.6); Field(e, "_letter", "D");
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "slice set 4 audio_mute=1") { c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000016, ""); return; }
            c.Respond(command);
        };
        var mutes = await c.Pump(c.F.Rig.MuteOtherSlicesForPlaybackTakeAsync(SliceRecorderTarget.TransmitSlice));
        Assert.False(mutes.Isolated);
        Assert.Equal("mute-unconfirmed", mutes.Failure);
        // Released at once, with nobody calling Release: the mix is kept.
        Assert.True(await c.Pump(mutes.Restored));
        Assert.False(c.HeldMute[2]);
    }

    [Fact]
    public async Task A_muted_transmit_slice_is_reported_so_the_take_can_say_its_playback_was_silent()
    {
        using var c = ThreeSlices();
        Field(c.Tx, "_mute", true); c.HeldMute[1] = true;
        var mutes = await c.Pump(c.F.Rig.MuteOtherSlicesForPlaybackTakeAsync(SliceRecorderTarget.TransmitSlice));
        Assert.True(mutes.PlaybackSliceMuted);
        Assert.DoesNotContain("slice set 1 audio_mute=0", c.F.Vendor.Transport.Commands);
        Assert.True(await Restored(c, mutes));
        Assert.True(c.HeldMute[1]);
    }

    // ── The station layout (the leak the second reading suspected) ──

    private static string OwnedSerial()
    {
        string serial = "4864-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999);
        RadioConfig.RecordOwnership(serial, RadioOwnership.Mine);
        return serial;
    }

    private static void RecordLayout(FlexBase rig) =>
        typeof(FlexBase).GetMethod("RecordOwnStationLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig, new object[] { "test" });

    private static SliceMuteState LayoutMute(string serial, string letter) =>
        RadioConfig.StationLayoutOf(serial)!.Slices.Single(s => s.Letter == letter).Mute;

    [Fact]
    public async Task The_station_layout_records_the_operators_mute_while_a_take_holds_another()
    {
        string serial = OwnedSerial();
        using var c = ThreeSlices(serial);
        var mutes = await c.Pump(c.F.Rig.MuteOtherSlicesForPlaybackTakeAsync(SliceRecorderTarget.TransmitSlice));
        // The radio's echo has reached FlexLib: the slice reads muted now.
        Assert.True(Slice(c, 2).Mute);
        RecordLayout(c.F.Rig); // the place flush, or the disconnect's
        Assert.Equal(SliceMuteState.Unmuted, LayoutMute(serial, "B"));
        Assert.Equal(SliceMuteState.Muted, LayoutMute(serial, "C"));

        // Positive control: the same flush after the restore reads the slice itself.
        Assert.True(await Restored(c, mutes));
        Field(Slice(c, 3), "_mute", false); c.HeldMute[3] = false; // the operator unmutes C
        RecordLayout(c.F.Rig);
        Assert.Equal(SliceMuteState.Unmuted, LayoutMute(serial, "C"));
        Assert.False(c.F.Rig.HoldsTemporaryMutes);
    }

    private static RadioCase Arranged(string? serial = null)
    {
        var c = ThreeSlices(serial);
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else c.Respond(command);
        };
        return c;
    }

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    [Fact]
    public async Task Loopback_had_the_same_leak_and_the_shared_hold_closes_it_there_too()
    {
        string serial = OwnedSerial();
        using var c = Arranged(serial);
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);
        Assert.True(c.HeldMute[2]);
        RecordLayout(c.F.Rig);
        Assert.Equal(SliceMuteState.Unmuted, LayoutMute(serial, "B"));

        var outcome = await c.Pump(c.F.Rig.EndLoopbackArrangementWithOutcomeAsync());
        Assert.Empty(outcome.NotRestored);
        Assert.False(c.F.Rig.HoldsTemporaryMutes);
    }

    [Fact]
    public async Task Loopback_still_restores_each_mute_and_says_exactly_what_it_always_said()
    {
        using var c = Arranged();
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);
        Assert.True(c.HeldMute[1]); Assert.True(c.HeldMute[2]);
        var outcome = await c.Pump(c.F.Rig.EndLoopbackArrangementWithOutcomeAsync());
        Assert.False(c.HeldMute[1]); Assert.False(c.HeldMute[2]); Assert.True(c.HeldMute[3]);
        Assert.Contains(LoopbackEndOutcome.Mutes, outcome.Restored);
        Assert.Equal(Lexicon.Get("audio.check.loopback_ended"), outcome.Sentence());
    }

    [Fact]
    public async Task A_Loopback_mute_restore_the_radio_does_not_confirm_is_still_named_in_its_sentence()
    {
        using var c = Arranged();
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);
        var normal = c.F.Vendor.Transport.CommandWritten;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "slice set 2 audio_mute=0") { c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000016, ""); return; }
            normal(command);
        };
        var outcome = await c.Pump(c.F.Rig.EndLoopbackArrangementWithOutcomeAsync());
        Assert.Contains(LoopbackEndOutcome.Mutes, outcome.NotRestored);
        Assert.Contains("Slice 2 mute restoration was not confirmed.", outcome.Trouble);
        Assert.Contains("your slices' mutes", outcome.Sentence());
    }

    // ── Silence (D2: "suppressing the CW and other notices") ──

    [Fact]
    public void The_radios_report_of_a_slice_mute_is_only_traced_never_announced()
    {
        // The one place a radio-reported mute arrives. If it ever speaks, a
        // take's own mutes would be announced; this pins it as a trace.
        string source = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "Radios", "FlexBase.cs"));
        int at = source.IndexOf("case \"Mute\":", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = source[at..source.IndexOf("break;", at, StringComparison.Ordinal)];
        Assert.Contains("Tracing.TraceLine", body);
        Assert.DoesNotContain("Speak", body);
        Assert.DoesNotContain("Earcon", body);
        Assert.DoesNotContain("Braille", body);
        Assert.DoesNotContain("Cw", body);
    }

    [Fact]
    public void The_take_mute_code_speaks_nothing_and_sends_plain_commands()
    {
        string source = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "Radios", "FlexBase.TakeMutes.cs"));
        foreach (string word in new[] { "ScreenReaderOutput", "Speak(", "Earcon", "Braille", "ToggleFeedback", "SliceMute =", ".Mute =" })
            Assert.DoesNotContain(word, source);
        Assert.Contains("audio_mute=1", source);
    }
}
