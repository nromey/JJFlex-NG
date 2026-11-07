using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// What the two FreqOutHandlers ways onto 60 m — typed entry and the band
/// jump — owe the mode, and every sentence each can assemble for it (#312).
/// Pure static decisions: NO WINDOW, no FreqOutHandlers instance, no WPF
/// object is constructed here, so this class is desk-safe.
/// </summary>
/// <remarks>
/// <para>The third way, the channel rotation, is asserted in Radios.Tests
/// (SixtyMeterModeDeliveryTests), which also drives a real FlexBase's
/// RequestMode and reads the sentence past the speech gate. Here each path's
/// own arrival goes through the same <see cref="SixtyMeterModeDelivery.Compose"/>
/// for each outcome, so all three paths' wording is pinned.</para>
/// <para><b>Ruled 2026-10-20 (round two):</b> sent says "switching" and
/// nothing more; a refusal speaks about the MODE only. Round one's sentences
/// ("..., mode set to CW for digital segment", "..., and nothing was changed
/// because you are transmitting") fail these assertions.</para>
/// <para><b>What this cannot see:</b> that <c>CommitQuickType</c> and
/// <c>BandJump</c> hand these to <c>SixtyMeterModeDelivery.Deliver</c>; that is
/// read in the source. Bench half: type 5.360 and Enter on the frequency field
/// while on USB, jump to 60 meters on CW, and listen.</para>
/// </remarks>
public sealed class QuickTypeCommitTests
{
    private static string Say(string arrival, SixtyMeterChannels.ModeCorrection c, ModeRequestOutcome o)
        => SixtyMeterModeDelivery.Compose(arrival, c, o);

    // ---------------- typed entry ----------------

    [Fact]
    public void Typing_a_frequency_inside_the_60m_digital_segment_on_USB_asks_for_CW()
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", 5_360_000, "USB", "5.360 megahertz");

