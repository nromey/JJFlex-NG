using System;
using System.Linq;
using System.Reflection;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The second 8600 bench (2026-11-06, over LAN), Fail 6 still failing: on a
/// local connect the app unmutes the shack speaker before PC audio starts, so a
/// speaker found muted (1) was left unmuted (0) by the stop and the disconnect,
/// twice. The value found at the connect is now recorded before that unmute and
/// is what is put back, only while the radio still reads what the app set.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SpeakerFoundAtConnectTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(SpeakerFoundAtConnectTests));
    public void Dispose() => _scope.Dispose();

    private static readonly MethodInfo? Note =
        typeof(FlexBase).GetMethod("NoteSpeakerFoundAtConnect", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? PutBack =
        typeof(FlexBase).GetMethod("PutBackSpeakerFoundAtConnect", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? FoundForRun =
        typeof(FlexBase).GetMethod("SpeakerFoundForRun", BindingFlags.Instance | BindingFlags.NonPublic);

    private static string[] Writes(RigOnVendorRadio f) =>
        f.Vendor.Transport.Commands.Where(c => c.StartsWith("radio set mute_local_audio_when_remote", StringComparison.Ordinal)).ToArray();

    private static RigOnVendorRadio FoundMutedThenUnmutedAtConnect()
    {
        Assert.True(Note != null && PutBack != null && FoundForRun != null,
            "nothing records the speaker found at a local connect");
        var f = new RigOnVendorRadio("speaker-found-at-connect");
        f.Vendor.Status("radio mute_local_audio_when_remote=1");     // found muted, before the app touches it
        Note!.Invoke(f.Rig, new object[] { f.Vendor.Radio });         // recorded first...
        f.Vendor.Radio.IsMuteLocalAudioWhenRemoteOn = false;          // ...then the local connect's unmute,
        NoteOwnWrite(f, false);                                       // noted as ours, as the connect does (round seven)
        return f;
    }

    private static readonly MethodInfo? NoteOwn =
        typeof(FlexBase).GetMethod("NoteOwnSpeaker", BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);

    private static void NoteOwnWrite(RigOnVendorRadio f, bool muted)
    {
        Assert.True(NoteOwn != null, "nothing notes the speaker value this app wrote");
        NoteOwn!.Invoke(NoteOwn.IsStatic ? null : f.Rig, new object[] { f.Vendor.Radio, muted });
    }

    [Fact]
    public void A_disconnect_with_no_PC_audio_puts_the_speaker_back_muted_as_found()
    {
        using var f = FoundMutedThenUnmutedAtConnect();
        PutBack!.Invoke(f.Rig, new object[] { "test" });
        Assert.Equal("radio set mute_local_audio_when_remote=1", Writes(f).Last());
        Assert.True(f.Vendor.Radio.IsMuteLocalAudioWhenRemoteOn);
    }

    [Fact]
    public void Someone_elses_later_choice_is_left_alone()
    {
        using var f = FoundMutedThenUnmutedAtConnect();
        f.Vendor.Status("radio mute_local_audio_when_remote=1");     // muted since, not by us
        int before = Writes(f).Length;
        PutBack!.Invoke(f.Rig, new object[] { "test" });
        Assert.Equal(before, Writes(f).Length);
    }

    [Fact]
    public void A_speaker_found_unmuted_needs_nothing_put_back()
    {
        Assert.True(Note != null && PutBack != null);
        using var f = new RigOnVendorRadio("speaker-found-unmuted");
        f.Vendor.Status("radio mute_local_audio_when_remote=0");
        Note!.Invoke(f.Rig, new object[] { f.Vendor.Radio });
        int before = Writes(f).Length;
        PutBack!.Invoke(f.Rig, new object[] { "test" });
        Assert.Equal(before, Writes(f).Length);
    }

    [Fact]
    public void PC_audio_started_after_a_local_connect_takes_the_value_found_at_the_connect()
    {
        // So its stop leaves a speaker found muted, muted.
        using var f = FoundMutedThenUnmutedAtConnect();
        Assert.True((bool)FoundForRun!.Invoke(f.Rig, new object[] { f.Vendor.Radio })!);
        string text = FlexBasePartials.JoinedText();
        Assert.Contains("bool speakerFoundMuted = SpeakerFoundForRun(theRadio);", text);
        int note = text.IndexOf("NoteSpeakerFoundAtConnect(theRadio);", StringComparison.Ordinal);
        int unmute = text.IndexOf("theRadio.IsMuteLocalAudioWhenRemoteOn = false;", StringComparison.Ordinal);
        Assert.True(note > 0 && unmute > note, "the local connect does not record the speaker before it unmutes it");
        int putBack = text.IndexOf("PutBackSpeakerFoundAtConnect(\"Disconnect\");", StringComparison.Ordinal);
        int hangUp = text.IndexOf("theRadio.Disconnect();", StringComparison.Ordinal);
        Assert.True(putBack > 0 && hangUp > putBack, "the disconnect does not put the speaker back before the hang-up");
    }

    // ── Round four, Codex's re-check S1: a newer choice of the same value ──
    //
    // The radio does not send a status back to the client that wrote THIS
    // property (measured on the 8600 over LAN with a raw client, 2026-11-06,
    // muteechoprobe.log: reply only; a mic input write, by contrast, IS echoed,
    // micechoprobe.log). So ANY mute_local_audio_when_remote report after the
    // connect recorded what it found is another station's choice, and it
    // retires the connect's obligation, even when it lands on the very value
    // the app had set.

    private static RigOnVendorRadio AnotherStationMutesThenUnmutes()
    {
        var f = FoundMutedThenUnmutedAtConnect();
        f.Vendor.Status("radio mute_local_audio_when_remote=1");     // another station mutes...
        f.Vendor.Status("radio mute_local_audio_when_remote=0");     // ...then deliberately unmutes
        return f;
    }

    [Fact]
    public void Another_stations_unmute_after_our_own_is_not_overwritten_at_the_disconnect()
    {
        // Codex's speaker-choice probe: the disconnect wrote 1 over the newer 0.
        using var f = AnotherStationMutesThenUnmutes();
        int before = Writes(f).Length;
        PutBack!.Invoke(f.Rig, new object[] { "test" });
        Assert.Equal(before, Writes(f).Length);
        Assert.False(f.Vendor.Radio.IsMuteLocalAudioWhenRemoteOn);
    }

    [Fact]
    public void A_PC_audio_run_after_that_choice_counts_the_speaker_as_the_station_left_it()
    {
        using var f = AnotherStationMutesThenUnmutes();
        Assert.False((bool)FoundForRun!.Invoke(f.Rig, new object[] { f.Vendor.Radio })!);
    }

    [Fact]
    public void With_no_choice_reported_the_record_still_puts_back_what_it_found()
    {
        // Control: the app's own 0 is never reported back to it, so with no
        // report since, the record stands and the found 1 is put back.
        using var f = FoundMutedThenUnmutedAtConnect();
        Assert.True((bool)FoundForRun!.Invoke(f.Rig, new object[] { f.Vendor.Radio })!);
        PutBack!.Invoke(f.Rig, new object[] { "test" });
        Assert.Equal("radio set mute_local_audio_when_remote=1", Writes(f).Last());
    }

    // ── Round seven, the third bench: the radio repeating our own unmute ──
    //
    // At a local connect the app unmuted to 0 and then received three status
    // reports of 0 within a second, with ONE change on the radio. They were
    // counted as another station's choice, so with PC audio off the
    // disconnect would have skipped putting the found 1 back.

    [Fact]
    public void Repeats_of_our_own_unmute_are_not_another_stations_choice()
    {
        using var f = FoundMutedThenUnmutedAtConnect();
        for (int i = 0; i < 3; i++) f.Vendor.Status("radio mute_local_audio_when_remote=0");
        PutBack!.Invoke(f.Rig, new object[] { "test" });
        Assert.Equal("radio set mute_local_audio_when_remote=1", Writes(f).Last());
        Assert.True(f.Vendor.Radio.IsMuteLocalAudioWhenRemoteOn);
    }

    [Fact]
    public void Our_value_again_after_a_different_one_is_a_choice()
    {
        // The narrow rule: a repeat counts as ours only with no different
        // value in between. 1 then 0 is another station's mute and unmute.
        using var f = FoundMutedThenUnmutedAtConnect();
        f.Vendor.Status("radio mute_local_audio_when_remote=0");    // a repeat of ours: not a choice
        f.Vendor.Status("radio mute_local_audio_when_remote=1");    // another station
        f.Vendor.Status("radio mute_local_audio_when_remote=0");    // and back: a choice
        int before = Writes(f).Length;
        PutBack!.Invoke(f.Rig, new object[] { "test" });
        Assert.Equal(before, Writes(f).Length);
    }

    [Fact]
    public void Every_speaker_write_notes_its_value_right_after_it()
    {
        string text = FlexBasePartials.JoinedText();
        foreach (var (write, note) in new[]
        {
            ("theRadio.IsMuteLocalAudioWhenRemoteOn = false;", "NoteOwnSpeaker(theRadio, false);"),
            ("theRadio.IsMuteLocalAudioWhenRemoteOn = true;", "NoteOwnSpeaker(theRadio, true);"),
            ("radio.IsMuteLocalAudioWhenRemoteOn = true;", "NoteOwnSpeaker(radio, true);"),
            ("speakerRadio.IsMuteLocalAudioWhenRemoteOn = false;", "NoteOwnSpeaker(speakerRadio, false);"),
        })
        {
            int at = text.IndexOf(write, StringComparison.Ordinal);
            Assert.True(at > 0, "write gone: " + write);
            Assert.Contains(note, text.Substring(at, 120), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_put_back_that_did_not_go_out_leaves_no_value_of_ours_noted()
    {
        // Its value was never written, so a later report of it is somebody's choice.
        using var f = FoundMutedThenUnmutedAtConnect();
        f.Vendor.Transport.FailEveryWriteOf = "radio set mute_local_audio_when_remote";
        PutBack!.Invoke(f.Rig, new object[] { "test" });
        f.Vendor.Transport.FailEveryWriteOf = null;
        var connection = (Flex.Smoothlake.FlexLib.CommandConnection)f.Vendor.Radio.CurrentCommandConnection!;
        long before = InterlockReports.SpeakerChoicesReported(connection);
        f.Vendor.Status("radio mute_local_audio_when_remote=1");
        Assert.Equal(before + 1, InterlockReports.SpeakerChoicesReported(connection));
    }
}
