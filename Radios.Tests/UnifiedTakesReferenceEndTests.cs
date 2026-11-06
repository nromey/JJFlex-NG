#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round three (5b): a reference that reaches its end ends the
/// transmission (Noel, 2026-11-04 11:36). Pinned in source where the window
/// itself is never built in a test: the check stops by the call the Stop
/// button makes, the app's key goes through the controller, the sentence no
/// longer says the microphone is back, and the two users of the transmit file
/// player refuse each other. The behaviour on the real controller is
/// <c>JJFlexWpf.Tests.ReferenceEndTests</c>.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesReferenceEndTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), Path.Combine(parts)));

    private static string Dialog(string file) => Read("JJFlexWpf", "Dialogs", file);

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced: " + signature);
    }

    private static string Case(string body, string label)
    {
        int at = body.IndexOf(label, StringComparison.Ordinal);
        Assert.True(at >= 0, "no case " + label);
        int end = body.IndexOf("return ", at, StringComparison.Ordinal);
        return body[at..end];
    }

    /// <summary>
    /// Round four repair (6e-3): every change of radio lets the departing
    /// radio's tone and reference go, a switch straight to another radio as
    /// well as a radio leaving with none arriving. Pinned in source because
    /// the real <c>SetRig</c> cannot run without the built window (its poll
    /// timer needs a dispatcher, which brings a window with it); the release
    /// itself, <c>DisarmReference</c> on a named radio, is driven in
    /// <c>JJFlexWpf.Tests.ReferenceEndSilenceTests</c>.
    /// </summary>
    [Fact]
    public void Every_change_of_radio_lets_the_departing_radios_tone_and_reference_go()
    {
        string setRig = Body(Dialog("CheckYourAudioDialog.cs"), "public void SetRig(FlexBase? rig)");
        int arriving = setRig.IndexOf("if (rig != null)", StringComparison.Ordinal);
        int leaving = setRig.IndexOf("        else", arriving, StringComparison.Ordinal);
        Assert.True(arriving > 0 && leaving > arriving, "SetRig's two branches moved");
        string arrivingBranch = setRig[arriving..leaving];
        string leavingBranch = setRig[leaving..];

        // A switch: the departing radio, named, and only when it is another one.
        int guard = arrivingBranch.IndexOf("if (oldRig != null && !ReferenceEquals(oldRig, rig))", StringComparison.Ordinal);
        Assert.True(guard > 0, "a switch to another radio no longer releases the departing one");
        int load = arrivingBranch.IndexOf("LoadPerRadioPrefs();", StringComparison.Ordinal);
        string released = arrivingBranch[guard..load];
        // Round five (7b): a held end silence goes to that radio's own wait;
        // anything else is disarmed (ReleaseReferenceLeavingWindow). Round five
        // repair (7h-2): first an end the poll had not seen is finished for the
        // departing radio, so its app key is let go after the tail.
        Assert.Contains("ReleaseReferenceAtSwitch(oldRig);", released);
        Assert.Contains("DisarmTone(speak: false, rig: oldRig);", released);
        string atSwitch = Body(Dialog("CheckYourAudioDialog.ReferenceEnd.cs"), "private void ReleaseReferenceAtSwitch(FlexBase oldRig)");
        int finish = atSwitch.IndexOf("FinishPassIfItEnded(oldRig);", StringComparison.Ordinal);
        int leave = atSwitch.IndexOf("ReleaseReferenceLeavingWindow(oldRig);", StringComparison.Ordinal);
        Assert.True(finish > 0 && leave > finish, "the switch finishes an unseen end before it lets the reference go");
        // The order the helper is driven in (JJFlexWpf.Tests.ReferenceEndSpeechTests):
        // _rig has already moved to the arriving radio when it runs.
        Assert.True(setRig.IndexOf("_rig = rig;", StringComparison.Ordinal) < setRig.IndexOf("ReleaseReferenceAtSwitch(oldRig);", StringComparison.Ordinal),
            "SetRig now releases the departing reference before _rig moves; the switch tests drive the other order");

        // A radio leaving with none arriving, unchanged.
        Assert.Contains("DisarmReference(speak: false, rig: oldRig);", leavingBranch);
        Assert.Contains("DisarmTone(speak: false, rig: oldRig);", leavingBranch);
    }

    [Fact]
    public void A_check_the_reference_rode_stops_by_the_same_call_the_Stop_button_makes()
    {
        string toggle = Body(Dialog("CheckYourAudioDialog.Check.cs"), "public void ToggleAudioCheck()");
        Assert.Contains("_session.StopCheck();", toggle);
        string end = Body(Dialog("CheckYourAudioDialog.ReferenceEnd.cs"),
            "internal ReferenceEndHolder EndTransmissionForReference(");
        string check = Case(end, "case ReferenceEndHolder.Check");
        Assert.Contains("_session.StopCheck();", check);
        Assert.DoesNotContain("ForceEnd", check);
        Assert.DoesNotContain("EscapeUnlock", check);
        // Round five repair 2 (7i-1): a check asks the same plan as the app's
        // key, and the plan is asked in one place, before either branch.
        Assert.Contains("if (stillTheReferences)", check);
        Assert.Equal(1, Regex.Matches(end, @"\.StillCurrent\(").Count);
        // Round five repair 2 (7i-2): the session takes its own key's identity
        // the moment it keys, and its watcher asks that, not "transmitting".
        string start = Body(Dialog("CheckYourAudioDialog.Check.cs"), "public async Task<bool> StartAsync(");
        int taken = start.IndexOf("_ownKeying = ReferenceKeying.Now(rig, _ptt);", StringComparison.Ordinal);
        int keyed = start.IndexOf("_phase = Phase.Keyed;", StringComparison.Ordinal);
        int lastAwait = start.LastIndexOf("await ", keyed, StringComparison.Ordinal);
        Assert.True(taken > lastAwait && taken < keyed, "the check's identity is not taken between its last await and Keyed");
        string tick = Body(Dialog("CheckYourAudioDialog.Check.cs"), "private void TickKeyed(FlexBase rig)");
        Assert.Contains("if (!OwnKeyHeld)", tick);
        // Stop unkeys only while keyed; the check's later phases are not the reference's to end.
        Assert.Contains("internal bool Keyed => Active && _phase == Phase.Keyed;",
            Dialog("CheckYourAudioDialog.ReferenceEnd.cs"));
    }

    [Fact]
    public void The_apps_key_is_let_go_through_the_controller_never_by_the_radios_transmit_flag()
    {
        string end = Body(Dialog("CheckYourAudioDialog.ReferenceEnd.cs"),
            "internal ReferenceEndHolder EndTransmissionForReference(");
        // Up to its own return: round five (7c) put an early return for a new transmission inside it.
        string app = end[end.IndexOf("case ReferenceEndHolder.App", StringComparison.Ordinal)..end.IndexOf("return ReferenceEndHolder.App;", StringComparison.Ordinal)];
        // Round five (7a): through MainWindow's key edges, which latch a held key.
        Assert.Contains("EndAppKeyForReference", app);
        // Round five (7c): the quiet unkey, the controller's own.
        Assert.Contains(".EndAfterReference()", app);
        Assert.DoesNotMatch(new Regex(@"\.Transmit\s*="), end);
    }

    [Fact]
    public void The_finished_reference_ends_the_transmission_only_for_a_check_or_the_apps_key_after_the_tail()
    {
        string sync = Body(Dialog("CheckYourAudioDialog.ReferenceAudio.cs"), "private void SyncReferenceUi()");
        // Round five repair (7h-2): the end is the helper's, shared with a radio switch.
        Assert.Contains("if (FinishPassIfItEnded(rig)) return;", sync);
        string poll = sync;
        sync = Body(Dialog("CheckYourAudioDialog.ReferenceAudio.cs"), "private bool FinishPassIfItEnded(FlexBase rig)");
        int holder = sync.IndexOf("var holder = ReferenceHolderNow(rig);", StringComparison.Ordinal);
        // Round four: the sentence and the silence are FinishReferencePass's.
        int speak = sync.IndexOf("FinishReferencePass(rig, holder, hardwareLine: rig.PttSourceIsHardware);", StringComparison.Ordinal);
        int end = sync.IndexOf("if (holder is ReferenceEndHolder.Check or ReferenceEndHolder.App)\n            EndTransmissionAfterReferenceTail(rig, holder);",
            StringComparison.Ordinal);
        if (end < 0) end = sync.IndexOf("if (holder is ReferenceEndHolder.Check or ReferenceEndHolder.App)\r\n            EndTransmissionAfterReferenceTail(rig, holder);",
            StringComparison.Ordinal);
        Assert.True(holder > 0 && speak > holder && end > speak,
            "the holder is read, the sentence chosen from it, and only a check or the app's key ended");
        // The finished sentence is chosen by holder, never said straight from the old key.
        // (Split, so the lexicon key scanner does not read this literal as a key.)
        Assert.DoesNotContain("Lexicon" + ".Get(\"audio.reference.finished\")", sync);
        // Round four: nothing on the end path hands the microphone back or
        // moves the transmit input; both wait for the radio to receive.
        Assert.DoesNotContain("TxFileStop", sync);
        Assert.DoesNotContain("RestoreInjectionPath", sync);
        Assert.DoesNotContain("TxFileStop", poll);
        Assert.DoesNotContain("RestoreInjectionPath", poll);
        string finish = Body(Dialog("CheckYourAudioDialog.ReferenceEnd.cs"), "internal string FinishReferencePass(");
        Assert.DoesNotContain("TxFileStop", finish);
        Assert.DoesNotContain("RestoreInjectionPath", finish);
        Assert.Contains("BeginReferenceEndSilence(rig);", finish);
        string tail = Body(Dialog("CheckYourAudioDialog.ReferenceEnd.cs"),
            "private async void EndTransmissionAfterReferenceTail(");
        Assert.Contains("await Task.Delay(ReferenceEndTail);", tail);
    }

    [Fact]
    public void The_finished_sentence_and_the_arm_help_no_longer_say_the_microphone_comes_back()
    {
        Lexicon.Load(Lexicon.Partitions);
        Assert.DoesNotContain("microphone", Lexicon.Get("audio.reference.finished"), StringComparison.OrdinalIgnoreCase);
        // Round four (6b): nothing hands the microphone back at a reference's
        // end, so the key that said it did is gone (a missing key reads as itself).
        string gone = "audio.check." + "reference_finished_mic_back";
        Assert.Equal(gone, Lexicon.Get(gone));
        Assert.DoesNotContain("reference_finished_mic_back", Read("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.ReferenceEnd.cs"));
        string audio = Dialog("CheckYourAudioDialog.ReferenceAudio.cs");
        Assert.DoesNotContain("microphone comes back", audio);
        Assert.Contains("When it finishes, the transmission ends", audio);
    }

    [Fact]
    public void A_send_refuses_while_a_reference_is_armed_and_a_reference_refuses_while_a_send_plays()
    {
        string sender = Read("JJFlexWpf", "VoiceMessageSender.cs");
        int sends = Regex.Matches(sender,
            @"if \(rig\.TxReferenceArmed\)\s*\{\s*(Refuse|RefuseOrHold)\(info, Lexicon\.Get\(""audio\.check\.reference_already_armed""\)\);\s*return;").Count;
        Assert.Equal(3, sends);   // a voice message, Play from computer, and a paused send carrying on
        Assert.Equal(3, Regex.Matches(sender, @"if \(rig\.TxToneEngaged\)").Count);
        // Round five (7d): during the silence after a reference's end the lease is
        // still held, so each of the three asks for that silence FIRST, with its own sentence.
        Assert.Equal(3, Regex.Matches(sender,
            @"if \(rig\.TxReferenceEndSilenceHeld\)\s*\{\s*(Refuse|RefuseOrHold)\(info, Lexicon\.Get\(""audio\.check\.reference_end_silence_busy""\)\);\s*return;\s*\}\s*if \(rig\.TxReferenceArmed\)").Count);
        string arm = Body(Dialog("CheckYourAudioDialog.ReferenceAudio.cs"), "private void ReferenceArmChanged(bool armed)");
        int refusal = arm.IndexOf("if (rig.TxFilePlaying && !rig.TxReferenceArmed)", StringComparison.Ordinal);
        int load = arm.IndexOf("WithInjectionPath(rig,", StringComparison.Ordinal);
        Assert.True(refusal > 0 && load > refusal, "the arm refuses before anything loads the player");
        Assert.Contains("audio.check.send_already_playing", arm);
    }
}