        Assert.Equal("CW", o.Correction?.RequiredMode);
        Assert.Equal("Frequency set to 5.360 megahertz", o.Arrival);
        Assert.Equal("Frequency set to 5.360 megahertz, digital segment needs CW, switching",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.Sent));
        Assert.Equal("Frequency set to 5.360 megahertz, digital segment needs CW, mode not changed while transmitting",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedTransmitting));
        Assert.Equal("Frequency set to 5.360 megahertz, digital segment needs CW, mode not changed",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedNoSlice));
        Assert.Equal("Frequency set to 5.360 megahertz, digital segment needs CW, mode not changed",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedNotRunning));
    }

    [Fact]
    public void Typing_a_60m_channel_on_CW_asks_for_the_channel_mode_as_the_band_jump_does()
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", 5_332_000, "CW", "5.332 megahertz");

        Assert.Equal("USB", o.Correction?.RequiredMode);
        Assert.Equal("Frequency set to 5.332 megahertz, Channel 1 needs USB, switching",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.Sent));
    }

    [Theory]
    [InlineData(5_360_000UL, "DIGU")]
    [InlineData(5_360_000UL, "CW")]
    [InlineData(5_332_000UL, "USB")]
    [InlineData(14_200_000UL, "USB")]
    [InlineData(7_030_000UL, "LSB")]
    public void Anything_already_permitted_or_off_60m_is_unchanged(ulong hz, string mode)
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", hz, mode, "the frequency");

        Assert.Null(o.Correction);
        Assert.Equal("Frequency set to the frequency", o.Arrival);
    }

    [Fact]
    public void With_no_rig_there_is_no_mode_to_correct()
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", 5_360_000, null, "5.360 megahertz");

        Assert.Null(o.Correction);
        Assert.Equal("Frequency set to 5.360 megahertz", o.Arrival);
    }

    // ---------------- thousands separator (Noel, 2026-10-25 20:35) ----------------

    [Theory]
    [InlineData("14,225.000", 14.225)]   // his example: grouped thousands are kilohertz
    [InlineData("14,225", 14.225)]
    [InlineData("7,074", 7.074)]
    [InlineData("14,225.5", 14.2255)]
    [InlineData("14.225", 14.225)]       // unchanged: a decimal with no grouping is megahertz
    [InlineData("14.2", 14.2)]
    [InlineData("14225", 14.225)]        // unchanged: plain digits are kilohertz
    [InlineData("0.5", 0.5)]
    public void A_comma_in_a_typed_frequency_is_a_thousands_separator(string typed, double mhz)
    {
        var parsed = FreqOutHandlers.ParseQuickTypeFreq(typed);
        Assert.NotNull(parsed);
        Assert.Equal(mhz, parsed!.Value, 6);
    }

    [Fact]
    public void Grouping_never_rescues_a_frequency_out_of_range()
    {
        Assert.Null(FreqOutHandlers.ParseQuickTypeFreq("1,000,000"));   // a gigahertz
        Assert.Null(FreqOutHandlers.ParseQuickTypeFreq("14225.000"));   // ungrouped, read as megahertz, as before
    }

    // ---------------- leaving the field (Noel, 2026-10-25 20:49) ----------------

    [Theory]
    [InlineData("Freq", "SMeter", true)]
    [InlineData("Freq", "Slice", true)]
    [InlineData("Freq", null, true)]       // focus left the display altogether
    [InlineData("Freq", "Freq", false)]    // a move within the field keeps the number
    [InlineData("SMeter", "Freq", false)]  // arriving is not leaving
    [InlineData("SMeter", "Mode", false)]
    public void Leaving_the_Frequency_field_ends_an_unfinished_number(string from, string? to, bool ends)
        => Assert.Equal(ends, FreqOutHandlers.LeavingEndsNumberEntry(from, to));

    // ---------------- band jump ----------------

    [Fact]
    public void A_band_jump_to_a_60m_channel_on_CW_asks_for_USB_and_says_only_what_is_known()
    {
        var o = FreqOutHandlers.BandJumpOutcome("60 meter", "5.332 megahertz", true, "US", 5_332_000, "CW");

        Assert.Equal("USB", o.Correction?.RequiredMode);
        Assert.Equal("60 meter band, 5.332 megahertz", o.Arrival);
        Assert.Equal("60 meter band, 5.332 megahertz, Channel 1 needs USB, switching",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.Sent));
        Assert.Equal("60 meter band, 5.332 megahertz, Channel 1 needs USB, mode not changed while transmitting",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedTransmitting));
        Assert.Equal("60 meter band, 5.332 megahertz, Channel 1 needs USB, mode not changed",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedNoSlice));
    }

    [Fact]
    public void A_band_jump_that_needs_no_correction_is_the_old_sentence()
    {
        var onChannel = FreqOutHandlers.BandJumpOutcome("60 meter", "5.332 megahertz", true, "US", 5_332_000, "USB");
        Assert.Null(onChannel.Correction);
        Assert.Equal("60 meter band, 5.332 megahertz", onChannel.Arrival);

        var elsewhere = FreqOutHandlers.BandJumpOutcome("20 meter", "14.200 megahertz", false, "US", 14_200_000, "LSB");
        Assert.Null(elsewhere.Correction);
        Assert.Equal("20 meter band, 14.200 megahertz", elsewhere.Arrival);
    }

    // ---------------- J28: a typed frequency waits for the radio ----------------
    // Ruled by Noel 2026-11-06 16:52: "Terse should just do an error beep and
    // say unable to tune this frequency. Heck, maybe both places could say that."

    [Theory]
    [InlineData(VerbosityLevel.Terse)]
    [InlineData(VerbosityLevel.Chatty)]
    public void A_refused_typed_frequency_beeps_and_says_unable_at_terse_and_chatty(VerbosityLevel level)
    {
        var said = FreqOutHandlers.TypedTuneSpeech(FlexBase.TuneAnswer.Refused, level);
        Assert.True(said.Beep);
        Assert.Equal("Unable to tune this frequency", said.Sentence);
        Assert.False(said.Arrived);
    }

    [Fact]
    public void An_accepted_typed_frequency_says_its_arrival_and_nothing_else()
    {
        var said = FreqOutHandlers.TypedTuneSpeech(FlexBase.TuneAnswer.Accepted, VerbosityLevel.Terse);
        Assert.False(said.Beep);
        Assert.Null(said.Sentence);
        Assert.True(said.Arrived);
    }

    [Fact]
    public void A_typed_frequency_with_no_answer_says_so_without_the_refusal_beep()
    {
        var said = FreqOutHandlers.TypedTuneSpeech(FlexBase.TuneAnswer.NoAnswerYet, VerbosityLevel.Terse);
        Assert.False(said.Beep);
        Assert.Equal("No answer from the radio yet", said.Sentence);
        Assert.False(said.Arrived);
    }

    [Fact]
    public void The_commit_no_longer_says_the_frequency_was_set_before_the_radio_answers()
    {
        // Read in the source, as this class reads the delivery: the commit
        // hands its tune to the rig and says nothing about the frequency until
        // the answer comes; the arrival sentence is said only on Accepted.
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "JJFlexWpf", "FreqOutHandlers.cs"));
        int commit = text.IndexOf("private void CommitQuickType()", System.StringComparison.Ordinal);
        int end = text.IndexOf("internal static (SixtyMeterChannels.ModeCorrection? Correction, string Arrival) QuickTypeCommitOutcome(", commit, System.StringComparison.Ordinal);
        Assert.True(commit > 0 && end > commit);
        string body = text.Substring(commit, end - commit);
        Assert.Contains("TuneAwaitingAnswer(freqHz,", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("SetRXFrequency?.Invoke(freqHz)", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("QuickTypeCommitOutcome(", body.Substring(0, body.IndexOf("TuneAwaitingAnswer(freqHz,", System.StringComparison.Ordinal)), System.StringComparison.Ordinal);
        int say = text.IndexOf("private void SayTypedTuneAnswer(", System.StringComparison.Ordinal);
        Assert.True(say > 0, "nothing says the radio's answer to a typed frequency");
        string sayBody = text.Substring(say, 2500);
        int speech = text.IndexOf("private static bool SayTypedTuneSpeech(", System.StringComparison.Ordinal);
        Assert.True(speech > 0);
        Assert.Contains("EarconPlayer.LeaderInvalidTone();", text.Substring(speech, 800), System.StringComparison.Ordinal);
        Assert.True(sayBody.IndexOf("if (!said.Arrived) return;", System.StringComparison.Ordinal)
            < sayBody.IndexOf("QuickTypeCommitOutcome(", System.StringComparison.Ordinal),
            "the arrival is said before the radio accepted the tune");
        // Round five (T1): asked first, as the window runs it; and only the typed tune's own rig is acted on.
        int stale = sayBody.IndexOf("TypedTuneAnswerStale(Rig, tune, answer)", System.StringComparison.Ordinal);
        Assert.True(stale > 0 && stale < sayBody.IndexOf("TypedTuneSpeech(answer", System.StringComparison.Ordinal),
            "the answer is said before it is asked whether it is still about the radio");
        Assert.Contains("var rig = tune.Rig;", sayBody, System.StringComparison.Ordinal);
        Assert.DoesNotContain("var rig = Rig;", sayBody, System.StringComparison.Ordinal);
    }

    // ---------------- Round five, Codex's re-check T1: the answer's authority ----------------

    private static FlexBase.TypedTune RefusedTuneOn(FlexBase rig)
    {
        // A rig with no radio refuses at once, and hands back the tune it was for.
        FlexBase.TypedTune? got = null;
        rig.TuneAwaitingAnswer(14_200_000, (answer, tune) => got = tune);
        Assert.NotNull(got);
        return got!;
    }

    [Fact]
    public void An_answer_for_a_rig_the_window_no_longer_holds_is_not_said_or_acted_on()
    {
        // Codex's rig-switch schedule: the answer reached the window after its
        // rig had been replaced, and the 60 m correction switched the
        // REPLACEMENT radio to CW.
        var original = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
        var replacement = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
        var tune = RefusedTuneOn(original);
        Assert.Same(original, tune.Rig);
        Assert.Null(FreqOutHandlers.TypedTuneAnswerStale(original, tune, FlexBase.TuneAnswer.Refused));   // control
        Assert.NotNull(FreqOutHandlers.TypedTuneAnswerStale(replacement, tune, FlexBase.TuneAnswer.Refused));
        Assert.NotNull(FreqOutHandlers.TypedTuneAnswerStale(null, tune, FlexBase.TuneAnswer.Refused));
    }
}
