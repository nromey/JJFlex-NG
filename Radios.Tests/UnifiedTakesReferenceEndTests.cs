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
        // Stop unkeys only while keyed; the check's later phases are not the reference's to end.
        Assert.Contains("internal bool Keyed => Active && _phase == Phase.Keyed;",
            Dialog("CheckYourAudioDialog.ReferenceEnd.cs"));
    }

    [Fact]
    public void The_apps_key_is_let_go_through_the_controller_never_by_the_radios_transmit_flag()
    {
        string end = Body(Dialog("CheckYourAudioDialog.ReferenceEnd.cs"),
            "internal ReferenceEndHolder EndTransmissionForReference(");
        string app = Case(end, "case ReferenceEndHolder.App");
        Assert.Contains("DialogTransmitKeys.EscapeUnkey", app);
        Assert.Contains(".EscapeUnlock()", app);
        Assert.DoesNotMatch(new Regex(@"\.Transmit\s*="), end);
    }

    [Fact]
    public void The_finished_reference_ends_the_transmission_only_for_a_check_or_the_apps_key_after_the_tail()
    {
        string sync = Body(Dialog("CheckYourAudioDialog.ReferenceAudio.cs"), "private void SyncReferenceUi()");
        int holder = sync.IndexOf("var holder = ReferenceHolderNow(rig);", StringComparison.Ordinal);
        // Round four: the sentence and the silence are FinishReferencePass's.
        int speak = sync.IndexOf("FinishReferencePass(rig, holder, hardwareLine: rig.PttSourceIsHardware);", StringComparison.Ordinal);
        int end = sync.IndexOf("if (holder is ReferenceEndHolder.Check or ReferenceEndHolder.App)\n                EndTransmissionAfterReferenceTail(rig, holder);",
            StringComparison.Ordinal);
        if (end < 0) end = sync.IndexOf("if (holder is ReferenceEndHolder.Check or ReferenceEndHolder.App)\r\n                EndTransmissionAfterReferenceTail(rig, holder);",
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
        string arm = Body(Dialog("CheckYourAudioDialog.ReferenceAudio.cs"), "private void ReferenceArmChanged(bool armed)");
        int refusal = arm.IndexOf("if (rig.TxFilePlaying && !rig.TxReferenceArmed)", StringComparison.Ordinal);
        int load = arm.IndexOf("WithInjectionPath(rig,", StringComparison.Ordinal);
        Assert.True(refusal > 0 && load > refusal, "the arm refuses before anything loads the player");
        Assert.Contains("audio.check.send_already_playing", arm);
    }
}
