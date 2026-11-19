using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track S, B3 (#1019): the RNNoise model as an Advanced setting, seen
/// through the noise controller over fakes (the real descriptors, fake engines)
/// and, where audio must pass through, the real catalog. Windowless.
/// </summary>
public sealed class RnnoiseModelSettingTests
{
    public RnnoiseModelSettingTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static NoiseLever ModelLever(NoiseFixture f) => f.Noise.Levers("rnnoise", true).Single(l => l.Key == "model");
    private static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    [Fact]
    public void The_model_is_an_Advanced_choice_of_three_named_by_identity_defaulting_to_the_newest()
    {
        using var f = new NoiseFixture();
        Assert.DoesNotContain(f.Noise.Levers("rnnoise", false), l => l.Key == "model");
        var model = ModelLever(f);
        Assert.True(model.Advanced);
        Assert.True(model.Descriptor!.ChangesLatency);
        Assert.Equal(RnnoiseModels.All, model.Descriptor.Options!.Select(o => o.Name));
        Assert.Equal(new[] { "main_70f1d256", "tag_v02", "v011" }, RnnoiseModels.All);   // stored names never drift
        Assert.Equal(RnnoiseModels.Newest, f.Noise.ChoiceOf(model));
        // The floor stays the first lever; the model never is.
        Assert.Equal(NoiseLeverKind.Floor, f.Noise.Levers("rnnoise", true)[0].Kind);
        Assert.All(model.Descriptor.Options!, o => Assert.StartsWith("WORDING PLACEHOLDER:", Radios.Lexicon.Get(o.LabelKey)));
    }

    [Fact]
    public void A_first_model_choice_on_an_untouched_RNNoise_keeps_its_default_floor()
    {
        // Premise correction 2: until Track S a first setting created the entry at
        // Strength 1, so this choice would have set the floor to 0.
        using var f = new NoiseFixture();
        Assert.True(f.Pipeline.NeuralSlot.TrySetSetting("rnnoise", "model", new(0, RnnoiseModels.Tag)));
        Assert.Equal(Bits(NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Receive)), Bits(f.Pipeline.NeuralSlot.GetStrength("rnnoise")));
        Assert.Equal(10, f.Noise.NumberOf(f.Noise.FloorLeverOf("rnnoise")!));
    }

    [Fact]
    public void Choosing_a_model_builds_a_new_engine_with_it_and_journals_a_model_change()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("rnnoise", "test");
        f.Settle();
        int built = f.Built.Count(b => b == "rnnoise");
        var entries = new List<NoiseJournalEntry>();
        void Take(NoiseJournalEntry e) => entries.Add(e);
        NoiseSettingsJournal.Recorded += Take;
        try { f.Noise.Set(ModelLever(f), null, RnnoiseModels.V011, "test"); }
        finally { NoiseSettingsJournal.Recorded -= Take; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (f.Built.Count(b => b == "rnnoise") <= built && clock.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(5);
        Assert.True(f.Built.Count(b => b == "rnnoise") > built, "a model change must rebuild the engine");
        Assert.Contains(entries, e => e.Kind == NoiseJournalKind.Model && e.Changes.Single() == new NoiseJournalChange("rnnoise", "model", RnnoiseModels.Newest, RnnoiseModels.V011));
    }

    [Fact]
    public void Escape_puts_a_model_change_back()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("rnnoise", "test");
        f.Settle();
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Set(ModelLever(f), null, RnnoiseModels.Tag, NoiseController.LayerSource);   // the layer's own (#1150)
        Assert.Equal(RnnoiseModels.Tag, f.Noise.ChoiceOf(ModelLever(f)));
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Equal(RnnoiseModels.Newest, f.Noise.ChoiceOf(ModelLever(f)));
    }

    [Fact]
    public void Return_to_defaults_puts_the_newest_model_back_with_the_floor()
    {
        using var f = new NoiseFixture();
        f.Noise.Set(ModelLever(f), null, RnnoiseModels.V011, "test");
        f.Noise.Set(f.Noise.FloorLeverOf("rnnoise")!, 40, null, "test");
        f.Noise.ReturnToDefaults("rnnoise", "test");
        Assert.Equal(RnnoiseModels.Newest, f.Noise.ChoiceOf(ModelLever(f)));
        Assert.Equal(10, f.Noise.NumberOf(f.Noise.FloorLeverOf("rnnoise")!));
    }

    [Fact]
    public void Return_to_defaults_in_CW_does_not_hand_RNNoise_an_NNR_model()
    {
        using var f = new NoiseFixture();
        f.Radio.Mode = "CW";
        f.Noise.Attach("CW");
        var entries = new List<NoiseJournalEntry>();
        void Take(NoiseJournalEntry e) => entries.Add(e);
        f.Noise.Set(ModelLever(f), null, RnnoiseModels.Tag, "test");
        NoiseSettingsJournal.Recorded += Take;
        try { f.Noise.ReturnToDefaults("rnnoise", "test"); }
        finally { NoiseSettingsJournal.Recorded -= Take; }
        Assert.Equal(RnnoiseModels.Newest, f.Noise.ChoiceOf(ModelLever(f)));
        Assert.DoesNotContain(entries.SelectMany(e => e.Changes), c => c.EngineId == "rnnoise" && c.NewValue.Contains("cw", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_preset_without_a_model_puts_RNNoise_back_on_its_default_model_and_one_with_a_model_carries_it()
    {
        using var f = new NoiseFixture();
        f.Noise.PresetStore = new MemoryPresets();
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.Set(ModelLever(f), null, RnnoiseModels.Tag, "test");
        Assert.NotNull(f.Noise.SavePreset("Tag model", true, false, PresetCollision.Ask, "test"));
        // Balanced capture holds RNNoise with no model value: the slot replaces an
        // engine's values wholesale, so the model returns to its default.
        f.Noise.ApplyPreset(NoiseController.BalancedCaptureName, "test");
        Assert.Equal(RnnoiseModels.Newest, f.Noise.ChoiceOf(ModelLever(f)));
        f.Noise.ApplyPreset("Tag model", "test");
        Assert.Equal(RnnoiseModels.Tag, f.Noise.ChoiceOf(ModelLever(f)));
    }

    [Fact]
    public void The_model_is_app_wide_and_per_mode_memory_does_not_carry_it()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.Set(ModelLever(f), null, RnnoiseModels.V011, "test");
        f.Radio.Mode = "CW";
        f.Noise.Attach("CW");
        f.Radio.Mode = "USB";
        f.Noise.Attach("USB");
        Assert.Equal(RnnoiseModels.V011, f.Noise.ChoiceOf(ModelLever(f)));
    }

    [Fact]
    public void A_favourite_saved_before_RNNoise_had_a_model_still_matches_and_a_new_one_carries_its_model()
    {
        using var f = new NoiseFixture();
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise" });   // as an older build saved it
        Assert.True(f.Noise.IsFavourite("rnnoise"));
        f.Noise.Set(ModelLever(f), null, RnnoiseModels.Tag, "test");
        Assert.True(f.Noise.IsFavourite("rnnoise"));
        f.Config.NoiseFavourites.Clear();
        f.Noise.ToggleFavourite("rnnoise", "test");
        var saved = Assert.Single(f.Config.NoiseFavourites);
        Assert.Equal(RnnoiseModels.Tag, saved.Model);
        // Its spoken name reads RNNoise's own model label, never the raw identity.
        Assert.DoesNotContain("tag_v02", f.Noise.FavouriteName(saved));
        Assert.Contains(Radios.Lexicon.Get("audio.denoise.rnnoise.model.tag_v02"), f.Noise.FavouriteName(saved));
        // The default model of an Advanced model setting goes unsaid: "RNNoise", as before the model existed.
        Assert.Equal(Radios.Lexicon.Get("audio.denoise.rnnoise"), f.Noise.FavouriteName(new NoiseFavourite { EngineId = "rnnoise", Model = RnnoiseModels.Newest }));
        // NNR's model is a main lever, so its favourites keep naming it.
        Assert.Contains(Radios.Lexicon.Get("audio.denoise.nnr.model.standard"), f.Noise.FavouriteName(new NoiseFavourite { EngineId = "nnr", Model = "standard" }));
    }

    [Fact]
    public void The_delays_readout_expects_ten_or_twenty_milliseconds_before_RNNoise_runs()
    {
        using var f = new NoiseFixture();
        Assert.Equal(20, DenoiseEngineCatalog.ExpectedEngineDelayMs("rnnoise", RnnoiseModels.Newest));
        Assert.Equal(20, DenoiseEngineCatalog.ExpectedEngineDelayMs("rnnoise", RnnoiseModels.Tag));
        Assert.Equal(10, DenoiseEngineCatalog.ExpectedEngineDelayMs("rnnoise", RnnoiseModels.V011));
        // DeepFilterNet3's delay is fixed by the model, so it is known before it runs too
        // (Sprint 49 Track H2 bench round, item 4: 1,440 samples, measured at the bench).
        Assert.Equal(30, DenoiseEngineCatalog.ExpectedEngineDelayMs("deepfilternet3", null));
        // NR4's live shape is fixed too (#1152): one 512-sample frame, about 11 ms.
        Assert.Equal(11, DenoiseEngineCatalog.ExpectedEngineDelayMs("nr4", null));
        Assert.Equal(Radios.Lexicon.Get("audio.noise_surface.delay_figure", ("ms", 20)), f.Noise.DelayFigure("rnnoise"));
        f.Noise.Set(ModelLever(f), null, RnnoiseModels.V011, "test");
        Assert.Equal(Radios.Lexicon.Get("audio.noise_surface.delay_figure", ("ms", 10)), f.Noise.DelayFigure("rnnoise"));
    }

    [Fact]
    public void A_model_chosen_while_no_audio_flows_reads_its_expected_delay_not_the_old_builds()
    {
        // Walk 2026-11-09 22:31: with PC audio off, the startup engine (newest model,
        // 960 samples) kept answering after the original model was chosen, so
        // Settings said about 20 ms. The readout now measures only a current build.
        using var f = new NoiseFixture(rnnoiseLatency: 960);
        Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.RunningEngineId);
        Assert.Equal(Radios.Lexicon.Get("audio.noise_surface.delay_figure", ("ms", 20)), f.Noise.DelayFigure("rnnoise"));
        f.Noise.Set(ModelLever(f), null, RnnoiseModels.V011, "test");
        Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.RunningEngineId);      // no audio yet: the old build still runs
        Assert.False(f.Pipeline.NeuralSlot.RunningIsCurrent("rnnoise"));
        Assert.Equal(Radios.Lexicon.Get("audio.noise_surface.delay_figure", ("ms", 10)), f.Noise.DelayFigure("rnnoise"));
    }

    // ── A model whose library is missing: listed, marked, refused with its reason, audio passed through ──

    [Fact]
    public void A_model_whose_library_is_missing_is_listed_marked_kept_and_passes_audio_through()
    {
        string folder = Path.Combine(Path.GetTempPath(), "jjflex-49s-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string native = Path.Combine(RepoPaths.RequireRoot(), "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native");
        File.Copy(Path.Combine(native, RnnoiseEngine.LibraryFileName), Path.Combine(folder, RnnoiseEngine.LibraryFileName));
        File.Copy(Path.Combine(native, RnnoiseVoiceDetector.LibraryFileName), Path.Combine(folder, RnnoiseVoiceDetector.LibraryFileName));
        var previous = RnnoiseEngine.LibraryLocator;
        try
        {
            RnnoiseEngine.LibraryLocator = file => Path.Combine(folder, file);   // the tag library is absent
            using var f = new NoiseFixture();
            var model = ModelLever(f);
            string tagLabel = Radios.Lexicon.Get("audio.denoise.rnnoise.model.tag_v02");
            string marker = Radios.Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(Unavailability.LibraryMissing));
            // Listed, never hidden, with its marker; the others are unmarked.
            var steps = f.Noise.LeverSteps(model);
            Assert.Equal(3, steps.Count);
            Assert.Contains(char.ToLowerInvariant(marker[0]) + marker.Substring(1), steps.Single(s => s.Choice == RnnoiseModels.Tag).Text);
            Assert.Equal(Radios.Lexicon.Get("audio.denoise.rnnoise.model.main_70f1d256"), steps.Single(s => s.Choice == RnnoiseModels.Newest).Text);
            // Feature Availability names it, with its reason; no line for a model that can run.
            var lines = f.Noise.ComputerAvailabilityLines();
            Assert.Single(lines, l => l == Radios.Lexicon.Get("audio.noise_availability.model_line", ("engine", Radios.Lexicon.Get("audio.denoise.rnnoise")),
                ("model", tagLabel), ("status", Radios.Lexicon.Get("audio.noise_availability.unavailable")),
                ("reason", Radios.Lexicon.Get("audio.noise_availability.reason.librarymissing"))));
            // Choosing it keeps the choice as an intent and says why, as an unavailable engine does.
            f.Noise.Toggle("rnnoise", "test");
            f.Noise.Set(model, null, RnnoiseModels.Tag, "test");
            Assert.Equal(RnnoiseModels.Tag, f.Noise.ChoiceOf(model));
            Assert.Equal(SpeechSubject.NoiseUnavailable, f.Spoken.Said[^1].Subject);
            Assert.StartsWith(Radios.Lexicon.Get("audio.noise_surface.unavailable.librarymissing", f.Spoken.Verbosity,
                ("engine", Radios.Lexicon.Get("audio.denoise.rnnoise"))), f.Spoken.Last);
            Assert.NotNull(f.Noise.Marker("rnnoise"));        // the engine follows its chosen model

            // The real catalog: the slot cannot build the tag model, so it passes audio through.
            using var p = new RxAudioPipeline();
            p.NeuralSlot.Enabled = true;
            Assert.True(p.NeuralSlot.TrySetSetting("rnnoise", "model", new(0, RnnoiseModels.Tag)));
            var audio = new float[960];
            for (int i = 0; i < 400; i++)
            {
                Array.Fill(audio, 0.1f);
                p.NeuralSlot.Process(audio, true, true, false, null);
                System.Threading.Thread.Sleep(1);
                if (p.NeuralSlot.RunningEngineId.Length == 0) break;
            }
            Array.Fill(audio, 0.1f);
            p.NeuralSlot.Process(audio, true, true, false, null);
            Assert.Equal("", p.NeuralSlot.RunningEngineId);
            Assert.All(audio, s => Assert.Equal(0.1f, s));
            Assert.Equal(RnnoiseModels.Tag, p.NeuralSlot.GetSetting("rnnoise", "model").Choice);
        }
        finally
        {
            RnnoiseEngine.LibraryLocator = previous;
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ── Repair round 3 (#1019): a favourite is judged on the model it lands on ──

    /// <summary>Runs the body with only the newest and v0.1.1 libraries present: the tag model's library is missing.</summary>
    private static void WithTagLibraryMissing(Action<NoiseFixture> body)
    {
        string folder = Path.Combine(Path.GetTempPath(), "jjflex-49s-fav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string native = Path.Combine(RepoPaths.RequireRoot(), "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native");
        File.Copy(Path.Combine(native, RnnoiseEngine.LibraryFileName), Path.Combine(folder, RnnoiseEngine.LibraryFileName));
        File.Copy(Path.Combine(native, RnnoiseVoiceDetector.LibraryFileName), Path.Combine(folder, RnnoiseVoiceDetector.LibraryFileName));
        var previous = RnnoiseEngine.LibraryLocator;
        try
        {
            RnnoiseEngine.LibraryLocator = file => Path.Combine(folder, file);
            using var f = new NoiseFixture();
            body(f);
        }
        finally
        {
            RnnoiseEngine.LibraryLocator = previous;
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void A_favourite_on_a_healthy_model_recovers_from_a_chosen_model_whose_library_is_missing()
    {
        WithTagLibraryMissing(f =>
        {
            var model = ModelLever(f);
            f.Noise.Toggle("rnnoise", "test");
            f.Noise.Set(model, null, RnnoiseModels.Tag, "test");          // kept, unavailable
            Assert.NotNull(f.Noise.Marker("rnnoise"));
            f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise", Model = RnnoiseModels.Newest });
            f.Noise.Step(+1, "test");
            Assert.Equal(RnnoiseModels.Newest, f.Noise.ChoiceOf(model));
            Assert.True(f.Noise.IsOn("rnnoise"));
            Assert.Equal(SpeechSubject.NoiseEngineSwitch, f.Spoken.Said[^1].Subject);
            Assert.Null(f.Noise.Marker("rnnoise"));
        });
    }

    [Fact]
    public void A_favourite_on_a_model_whose_library_is_missing_says_why_and_leaves_the_healthy_model_running()
    {
        WithTagLibraryMissing(f =>
        {
            var model = ModelLever(f);
            f.Noise.Toggle("rnnoise", "test");
            Assert.Equal(RnnoiseModels.Newest, f.Noise.ChoiceOf(model));
            f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise", Model = RnnoiseModels.Tag });
            f.Noise.Step(+1, "test");
            Assert.Equal(RnnoiseModels.Newest, f.Noise.ChoiceOf(model));
            Assert.Equal(SpeechSubject.NoiseUnavailable, f.Spoken.Said[^1].Subject);
            Assert.StartsWith(Radios.Lexicon.Get("audio.noise_surface.unavailable.librarymissing", f.Spoken.Verbosity,
                ("engine", Radios.Lexicon.Get("audio.denoise.rnnoise"))), f.Spoken.Last);
        });
    }

    /// <summary>An operator's preset store in memory, with Balanced capture still to be offered.</summary>
    private sealed class MemoryPresets : INoisePresetStore
    {
        public AudioChainPresets Data = new();
        public AudioChainPresets Load() => Data;
        public bool Save(AudioChainPresets store) { Data = store; return true; }
    }
}
