#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes part 2, the header contract (design section 7): every
/// registered transmit-chain field is STORED by the snapshot a new take
/// writes, and NAMED in an assembled sentence when it changes. A field that
/// is recorded and never named would let its effect be put down to something
/// else; a field named and never recorded cannot be named at all.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesContractTests
{
    private static AudioChainPreset Chain() => new()
    {
        SchemaVersion = AudioChainPreset.CurrentSchemaVersion,
        MicGain = 50, MicBoost = false, MicBias = false, CompanderOn = true, CompanderLevel = 50,
        SpeechProcessorOn = false, SpeechProcessorLevel = 0, TxFilterLow = 100, TxFilterHigh = 2900,
        RadioMicInput = "PC", PcInputDevice = "Yeti",
        TxEqCaptured = true, TxEqEnabled = true,
        TxEq32 = 0, TxEq63 = 0, TxEq125 = 0, TxEq250 = 0, TxEq500 = 0, TxEq1000 = 0, TxEq2000 = 0, TxEq4000 = 0, TxEq8000 = 0,
    };

    private static TakeSetup.SetupSource Source() => new(Chain(), "Default", 10, "Yeti");

    /// <summary>
    /// One change to one registered field. A registered field with no case
    /// here fails the contract test outright: a field joins the registry WITH
    /// its proof, or not at all.
    /// </summary>
    private static TakeSetup.SetupSource Vary(string key, TakeSetup.SetupSource s)
    {
        var c = s.Chain;
        switch (key)
        {
            case "mic-input": c.RadioMicInput = "MIC"; return s;
            case "pc-microphone-device": return s with { PcMicrophoneDevice = "Shure MV7" };
            case "mic-profile": return s with { MicProfile = "Heil PR 781" };
            case "mic-gain": c.MicGain = 62; return s;
            case "mic-boost": c.MicBoost = true; return s;
            case "mic-bias": c.MicBias = true; return s;
            case "compander": c.CompanderOn = false; return s;
            case "compander-level": c.CompanderLevel = 70; return s;
            case "processor": c.SpeechProcessorOn = true; return s;
            case "processor-level": c.SpeechProcessorLevel = 2; return s;
            // The positive control named in the design: a changed EQ band, the
            // lowest one, which the old repeated reads could miss entirely.
            case "tx-equalizer": c.TxEq32 = 4; return s;
            case "tx-filter-low": c.TxFilterLow = 200; return s;
            case "tx-filter-high": c.TxFilterHigh = 2700; return s;
            case "rf-power": return s with { RfPower = 25 };
            default: throw new Xunit.Sdk.XunitException("No variation for registered field " + key +
                ": add one, with the words its sentence must contain.");
        }
    }

    /// <summary>A word the assembled sentence must contain for each varied field.</summary>
    private static string Evidence(string key) => key switch
    {
        "mic-input" => "MIC",
        "pc-microphone-device" => "Shure MV7",
        "mic-profile" => "Heil PR 781",
        "mic-gain" => "62",
        "mic-boost" => "Mic boost was off",
        "mic-bias" => "Mic bias was off",
        "compander" => "compander was on at 50",
        "compander-level" => "on at 70",
        "processor" => "speech processor was off",
        "processor-level" => "DXX",
        "tx-equalizer" => "32 hertz from 0 to +4 dB",
        "tx-filter-low" => "200 to 2900",
        "tx-filter-high" => "100 to 2700",
        "rf-power" => "25 watts",
        _ => throw new Xunit.Sdk.XunitException("No evidence for " + key)
    };

    private static string Take(string root, TakeSetup.SetupSource source) =>
        UnifiedTakeFixtures.AudioTake(root, facts: TakeSetup.Snapshot(source).ToDictionary(f => f.Key, f => (string?)f.Value));

    [Fact]
    public void Every_registered_field_is_stored_by_a_new_take_and_named_when_it_alone_changes()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            string baseline = Take(root, Source());
            var before = TakeReader.Inspect(baseline);
            foreach (var field in TakeSetup.Registered)
            {
                string changed = Take(root, Vary(field.Key, Source()));
                var after = TakeReader.Inspect(changed);
                // Stored: both headers carry the fact, and it differs.
                Assert.NotNull(before.Get(field.Key));
                Assert.NotNull(after.Get(field.Key));
                Assert.NotEqual(before.Get(field.Key), after.Get(field.Key));
                // Named: exactly one change, in this field's group, assembled.
                var changes = TakeSetup.Changes(before, after);
                Assert.True(changes.Count == 1, field.Key + " produced " + changes.Count + " changes");
                Assert.Equal(field.Group, TakeSetup.GroupOf(changes[0].Key));
                string sentence = ListenbackResultText.SetupChanges(new AudioDifference(true, "test", SetupChanges: changes));
                Assert.Contains(Evidence(field.Key), sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("More than one setting", sentence, StringComparison.Ordinal);
            }
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void The_snapshot_writes_exactly_the_registered_fields_and_never_reports_an_unreported_eq_as_flat()
    {
        var snapshot = TakeSetup.Snapshot(Source());
        Assert.Equal(TakeSetup.Registered.Select(f => f.Key), snapshot.Select(f => f.Key));
        var chain = Chain();
        chain.TxEqCaptured = false;
        var unknown = TakeSetup.Snapshot(new(chain, "Default", 10, "Yeti"));
        Assert.Equal("null", unknown.Single(f => f.Key == "tx-equalizer").Value);
        Assert.Null(TakeSetup.Equalizer("null"));
    }

    [Fact]
    public void The_off_air_header_writes_every_registered_field_it_has_always_written_and_reads_the_eq_once()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.Listenback.cs"));
        // mic-boost and mic-bias are new with Unified takes and recorded by
        // playback takes only; an old header's absence reads as unknown.
        foreach (var field in TakeSetup.Registered.Where(f => f.Key is not ("mic-boost" or "mic-bias" or "tx-equalizer")))
            Assert.Contains("[\"" + field.Key + "\"]", source, StringComparison.Ordinal);
        Assert.Contains("Event(\"tx-equalizer\"", source, StringComparison.Ordinal);
        Assert.Contains("TakeSetup.TxEqualizerRecord(", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(source, "GetTxEq()"));
    }

    [Fact]
    public void Two_groups_changing_adds_the_several_settings_warning_and_a_switch_with_its_level_is_one_group()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var before = TakeReader.Inspect(Take(root, Source()));
            var compander = Source(); compander.Chain.CompanderOn = false; compander.Chain.CompanderLevel = 70;
            var one = TakeSetup.Changes(before, TakeReader.Inspect(Take(root, compander)));
            Assert.Single(one);
            Assert.DoesNotContain("More than one setting",
                ListenbackResultText.SetupChanges(new AudioDifference(true, "t", SetupChanges: one)), StringComparison.Ordinal);
            var two = Source(); two.Chain.MicGain = 70; two = two with { RfPower = 50 };
            var both = TakeSetup.Changes(before, TakeReader.Inspect(Take(root, two)));
            Assert.Equal(
                "Your mic gain was 50 on your last take and is 70 on this one. " +
                "Your RF power was 10 watts on your last take and is 50 watts on this one. " +
                "More than one setting changed between these takes, so a difference can't be put down to any one of them.",
                ListenbackResultText.SetupChanges(new AudioDifference(true, "t", SetupChanges: both)));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_fact_an_older_header_never_recorded_is_never_said_to_have_changed()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var facts = TakeSetup.Snapshot(Source()).ToDictionary(f => f.Key, f => (string?)f.Value);
            facts["mic-boost"] = null; facts["tx-equalizer"] = null;
            var old = TakeReader.Inspect(UnifiedTakeFixtures.AudioTake(root, facts: facts));
            var boosted = Source(); boosted.Chain.MicBoost = true; boosted.Chain.TxEq1000 = 6;
            Assert.Empty(TakeSetup.Changes(old, TakeReader.Inspect(Take(root, boosted))));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    [Fact]
    public void A_switched_off_eq_whose_stored_bands_changed_is_named_as_stored_settings()
    {
        string root = UnifiedTakeFixtures.NewRoot();
        try
        {
            var off = Source(); off.Chain.TxEqEnabled = false;
            var offChanged = Source(); offChanged.Chain.TxEqEnabled = false; offChanged.Chain.TxEq63 = -3; offChanged.Chain.TxEq8000 = 2;
            var changes = TakeSetup.Changes(TakeReader.Inspect(Take(root, off)), TakeReader.Inspect(Take(root, offChanged)));
            Assert.Equal(
                "Your transmit equalizer is off, and its stored settings changed at 63 hertz from 0 to -3 dB and 8000 hertz from 0 to +2 dB.",
                ListenbackResultText.SetupChanges(new AudioDifference(true, "t", SetupChanges: changes)));
        }
        finally { UnifiedTakeFixtures.Remove(root); }
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
