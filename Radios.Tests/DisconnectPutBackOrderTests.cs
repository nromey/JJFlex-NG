using System;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// A deliberate disconnect stops PC audio, and so puts back the mic input and
/// the shack speaker, while the connection still answers (#861, #600).
/// </summary>
/// <remarks>
/// <para>
/// Until this, <c>Disconnect()</c> set <c>PCAudio = false</c> only after
/// <c>theRadio.Disconnect()</c> and the wait for the link to fall. The mic
/// input's put-back then went into a closed connection, where FlexLib's send
/// returns without sending, and traced success; and the shack speaker's unmute,
/// which refuses while <c>Disconnecting</c> is set, never ran.
/// </para>
/// <para>
/// A SOURCE-ORDER test, on purpose: <c>Disconnect()</c> speaks and needs a
/// live audio thread to exercise, so what is pinned is the order of the three
/// statements that decide the outcome. The put-backs themselves (only what
/// still matches) are each one condition on the radio's reported state, read
/// in <see cref="The_put_backs_write_only_what_still_matches"/>.
/// </para>
/// </remarks>
public sealed class DisconnectPutBackOrderTests
{
    private static string DisconnectBody()
    {
        string text = FlexBasePartials.JoinedText();
        int start = text.IndexOf("public void Disconnect()", StringComparison.Ordinal);
        Assert.True(start > 0, "FlexBase.Disconnect() moved");
        int end = text.IndexOf("public const int SkFarewellFallbackMs", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of Disconnect() could not be found");
        return text.Substring(start, end - start);
    }

    [Fact]
    public void PC_audio_stops_before_the_disconnect_flag_and_before_the_hang_up()
    {
        string body = DisconnectBody();

        int stop = body.IndexOf("PCAudio = false;", StringComparison.Ordinal);
        int flag = body.IndexOf("Disconnecting = true;", StringComparison.Ordinal);
        int hangUp = body.IndexOf("theRadio.Disconnect();", StringComparison.Ordinal);
        int profiles = body.IndexOf("PutProfilesBackOnDisconnect();", StringComparison.Ordinal);

        Assert.True(stop > 0 && flag > 0 && hangUp > 0 && profiles > 0, "a statement this test orders has moved");
        Assert.True(stop < flag,
            "PC audio stops after Disconnecting is set, so the speaker's unmute refuses and the room keeps a dead speaker");
        Assert.True(stop < hangUp,
            "PC audio stops after the hang-up, so the mic input is put back into a closed connection");
        Assert.True(profiles < stop, "the profile put-back should keep the last word, so it runs first");
    }

    [Fact]
    public void A_stop_after_the_hang_up_says_it_cannot_reach_the_radio()
    {
        string body = DisconnectBody();
        int hangUp = body.IndexOf("theRadio.Disconnect();", StringComparison.Ordinal);
        int late = body.IndexOf("PCAudio = false;", hangUp, StringComparison.Ordinal);
        if (late < 0) return;   // no backstop at all is also honest
        string around = body.Substring(Math.Max(0, late - 400), 400);
        Assert.Contains("nothing it puts back can reach the radio", around, StringComparison.Ordinal);
    }

    [Fact]
    public void The_put_backs_write_only_what_still_matches()
    {
        string text = FlexBasePartials.JoinedText();

        // The mic input: only while the radio still reports the PC we chose.
        int putBack = text.IndexOf("private void PutBackNow(Radio radio, MicInputSwap swap, string when)", StringComparison.Ordinal);
        Assert.True(putBack > 0, "PutBackNow moved");
        string mic = text.Substring(putBack, 1200);   // widened: the teardown withhold sits ahead of it
        Assert.Contains("if (string.Equals(nowInput, \"PC\", StringComparison.OrdinalIgnoreCase))", mic, StringComparison.Ordinal);

        // The shack speaker: only while the radio still mutes it.
        int write = text.IndexOf("IsMuteLocalAudioWhenRemoteOn=false on remote audio stop", StringComparison.Ordinal);
        Assert.True(write > 0);
        string speaker = text.Substring(Math.Max(0, write - 300), 300);
        Assert.Contains("if (!speakerRadio.IsMuteLocalAudioWhenRemoteOn)", speaker, StringComparison.Ordinal);
    }

    [Fact]
    public void The_disconnect_unkeys_and_confirms_receive_before_anything_is_put_back()
    {
        // #596 and #861, Codex's checks: the profile put-back, the mic input
        // and the speaker are put back only after this session has unkeyed and
        // the radio has reported receive.
        string body = DisconnectBody();
        int unkey = body.IndexOf("UnkeyBeforePuttingBack(\"Disconnect\"", StringComparison.Ordinal);
        int profiles = body.IndexOf("PutProfilesBackOnDisconnect();", StringComparison.Ordinal);
        int stop = body.IndexOf("PCAudio = false;", StringComparison.Ordinal);
        int vox = body.IndexOf("DisconnectVoxPauses(restore: receiveConfirmed", StringComparison.Ordinal);
        Assert.True(unkey > 0 && profiles > 0 && stop > 0 && vox > 0, "a statement this test orders has moved");
        Assert.True(unkey < vox && unkey < profiles && unkey < stop,
            "something is put back before this session has unkeyed and the radio has reported receive");
    }

    [Fact]
    public void A_teardown_that_cannot_confirm_receive_puts_nothing_in_the_transmit_chain_back()
    {
        // #596 N3, repair round 2: the disconnect honours its unkey's answer,
        // and Dispose no longer restores the profiles before that unkey.
        string body = DisconnectBody();
        Assert.Contains("bool receiveConfirmed = UnkeyBeforePuttingBack(\"Disconnect\"", body, StringComparison.Ordinal);
        Assert.Contains("DisconnectVoxPauses(restore: receiveConfirmed && !TeardownRefusesRestore(\"VOX\"));", body, StringComparison.Ordinal);
        string text = FlexBasePartials.JoinedText();
        int dispose = text.IndexOf("protected virtual void Dispose(bool disposing)", StringComparison.Ordinal);
        int disconnect = text.IndexOf("Disconnect();", dispose, StringComparison.Ordinal);
        string beforeDisconnect = text.Substring(dispose, disconnect - dispose);
        Assert.DoesNotContain("PutProfilesBackOnDisconnect();", beforeDisconnect, StringComparison.Ordinal);
        Assert.DoesNotContain("saveNewGlobalProfile();", beforeDisconnect, StringComparison.Ordinal);
        Assert.Contains("if (theRadio == null) DisconnectVoxPauses(restore: true);", beforeDisconnect, StringComparison.Ordinal);
        // Every restoration entry point asks the interlock.
        int plan = text.IndexOf("private void PutProfilesBackOnDisconnect()", StringComparison.Ordinal);
        string planBody = text.Substring(plan, 4000);
        int barrier = planBody.IndexOf("if (TeardownRefusesRestore(\"the profiles and the live transmit audio\")) return;", StringComparison.Ordinal);
        int run = planBody.IndexOf("bool ok = RunProfileAction(action);", StringComparison.Ordinal);
        Assert.True(barrier > 0 && run > barrier, "the profile put-back writes before asking the interlock");
        Assert.Contains("TeardownRefusesRestore(\"the transmit profile '\" + name + \"'\")", text, StringComparison.Ordinal);
        Assert.Contains("TeardownRefusesRestore(\"the microphone profile '\" + name + \"'\")", text, StringComparison.Ordinal);
        Assert.Contains("TeardownRefusesRestore(\"the global profile '\" + name + \"'\")", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_queued_mic_put_back_runs_before_the_command_loop_stops()
    {
        // #861, Codex's check, blocker 2: the switch-back's put-back is queued,
        // so the disconnect drains it after PC audio stops and before the
        // attempt is cancelled and the loop is told to stop.
        string body = DisconnectBody();
        int stop = body.IndexOf("PCAudio = false;", StringComparison.Ordinal);
        int drain = body.IndexOf("DrainQueuedMicPutBack(\"Disconnect\"", StringComparison.Ordinal);
        int cancel = body.IndexOf("CancelStationAttempt(\"Disconnect\");", StringComparison.Ordinal);
        int loop = body.IndexOf("stopMainThread = true;", StringComparison.Ordinal);
        Assert.True(stop > 0 && drain > 0 && cancel > 0 && loop > 0, "a statement this test orders has moved");
        Assert.True(stop < drain && drain < cancel && drain < loop,
            "the queued mic put-back can be left behind when the command loop stops");
    }

    [Fact]
    public void Both_mic_put_backs_ask_the_interlock_at_their_write()
    {
        string text = FlexBasePartials.JoinedText();
        int now = text.IndexOf("private void PutBackNow(Radio radio, MicInputSwap swap, string when)", StringComparison.Ordinal);
        int queued = text.IndexOf("private void WriteSwitchBackPutBack(Radio radio, MicInputSwap swap, PutBackPin pin, string when)", StringComparison.Ordinal);
        Assert.True(now > 0 && queued > 0, "a put-back moved");
        Assert.Contains("if (MicPutBackWithheld(when, swap.Saved)) { KeepMicPutBackOwed(swap); return; }", text.Substring(now, 900), StringComparison.Ordinal);
        string queuedBody = text.Substring(queued, 3500);
        int withhold = queuedBody.IndexOf("if (MicPutBackWithheld(when, swap.Saved)) { KeepMicPutBackOwed(swap); return; }", StringComparison.Ordinal);
        int raw = queuedBody.IndexOf("SendRadioCommandWithReply(radio, command", StringComparison.Ordinal);
        Assert.True(withhold > 0 && raw > withhold, "the raw switch-back write can go out without asking the teardown");
    }
}
