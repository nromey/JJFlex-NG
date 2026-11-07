#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes part 6, comparison (design sections 2 and 3, with the build
/// brief's one loudness rule): TakeAnalysis.CompareTakes stays the one entry
/// point; a pair involving a playback take compares only what is fair and
/// says what was left out, in the ruled mixed-kind sentences. Created state:
/// takes written by the real writers, compared by the real engine, the
/// sentence read assembled.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesComparisonTests
{
    private static Dictionary<string, string?> With(params (string Key, string? Value)[] facts) =>
        facts.ToDictionary(f => f.Key, f => f.Value);

    [Fact]
    public void The_playback_boundary_is_unverified_on_both_transports_until_the_bench_says_otherwise()
    {
        // Flipping either is a ruling with a measurement behind it, never a fix.
        Assert.False(PlaybackBoundary.IsVerified("LAN"));
        Assert.False(PlaybackBoundary.IsVerified("SmartLink"));
        Assert.Equal(0.25, PlaybackBoundary.GuardSeconds);
    }

    [Fact]
    public void Two_radio_recordings_compare_tone_and_hold_loudness_back_while_the_boundary_is_unverified()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string first = UnifiedTakeFixtures.AudioTake(root, seed: 7);
            string second = UnifiedTakeFixtures.AudioTake(root, seed: 41, lowGain: 0.5, highGain: 1.5, level: 0.4);
            var result = TakeAnalysis.CompareTakes(first, second);
            Assert.True(result.Comparable, result.Reason);
            Assert.Equal("audio", result.Road);
            Assert.False(result.LevelCompared);
            Assert.Equal("boundary", result.LevelWithheld);
            Assert.Equal(0, result.LevelDb);
            Assert.InRange(result.TiltDb, 5, 14);
            string sentence = ListenbackResultText.AudioRoadSentence(result);
            Assert.StartsWith("Only the tone of these takes is compared. Where the radio's playback starts and ends " +
                "hasn't been measured yet, so their loudness is held back. Your new take sounds brighter than your last take, " +
                "with highs stronger relative to lows by ", sentence);
            Assert.DoesNotContain("louder", sentence);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Same_audio_compares_as_the_same_tone()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var result = TakeAnalysis.CompareTakes(UnifiedTakeFixtures.AudioTake(root, seed: 7), UnifiedTakeFixtures.AudioTake(root, seed: 7));
            Assert.True(result.Comparable, result.Reason);
            Assert.InRange(result.TiltDb, -0.1, 0.1);
            Assert.EndsWith("Your new take has the same tone as your last take.", ListenbackResultText.AudioRoadSentence(result));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Lan_against_smartlink_and_a_changed_slice_volume_each_say_why_only_tone_is_compared()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string lan = UnifiedTakeFixtures.AudioTake(root);
            var remote = TakeAnalysis.CompareTakes(lan, UnifiedTakeFixtures.AudioTake(root, facts: With(("transport", "SmartLink"))));
            Assert.Equal("transport", remote.LevelWithheld);
            Assert.StartsWith("One of these takes came over SmartLink and the other didn't, so only their tone is compared, not their loudness.",
                ListenbackResultText.AudioRoadSentence(remote));
            var louder = TakeAnalysis.CompareTakes(lan, UnifiedTakeFixtures.AudioTake(root, facts: With(("slice-audio-gain", "70"))));
            Assert.Equal("slice-volume", louder.LevelWithheld);
            Assert.StartsWith("The radio's slice volume was different for these takes, so only their tone is compared, not their loudness.",
                ListenbackResultText.AudioRoadSentence(louder));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_radio_recording_against_a_looped_back_take_says_so_in_the_ruled_sentence_and_compares_tone_only()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var result = TakeAnalysis.CompareTakes(UnifiedTakeFixtures.AudioTake(root, TakeKind.RadioRecording),
                UnifiedTakeFixtures.AudioTake(root, TakeKind.LoopedBack));
            Assert.True(result.Comparable, result.Reason);
            Assert.Equal("kinds", result.LevelWithheld);
            Assert.StartsWith("One of these takes was a radio recording and the other was looped back, so only their tone is compared.",
                ListenbackResultText.AudioRoadSentence(result));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Heard_on_the_air_against_a_radio_recording_compares_tone_on_the_same_radio_and_refuses_another()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string offAir = UnifiedTakeFixtures.IqTake(root, TakeKind.OffAir);
            var result = TakeAnalysis.CompareTakes(offAir, UnifiedTakeFixtures.AudioTake(root));
            Assert.True(result.Comparable, result.Reason);
            Assert.StartsWith("One of these takes was heard on the air and the other was a radio recording, so only their tone is compared.",
                ListenbackResultText.AudioRoadSentence(result));
            var other = TakeAnalysis.CompareTakes(offAir, UnifiedTakeFixtures.AudioTake(root, facts: With(("serial", "other-radio"))));
            Assert.False(other.Comparable);
            Assert.Equal("unlike-takes:serial", other.Reason);
            Assert.Equal("A take heard on the air can only be compared with one from the same radio, and these were made on different radios.",
                ListenbackResultText.AudioRoadSentence(other));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_diagnostic_take_against_a_playback_take_is_refused_in_the_ruled_nothing_fair_sentence()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var result = TakeAnalysis.CompareTakes(UnifiedTakeFixtures.IqTake(root, TakeKind.Diagnostic),
                UnifiedTakeFixtures.AudioTake(root));
            Assert.False(result.Comparable);
            Assert.Equal("One of these takes was a diagnostic take and the other was a radio recording, and nothing in them can be " +
                "compared fairly. Pick two takes made the same way for a useful comparison.",
                ListenbackResultText.AudioRoadSentence(result));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void Two_playback_takes_from_different_radios_compare_and_name_the_radio_after_the_microphone()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var result = TakeAnalysis.CompareTakes(UnifiedTakeFixtures.AudioTake(root),
                UnifiedTakeFixtures.AudioTake(root, facts: With(("serial", "other"), ("model", "FLEX-6300"), ("nickname", "Shack"),
                    ("mic-profile", "Heil"))));
            Assert.True(result.Comparable, result.Reason);
            Assert.Equal(new[] { "mic-profile", "radio" }, result.SetupChanges!.Select(c => c.Key));
            Assert.Contains("Your microphone profile was Default on your last take and is Heil on this one. " +
                "Your last take was made on FLEX-8600 Bench and this one on FLEX-6300 Shack. More than one setting changed",
                ListenbackResultText.AudioRoadSentence(result));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Theory]
    [InlineData("source", "unlike-takes:source")]
    [InlineData("tx-antenna", "unlike-takes:tx-antenna")]
    [InlineData("mode", "unlike-takes:mode")]
    public void A_different_source_antenna_or_mode_is_a_refusal(string fact, string reason)
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string changed = fact switch { "source" => TakeSetup.Tone, "tx-antenna" => "ANT1", _ => "LSB" };
            var result = TakeAnalysis.CompareTakes(UnifiedTakeFixtures.AudioTake(root),
                UnifiedTakeFixtures.AudioTake(root, facts: With((fact, changed))));
            Assert.False(result.Comparable);
            Assert.Equal(reason, result.Reason);
            Assert.NotEqual("", ListenbackResultText.AudioRoadSentence(result));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_playback_takes_own_figures_never_claim_splatter()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var stats = TakeStatistics.Measure(UnifiedTakeFixtures.AudioTake(root));
            Assert.True(stats.Available, stats.Reason);
            Assert.False(stats.SplatterAvailable);
            Assert.True(double.IsFinite(stats.Lufs) && stats.Lufs < 0);
            // An IQ take's statistics are untouched and still report splatter.
            Assert.True(TakeStatistics.Measure(UnifiedTakeFixtures.IqTake(root, TakeKind.OffAir)).SplatterAvailable);
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }
}
