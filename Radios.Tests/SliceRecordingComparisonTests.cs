#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #999 round 3, ruled C2 by Noel 2026-11-08: two slice recordings' backups MAY
/// be compared with each other in Play takes; a backup against any other kind
/// stays refused, with the existing mixed-kind sentence. Created state: takes
/// written by the real writer with the facts a backup really records
/// (<c>FlexBase.AddSliceRecordingFacts</c>: no source, no transmit chain, the
/// stamp's mode, the slice's receive antenna at the backup), compared by the
/// real engine, the sentence read assembled as Play takes speaks it.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SliceRecordingComparisonTests : IDisposable
{
    private readonly string _root = UnifiedTakeFixtures.NewRoot();
    public void Dispose() => UnifiedTakeFixtures.Remove(_root);

    /// <summary>
    /// A backup's header: the base facts with everything a transmission would
    /// record taken out, as a backup writes it. <paramref name="mode"/> and
    /// <paramref name="receiveAntenna"/> vary so the transmission gates, if they
    /// still ran, would refuse.
    /// </summary>
    private string Backup(int seed = 7, double lowGain = 1, double highGain = 1, string mode = "CW",
        string receiveAntenna = "ANT1", string isolation = "isolated", string serial = "same-radio")
    {
        var facts = new Dictionary<string, string?>
        {
            ["serial"] = serial,
            ["check-method"] = PlaybackTakeRun.SliceBackupMethod,
            ["source"] = null, ["tx-antenna"] = null, ["tx-frequency-mhz"] = null, ["rf-power"] = null,
            ["dummy-load"] = null, ["tx-filter-low"] = null, ["tx-filter-high"] = null,
            ["mic-input"] = null, ["pc-microphone-device"] = null, ["mic-profile"] = null, ["mic-gain"] = null,
            ["compander"] = null, ["compander-level"] = null, ["processor"] = null, ["processor-level"] = null,
            ["mode"] = mode, ["receive-antenna"] = receiveAntenna,
            [SliceRecordingFields.Source] = SliceTakeSource.F11,
            ["capture-isolation"] = isolation,
        };
        return UnifiedTakeFixtures.AudioTake(_root, TakeKind.SliceRecording, seed: seed, lowGain: lowGain,
            highGain: highGain, facts: facts);
    }

    [Fact]
    public void Two_backups_are_compared_on_tone_with_loudness_held_back()
    {
        // Different modes and receive antennas: none of that is a transmission,
        // and none of it may refuse two receive captures.
        string first = Backup(seed: 7, mode: "CW", receiveAntenna: "ANT1");
        string second = Backup(seed: 41, lowGain: 0.5, highGain: 1.5, mode: "FM", receiveAntenna: "ANT2");
        Assert.Null(TakeReader.Inspect(first).Get("source"));
        Assert.Null(TakeReader.Inspect(first).Get("tx-antenna"));

        var result = TakeAnalysis.CompareTakes(first, second);
        Assert.True(result.Comparable, result.Reason);
        Assert.Equal("audio", result.Road);
        Assert.Equal(TakeKind.SliceRecording, result.FirstKind);
        Assert.Equal(TakeKind.SliceRecording, result.SecondKind);
        Assert.Equal("tone", result.Reason);
        Assert.False(result.LevelCompared);
        Assert.Equal("boundary", result.LevelWithheld);
        Assert.InRange(result.TiltDb, 5, 14);
        // No transmit setup is named: a backup records none.
        Assert.Empty(result.SetupChanges ?? Array.Empty<SetupChange>());

        string sentence = ListenbackResultText.AudioRoadSentence(result, chosen: true);
        Assert.StartsWith(Lexicon.Get("audio.takes.level_withheld_boundary") + " This take sounds brighter than the one you chose", sentence);
        Assert.DoesNotContain("voice", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tone instead", sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_backups_of_the_same_audio_compare_as_the_same_tone()
    {
        var result = TakeAnalysis.CompareTakes(Backup(seed: 7), Backup(seed: 7));
        Assert.True(result.Comparable, result.Reason);
        Assert.InRange(result.TiltDb, -0.1, 0.1);
        Assert.EndsWith(Lexicon.Get("audio.takes.tone_same_chosen"),
            ListenbackResultText.AudioRoadSentence(result, chosen: true));
    }

    [Fact]
    public void Two_backups_from_different_radios_name_the_radio()
    {
        var result = TakeAnalysis.CompareTakes(Backup(seed: 7, serial: "one-radio"), Backup(seed: 7, serial: "other-radio"));
        Assert.True(result.Comparable, result.Reason);
        Assert.Equal("radio", Assert.Single(result.SetupChanges!).Key);
    }

    [Fact]
    public void A_backup_that_caught_other_slices_is_still_never_measured()
    {
        // Capture isolation is about the capture, not a transmission, so it
        // still applies between two backups.
        var result = TakeAnalysis.CompareTakes(Backup(seed: 7), Backup(seed: 41, isolation: "contaminated"));
        Assert.False(result.Comparable);
        Assert.Equal("unlike-takes:contaminated", result.Reason);
    }

    /// <summary>
    /// A backup against a check take (Radio recording, Looped back) and against
    /// the other computer takes (Heard on the air, Diagnostic), in both orders:
    /// refused as every cross-kind pair is, with the ruled mixed-kind sentence.
    /// </summary>
    [Theory]
    [InlineData(TakeKind.RadioRecording, false)]
    [InlineData(TakeKind.RadioRecording, true)]
    [InlineData(TakeKind.LoopedBack, false)]
    [InlineData(TakeKind.LoopedBack, true)]
    [InlineData(TakeKind.OffAir, false)]
    [InlineData(TakeKind.OffAir, true)]
    [InlineData(TakeKind.Diagnostic, false)]
    [InlineData(TakeKind.Diagnostic, true)]
    public void A_backup_against_any_other_kind_is_refused(string otherKind, bool backupSecond)
    {
        string backup = Backup(seed: 7);
        string other = TakeKind.IsAudio(otherKind)
            ? UnifiedTakeFixtures.AudioTake(_root, otherKind, seed: 7)
            : UnifiedTakeFixtures.IqTake(_root, otherKind, seed: 7);
        string first = backupSecond ? other : backup, second = backupSecond ? backup : other;

        var result = TakeAnalysis.CompareTakes(first, second);
        Assert.False(result.Comparable);
        Assert.Equal("unlike-takes:kind", result.Reason);
        Assert.Equal(Lexicon.Get("audio.takes.mixed_nothing_fair",
                ("first", TakeKindWords.Phrase(result.FirstKind!)), ("second", TakeKindWords.Phrase(result.SecondKind!))),
            ListenbackResultText.AudioRoadSentence(result, chosen: true));
        Assert.Contains(TakeKind.SliceRecording, new[] { result.FirstKind, result.SecondKind });
        Assert.Contains(otherKind, new[] { result.FirstKind, result.SecondKind });
    }
}
