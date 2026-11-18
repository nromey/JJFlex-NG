using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track H, item F: transmit noise setups held per radio, the
/// microphone's pointer to one, and the migration of each microphone's old
/// cleanup settings into a named setup, which must never lose a value.
/// </summary>
public sealed class TxNoiseSetupStoreTests : IDisposable
{
    private const string Radio8600 = "1234-5678-9012-3456";
    private const string Radio6300 = "2222-3333-4444-5555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-txnoise-" + Guid.NewGuid().ToString("N"));
    private TxNoiseSetupStore Store => new(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static TxNoiseSetup Sample(string name, bool neuralOn = true, float strength = 0.6f) => new()
    {
        Name = name,
        Spectral = new TxNoiseSlotSetup
        {
            Enabled = true,
            EngineId = "nr4",
            Engines = { new DenoiseEngineSettings { EngineId = "nr4", Direction = DenoiseDirection.Transmit, Strength = 1f,
                Values = { new DenoiseNamedValue { Name = "reduction_db", Number = 12 } } } },
        },
        Neural = new TxNoiseSlotSetup
        {
            Enabled = neuralOn,
            EngineId = "nnr",
            Engines = { new DenoiseEngineSettings { EngineId = "nnr", Direction = DenoiseDirection.Transmit, Strength = strength,
                Values = { new DenoiseNamedValue { Name = "model", Choice = "premium" } } } },
        },
        Gate = new TxNoiseGateSetup { Enabled = true, ThresholdMarginDb = 10, HoldMs = 300 },
    };

    [Fact]
    public void A_setup_round_trips_through_the_file_beside_the_radio_config()
    {
        var store = Store;
        Assert.True(store.Save(Radio8600, Sample("Studio mic")).Succeeded);
        Assert.Equal(Path.Combine(_root, "radios", Radio8600, TxNoiseSetupStore.FileName), store.PathFor(Radio8600));
        Assert.True(File.Exists(store.PathFor(Radio8600)));

        var back = new TxNoiseSetupStore(_root).Find(Radio8600, "studio MIC");
        Assert.NotNull(back);
        Assert.True(back!.SameContentAs(Sample("whatever")));
        Assert.Equal("Studio mic", back.Name);
        Assert.Equal("premium", back.Neural.Engines.Single().Values.Single().Choice);
        Assert.Equal(DenoiseDirection.Transmit, back.Neural.Engines.Single().Direction);
        Assert.Equal(300f, back.Gate.HoldMs);
        // Per radio: the other radio has nothing.
        Assert.Empty(store.List(Radio6300));
    }

    [Fact]
    public void Saving_a_name_again_replaces_it_in_place_and_the_store_hands_out_copies()
    {
        var store = Store;
        store.Save(Radio8600, Sample("A"));
        store.Save(Radio8600, Sample("B"));
        store.Save(Radio8600, Sample("a", strength: 0.2f));
        Assert.Equal(new[] { "a", "B" }, store.Names(Radio8600));
        var copy = store.Find(Radio8600, "A")!;
        Assert.Equal(0.2f, copy.Neural.Engines[0].Strength);
        copy.Neural.Engines[0].Strength = 0.9f;
        Assert.Equal(0.2f, store.Find(Radio8600, "A")!.Neural.Engines[0].Strength);
    }

    [Fact]
    public void Rename_refuses_a_name_another_setup_holds_and_allows_a_change_of_case()
    {
        var store = Store;
        store.Save(Radio8600, Sample("Studio"));
        store.Save(Radio8600, Sample("Headset"));
        Assert.Equal(TxNoiseSetupOutcome.NameTaken, store.Rename(Radio8600, "Studio", "headset").Outcome);
        Assert.Equal(TxNoiseSetupOutcome.NotFound, store.Rename(Radio8600, "Desk", "Other").Outcome);
        Assert.Equal(TxNoiseSetupOutcome.BadName, store.Rename(Radio8600, "Studio", "  ").Outcome);
        Assert.True(store.Rename(Radio8600, "Studio", "STUDIO").Succeeded);
        Assert.True(store.Rename(Radio8600, "studio", "Desk").Succeeded);
        Assert.Equal(new[] { "Desk", "Headset" }, store.Names(Radio8600));
    }

    [Fact]
    public void Delete_removes_only_the_named_setup()
    {
        var store = Store;
        store.Save(Radio8600, Sample("A"));
        store.Save(Radio8600, Sample("B"));
        Assert.True(store.Delete(Radio8600, "a").Succeeded);
        Assert.Equal(TxNoiseSetupOutcome.NotFound, store.Delete(Radio8600, "a").Outcome);
        Assert.Equal(new[] { "B" }, store.Names(Radio8600));
    }

    [Fact]
    public void Copying_a_setup_to_another_radio_is_explicit_and_never_overwrites_unasked()
    {
        var store = Store;
        store.Save(Radio8600, Sample("Studio", strength: 0.5f));
        Assert.True(store.CopyToRadio(Radio8600, "studio", Radio6300).Succeeded);
        Assert.True(store.Find(Radio6300, "Studio")!.SameContentAs(store.Find(Radio8600, "Studio")!));

        store.Save(Radio8600, Sample("Studio", strength: 0.1f));
        Assert.Equal(TxNoiseSetupOutcome.NameTaken, store.CopyToRadio(Radio8600, "Studio", Radio6300).Outcome);
        Assert.Equal(0.5f, store.Find(Radio6300, "Studio")!.Neural.Engines[0].Strength);
        Assert.True(store.CopyToRadio(Radio8600, "Studio", Radio6300, replace: true).Succeeded);
        Assert.Equal(0.1f, store.Find(Radio6300, "Studio")!.Neural.Engines[0].Strength);
        Assert.Equal(TxNoiseSetupOutcome.NotFound, store.CopyToRadio(Radio8600, "Nope", Radio6300).Outcome);
        Assert.Equal(TxNoiseSetupOutcome.BadRadio, store.CopyToRadio(Radio8600, "Studio", "not-a-serial").Outcome);
    }

    [Fact]
    public void A_name_that_is_not_a_radio_serial_is_refused_and_writes_nothing()
    {
        var store = Store;
        Assert.Equal(TxNoiseSetupOutcome.BadRadio, store.Save("..\\..\\evil", Sample("A")).Outcome);
        Assert.Equal(TxNoiseSetupOutcome.BadName, store.Save(Radio8600, Sample("   ")).Outcome);
        Assert.Null(store.PathFor(""));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void An_unreadable_file_is_moved_aside_and_the_change_that_found_it_is_refused()
    {
        var store = Store;
        string path = store.PathFor(Radio8600)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<not xml");
        var result = store.Save(Radio8600, Sample("A"));
        Assert.Equal(TxNoiseSetupOutcome.Unreadable, result.Outcome);
        Assert.NotNull(result.SidelinedPath);
        Assert.True(File.Exists(result.SidelinedPath));
        Assert.Equal("<not xml", File.ReadAllText(result.SidelinedPath!));
        // The next save starts a fresh file; the operator's old one is kept aside.
        Assert.True(store.Save(Radio8600, Sample("A")).Succeeded);
    }

    [Fact]
    public void The_default_store_lives_under_the_settings_root()
    {
        var store = TxNoiseSetupStore.Default();
        Assert.NotNull(store);
        Assert.StartsWith(Path.Combine(RadioConfig.AppDataRoot, "radios"), store!.PathFor(Radio8600));
    }

    // ── The migration ──

    /// <summary>Every field of the old shape set to a value none of its defaults has.</summary>
    private static TxConditioningSettings Distinctive() => new()
    {
        Version = 1,
        NrEnabled = true,
        NrStrength = 0.37f,
        GateEnabled = true,
        AutoThreshold = false,
        ThresholdMarginDb = 11.5f,
        GateThresholdDb = -47.25f,
        GateAttackMs = 7f,
        GateHoldMs = 425f,
        GateReleaseMs = 650f,
        GateRangeDb = 33f,
    };

    [Fact]
    public void The_migration_carries_every_field_of_the_old_settings()
    {
        var old = Distinctive();
        var defaults = new TxConditioningSettings();
        // Every property must differ from its default here, or the test proves nothing for it.
        foreach (var p in typeof(TxConditioningSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.Name == nameof(TxConditioningSettings.Version)) continue; // schema metadata, not a setting
            Assert.False(Equals(p.GetValue(old), p.GetValue(defaults)), p.Name + " is not distinctive in this test");
        }

        var store = Store;
        var profile = new MicrophoneProfile { Name = "Studio mic", Capture = new MicCaptureSettings { Conditioning = old } };
        var result = TxNoiseSetupMigration.Migrate(profile, Radio8600, "FLEX-8600", store);
        Assert.Equal(TxNoiseMigrationOutcome.Created, result.Outcome);

        // Through the file and back, then into the old shape: every value is there.
        var setup = new TxNoiseSetupStore(_root).Find(Radio8600, "Studio mic")!;
        var back = TxNoiseSetupMigration.ToConditioning(setup);
        foreach (var p in typeof(TxConditioningSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.True(Equals(p.GetValue(old), p.GetValue(back)), p.Name + " was lost by the migration");

        Assert.Equal(TxNoiseSetupMigration.LegacyEngineId, setup.Neural.EngineId);
        Assert.Equal(DenoiseDirection.Transmit, setup.Neural.Engines.Single().Direction);
        Assert.False(setup.Spectral.Enabled);
        // Left in place for older builds reading the same file.
        Assert.Same(old, profile.Capture.Conditioning);
    }

    [Fact]
    public void A_migrated_microphone_points_at_its_setup_and_moves_only_once()
    {
        var store = Store;
        var profile = new MicrophoneProfile { Name = "Studio mic", Capture = new MicCaptureSettings { Conditioning = Distinctive() } };
        profile.SetSetupFor(new RadioProfileReference { RadioId = Radio8600, ProfileName = "SSB" });
        var first = TxNoiseSetupMigration.Migrate(profile, Radio8600, "FLEX-8600", store);
        Assert.True(first.ProfileChanged);
        Assert.Equal("Studio mic", profile.NoiseSetupNameFor(Radio8600));
        Assert.Equal("SSB", ((RadioProfileReference)profile.FindSetupFor(Radio8600)!).ProfileName);

        var again = TxNoiseSetupMigration.Migrate(profile, Radio8600, "FLEX-8600", store);
        Assert.Equal(TxNoiseMigrationOutcome.AlreadyPointed, again.Outcome);
        Assert.False(again.ProfileChanged);
        Assert.Single(store.List(Radio8600));

        // Another radio is a separate move, into that radio's own store.
        var other = TxNoiseSetupMigration.Migrate(profile, Radio6300, "FLEX-6300", store);
        Assert.Equal(TxNoiseMigrationOutcome.Created, other.Outcome);
        Assert.Single(store.List(Radio6300));
    }

    [Fact]
    public void A_microphone_with_no_radio_binding_gets_an_empty_reference_holding_only_the_pointer()
    {
        var store = Store;
        var profile = new MicrophoneProfile { Name = "Headset", Capture = new MicCaptureSettings { Conditioning = Distinctive() } };
        TxNoiseSetupMigration.Migrate(profile, Radio8600, "FLEX-8600", store);
        var binding = Assert.IsType<RadioProfileReference>(profile.FindSetupFor(Radio8600));
        Assert.Equal("", binding.ProfileName);
        Assert.Equal("Headset", binding.NoiseSetupName);
        Assert.Equal("FLEX-8600", binding.RadioModel);
    }

    [Fact]
    public void A_name_taken_by_different_settings_is_never_overwritten()
    {
        var store = Store;
        store.Save(Radio8600, Sample("Studio mic"));
        var profile = new MicrophoneProfile { Name = "Studio mic", Capture = new MicCaptureSettings { Conditioning = Distinctive() } };
        var result = TxNoiseSetupMigration.Migrate(profile, Radio8600, "FLEX-8600", store);
        Assert.Equal(("Studio mic 2", TxNoiseMigrationOutcome.Created), (result.SetupName, result.Outcome));
        Assert.True(store.Find(Radio8600, "Studio mic")!.SameContentAs(Sample("x")));

        // The same settings under the same name are reused, not duplicated.
        var twin = new MicrophoneProfile { Name = "Studio mic", Capture = new MicCaptureSettings { Conditioning = Distinctive() } };
        var reused = TxNoiseSetupMigration.Migrate(twin, Radio8600, "FLEX-8600", store);
        Assert.Equal((TxNoiseMigrationOutcome.Reused, "Studio mic 2"), (reused.Outcome, reused.SetupName));
        Assert.Equal(2, store.List(Radio8600).Count);
    }

    [Fact]
    public void Identical_settings_already_saved_under_the_name_are_reused()
    {
        var store = Store;
        store.Save(Radio8600, TxNoiseSetupMigration.FromConditioning("Studio mic", Distinctive()));
        var profile = new MicrophoneProfile { Name = "Studio mic", Capture = new MicCaptureSettings { Conditioning = Distinctive() } };
        var result = TxNoiseSetupMigration.Migrate(profile, Radio8600, "FLEX-8600", store);
        Assert.Equal(TxNoiseMigrationOutcome.Reused, result.Outcome);
        Assert.Single(store.List(Radio8600));
        Assert.Equal("Studio mic", profile.NoiseSetupNameFor(Radio8600));
    }

    [Fact]
    public void A_setup_that_cannot_be_saved_leaves_the_microphone_untouched()
    {
        var store = Store;
        string path = store.PathFor(Radio8600)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "garbage");
        var profile = new MicrophoneProfile { Name = "Studio mic", Capture = new MicCaptureSettings { Conditioning = Distinctive() } };
        var result = TxNoiseSetupMigration.Migrate(profile, Radio8600, "FLEX-8600", store);
        Assert.Equal(TxNoiseMigrationOutcome.NotSaved, result.Outcome);
        Assert.False(result.ProfileChanged);
        Assert.Empty(profile.RadioSetups);
    }

    [Fact]
    public void A_microphone_with_no_old_settings_has_nothing_to_move()
    {
        var profile = new MicrophoneProfile { Name = "Plain" };
        Assert.Equal(TxNoiseMigrationOutcome.NothingToMove, TxNoiseSetupMigration.Migrate(profile, Radio8600, "", Store).Outcome);
        Assert.Equal(TxNoiseMigrationOutcome.BadRadio, TxNoiseSetupMigration.Migrate(profile, "x", "", Store).Outcome);
    }

    [Fact]
    public void The_pointer_survives_the_microphone_store_file_and_a_rename_repoints_it()
    {
        string dir = Path.Combine(_root, "operator");
        var mics = new MicrophoneProfileStore();
        var studio = new MicrophoneProfile { Name = "Studio mic" };
        studio.SetSetupFor(new RadioProfileReference { RadioId = Radio8600, ProfileName = "SSB", NoiseSetupName = "Quiet" });
        studio.SetSetupFor(new RadioTxValues { RadioId = Radio6300, NoiseSetupName = "Quiet" });
        mics.Profiles.Add(studio);
        Assert.True(mics.Save(dir, "op"));

        var loaded = MicrophoneProfileStore.Load(dir, "op");
        var back = loaded.FindByName("Studio mic")!;
        Assert.Equal("Quiet", back.NoiseSetupNameFor(Radio8600));
        Assert.Equal("Quiet", back.NoiseSetupNameFor(Radio6300));

        Assert.Equal(1, loaded.RepointNoiseSetup(Radio8600, "quiet", "Loud room"));
        Assert.Equal("Loud room", back.NoiseSetupNameFor(Radio8600));
        Assert.Equal("Quiet", back.NoiseSetupNameFor(Radio6300));
    }

    [Fact]
    public void Replacing_a_radio_binding_keeps_the_noise_setup_it_named()
    {
        var profile = new MicrophoneProfile { Name = "Studio mic" };
        profile.SetSetupFor(new RadioProfileReference { RadioId = Radio8600, ProfileName = "SSB", NoiseSetupName = "Quiet" });
        // The Workshop's save replaces the binding with a fresh one naming no setup.
        profile.SetSetupFor(new RadioTxValues { RadioId = Radio8600 });
        Assert.Equal("Quiet", profile.NoiseSetupNameFor(Radio8600));
        profile.SetSetupFor(new RadioProfileReference { RadioId = Radio8600, ProfileName = "AM", NoiseSetupName = "Loud" });
        Assert.Equal("Loud", profile.NoiseSetupNameFor(Radio8600));
    }

    // ── The conditioner's key state (item B) ──

    [Fact]
    public void The_conditioner_reads_the_key_once_per_buffer_and_a_throwing_source_reads_keyed()
    {
        var cond = new JJPortaudio.TxAudioConditioner();
        var buf = new float[960];
        cond.Process(buf, buf.Length, 48000);
        Assert.False(cond.Keyed); // no radio: never keyed
        bool keyed = false;
        cond.KeySource = () => keyed;
        cond.Process(buf, buf.Length, 48000);
        Assert.Equal((false, 0L), (cond.Keyed, cond.KeyDowns));
        keyed = true;
        cond.Process(buf, buf.Length, 48000);
        cond.Process(buf, buf.Length, 48000);
        Assert.Equal((true, 1L), (cond.Keyed, cond.KeyDowns));
        // Even in CW, where the chain steps aside, an over is still an over.
        keyed = false; cond.BypassAll = true;
        cond.Process(buf, buf.Length, 48000);
        Assert.False(cond.Keyed);
        cond.KeySource = () => throw new InvalidOperationException("radio gone");
        cond.Process(buf, buf.Length, 48000);
        Assert.True(cond.Keyed);
    }
}
