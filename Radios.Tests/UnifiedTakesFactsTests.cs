#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, part 4b: a playback take's header facts, read
/// from a real radio object at recording admission, are the set the
/// comparison reads (<c>UnifiedTakeFixtures.BaseFacts</c>), with the boundary
/// unverified and no IQ fact invented.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesFactsTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(UnifiedTakesFactsTests));
    private readonly string _root = UnifiedTakeFixtures.NewRoot();
    public void Dispose() { UnifiedTakeFixtures.Remove(_root); _scope.Dispose(); }

    private static string Value(IReadOnlyList<KeyValuePair<string, string>> facts, string key) =>
        facts.LastOrDefault(f => f.Key == key).Value;

    /// <summary>A take written by the real writer from the radio's own facts, as the check will write one.</summary>
    internal static string WriteFrom(string root, IReadOnlyList<KeyValuePair<string, string>> facts, string kind, string source,
        int seed = 7, double highGain = 1)
    {
        var all = facts.Concat(new[] { new KeyValuePair<string, string>("source", source),
            new KeyValuePair<string, string>("capture-isolation", "isolated") }).ToList();
        var writer = new AudioTakeWriter(AudioTakeWriter.NewDirectory(root), kind, all);
        writer.BeginPlayback();
        var body = UnifiedTakeFixtures.Speechlike(seed, AudioTakeFields.Rate * 2, highGain: highGain);
        writer.Write(body, body.Length);
        writer.EndPlayback();
        writer.Close(true, "observed-stop");
        return writer.DirectoryPath;
    }

    [Fact]
    public void A_radio_recording_records_every_fact_the_comparison_reads()
    {
        using var c = new RadioCase();
        Field(c.Tx, "_rxant", "ANT1"); // as the radio reports it
        var facts = c.F.Rig.PlaybackTakeFacts(TakeKind.RadioRecording, SliceRecorderTarget.TransmitSlice, "record-playback");

        foreach (string key in UnifiedTakeFixtures.BaseFacts().Keys.Where(k => k != "source"))
            Assert.False(Value(facts, key) == null, "missing: " + key);
        foreach (var field in TakeSetup.Registered)
            Assert.False(Value(facts, field.Key) == null, "missing registered field: " + field.Key);
        Assert.Equal(c.F.Vendor.Radio.Serial, Value(facts, "serial"));
        Assert.Equal("FLEX-8600", Value(facts, "model"));
        Assert.Equal("LAN", Value(facts, "transport"));
        Assert.Equal("USB", Value(facts, "mode"));
        Assert.Equal("ANT1", Value(facts, "tx-antenna"));
        Assert.Equal("1", Value(facts, "recorded-slice-index"));
        Assert.Equal("status-arrival", Value(facts, AudioTakeFields.BoundaryBasis));
        Assert.Equal(AudioTakeFields.Unverified, Value(facts, AudioTakeFields.BoundaryQuality));
        // No IQ fact is invented for an audio take.
        foreach (string iq in new[] { "rate-echoed", "pan-centre-mhz", "render-scale", "classifier" })
            Assert.Null(Value(facts, iq));
        // Looped back's receive chain belongs to Looped back only.
        Assert.Null(Value(facts, "agc-mode"));
    }

    [Fact]
    public void A_receive_antenna_the_radio_has_not_reported_is_left_out_not_guessed()
    {
        using var c = new RadioCase();
        var facts = c.F.Rig.PlaybackTakeFacts(TakeKind.RadioRecording, SliceRecorderTarget.TransmitSlice, "record-playback");
        Assert.Null(Value(facts, "receive-antenna"));
    }

    [Fact]
    public void A_looped_back_take_also_records_the_listening_receivers_chain()
    {
        using var c = new RadioCase();
        var facts = c.F.Rig.PlaybackTakeFacts(TakeKind.LoopedBack, SliceRecorderTarget.TransmitSlice, "loopback");
        foreach (string key in new[] { "rf-gain", "agc-mode", "agc-threshold", "rx-filter-low", "rx-filter-high" })
            Assert.False(Value(facts, key) == null, "missing: " + key);
    }

    [Fact]
    public void The_source_is_read_at_the_keying_edge()
    {
        using var c = new RadioCase();
        Assert.Equal(TakeSetup.Voice, c.F.Rig.PlaybackTakeSource);
    }

    [Fact]
    public void Two_takes_written_from_the_radios_own_facts_compare_their_tone_and_hold_loudness_back()
    {
        // Created state: written by the real writer, read by the reader,
        // listed by the catalog, compared by the engine.
        using var c = new RadioCase();
        var facts = c.F.Rig.PlaybackTakeFacts(TakeKind.RadioRecording, SliceRecorderTarget.TransmitSlice, "record-playback");
        string first = WriteFrom(_root, facts, TakeKind.RadioRecording, TakeSetup.Voice, seed: 3);
        string second = WriteFrom(_root, facts, TakeKind.RadioRecording, TakeSetup.Voice, seed: 4, highGain: 2);

        Assert.Equal(2, TakeCatalog.List(_root).Count(e => e.Kind == TakeKind.RadioRecording));
        Assert.True(TakeReader.Inspect(first).CanMeasure);
        var difference = TakeAnalysis.CompareTakes(first, second);
        Assert.True(difference.Comparable, difference.Reason);
        Assert.False(difference.LevelCompared);
        Assert.Equal("boundary", difference.LevelWithheld);
        Assert.Empty(difference.SetupChanges ?? Array.Empty<SetupChange>());
    }
}
