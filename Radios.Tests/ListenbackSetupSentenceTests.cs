#nullable enable
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The round-14 comparison sentences, read ASSEMBLED, as NVDA will speak them
/// (memory: read assembled sentences, not source lines). Every sentence here is
/// DRAFT PROSE (#787) awaiting Noel; the tests pin the assembly, not the words'
/// final form, and a rewording changes the expected strings and nothing else.
/// </summary>
/// <remarks>
/// What these cannot see: the dialog's choice of WHICH sentence to speak on
/// which road (automatic after playback, or the Compare button), which lives
/// in <c>AudioWorkshopDialog.CompareIqPairAsync</c> and needs a press on the
/// bench, step 3 of the brief.
/// </remarks>
public sealed class ListenbackSetupSentenceTests
{
    [Fact]
    public void A_tone_take_after_a_voice_take_is_refused_in_one_sentence_that_asks_for_another_take()
    {
        var refusal = new AudioDifference(false, "unlike-takes:source",
            SetupChanges: new[] { new SetupChange("source", TakeSetup.Voice, TakeSetup.Tone) });
        Assert.Equal(
            "This take used the test tone and your last take used your voice, so I didn't compare them. " +
            "Make another take with the test tone and I'll compare the two.",
            ListenbackResultText.UnlikeSource(refusal));
    }

    [Fact]
    public void A_voice_take_after_a_reference_take_names_the_reference()
    {
        var refusal = new AudioDifference(false, "unlike-takes:source",
            SetupChanges: new[] { new SetupChange("source", TakeSetup.Reference, TakeSetup.Voice) });
        Assert.Equal(
            "This take used your voice and your last take used the reference recording, so I didn't compare them. " +
            "Make another take with your voice and I'll compare the two.",
            ListenbackResultText.UnlikeSource(refusal));
    }

    [Fact]
    public void Setup_changes_are_named_one_sentence_each_in_front_of_the_numbers()
    {
        var compared = new AudioDifference(true, "overall-average", LevelDb: -2.1, SetupChanges: new[]
        {
            new SetupChange("tx-filter", "100 2900", "200 2700"),
            new SetupChange("rf-power", "1", "5"),
            new SetupChange("mic-profile", "Default", "Heil PR 781"),
        });
        Assert.Equal(
            "Your transmit filter was 100 to 2900 hertz on your last take and is 200 to 2700 hertz on this one. " +
            "Your RF power was 1 watts on your last take and is 5 watts on this one. " +
            "Your microphone profile was Default on your last take and is Heil PR 781 on this one.",
            ListenbackResultText.SetupChanges(compared));
    }

    [Fact]
    public void A_missing_profile_name_is_spoken_as_none_and_the_microphone_sentences_read_whole()
    {
        var compared = new AudioDifference(true, "aligned-spans", SetupChanges: new[]
        {
            new SetupChange("mic-profile", "", "Heil PR 781"),
            new SetupChange("mic-input", "PC", "MIC"),
            new SetupChange("pc-microphone-device", "Yeti", "Shure MV7"),
        });
        Assert.Equal(
            "Your microphone profile was none on your last take and is Heil PR 781 on this one. " +
            "Your microphone input was PC on your last take and is MIC on this one. " +
            "Your computer microphone was Yeti on your last take and is Shure MV7 on this one.",
            ListenbackResultText.SetupChanges(compared));
    }

    [Fact]
    public void Nothing_changed_says_nothing_and_the_source_entry_is_never_read_as_a_change()
    {
        Assert.Equal("", ListenbackResultText.SetupChanges(new AudioDifference(true, "aligned-spans")));
        Assert.Equal("", ListenbackResultText.SetupChanges(new AudioDifference(true, "aligned-spans",
            SetupChanges: new[] { new SetupChange("source", TakeSetup.Voice, TakeSetup.Voice) })));
        // A difference with no source record falls back to the older refusal sentence.
        Assert.Equal(
            "These takes have different receiving conditions or too little gap-free keyed audio for a comparison. No numerical comparison was made.",
            ListenbackResultText.UnlikeSource(new AudioDifference(false, "unlike-takes:receive-antenna")));
    }
}
