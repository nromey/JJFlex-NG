using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track K2 (#1002, ruled by Noel 2026-11-19, X1): a CW model chosen
/// before this machine had a Premium CW figure is provisional; when the first
/// figure arrives, if the operator has not changed CW's model since, the rule
/// runs once more and switches (said once) if it now says otherwise. A choice
/// made with a figure, or one the operator changed, is never looked at; and it
/// happens only once. Windowless: the fixture asserts it.
/// </summary>
public sealed class ProvisionalCwModelTests
{
    public ProvisionalCwModelTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static string Model(NoiseFixture f) => f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice ?? "";
    private static string Mark(NoiseFixture f) => f.Config.NoiseModeMemory.NnrCwModelProvisional;

    /// <summary>The first CW session on a machine never measured: NNR on, Premium CW, provisional.</summary>
    private static NoiseFixture FirstCwSessionUnmeasured()
    {
        var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        f.Radio.Mode = "CW";
        f.Noise.OnModeChanged("CW");
        Assert.True(f.Noise.IsOn("nnr"));
        Assert.Equal("premium_cw", Model(f));
        Assert.Equal("premium_cw", Mark(f));
        f.Spoken.Clear();
        return f;
    }

    private static List<NoiseJournalEntry> Journal(Action act)
    {
        int me = Environment.CurrentManagedThreadId;
        var journal = new List<NoiseJournalEntry>();
        void Listen(NoiseJournalEntry e) { if (Environment.CurrentManagedThreadId == me) journal.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        try { act(); }
        finally { NoiseSettingsJournal.Recorded -= Listen; }
        return journal;
    }

    [Fact]
    public void The_first_figure_saying_Standard_switches_the_provisional_model_once_and_says_so_once()
    {
        using var f = FirstCwSessionUnmeasured();
        int builtBefore = f.Built.Count(id => id == "nnr");
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Cost.Measurements = 1;
        var journal = Journal(() => f.Noise.OnModelCostsRemembered());

        Assert.Equal("standard_cw", Model(f));
        Assert.Equal("", Mark(f));
        // Said once, queued behind whatever was being said, on the mode-start subject.
        var said = Assert.Single(f.Spoken.Said);
        Assert.Equal(Lexicon.Get("audio.noise_surface.cw_model_measured", f.Spoken.Verbosity, ("model", "Standard CW")), said.Text);
        Assert.Equal(SpeechSubject.NoiseModeStart, said.Subject);
        Assert.Contains(0, f.Spoken.SaidAfter);
        // Journalled by JJ Flexible, with why.
        var entry = Assert.Single(journal);
        Assert.Equal(NoiseJournalKind.Model, entry.Kind);
        Assert.True(entry.ByJjFlexible);
        Assert.Equal("first measurement", entry.Source);
        Assert.Equal(new NoiseJournalChange("nnr", "model", "premium_cw", "standard_cw"), entry.Changes.Single());
        Assert.Equal(NoiseModeRules.CwModelReason(8.0, 1), entry.Label);
        // Under the slot's own warm-up: the engine is rebuilt with the new model.
        f.Settle();
        Assert.True(f.Built.Count(id => id == "nnr") > builtBefore, "NNR was not rebuilt for the new model");
        Assert.Equal("nnr", f.Pipeline.NeuralSlot.RunningEngineId);

        // Only once: a later figure, whatever it says, changes nothing.
        f.Spoken.Clear();
        f.Cost.PremiumCw99thPercentileMs = 4.0;
        Assert.Empty(Journal(() => f.Noise.OnModelCostsRemembered()));
        Assert.Equal("standard_cw", Model(f));
        Assert.Empty(f.Spoken.Said);
    }

    [Fact]
    public void A_first_figure_saying_Premium_leaves_it_silently_and_it_is_never_looked_at_again()
    {
        using var f = FirstCwSessionUnmeasured();
        f.Cost.PremiumCw99thPercentileMs = 4.0;
        Assert.Empty(Journal(() => f.Noise.OnModelCostsRemembered()));
        Assert.Equal("premium_cw", Model(f));
        Assert.Equal("", Mark(f));
        Assert.Empty(f.Spoken.Said);

        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.OnModelCostsRemembered();
        Assert.Equal("premium_cw", Model(f));
        Assert.Empty(f.Spoken.Said);
    }

    [Fact]
    public void A_figure_that_has_not_arrived_yet_leaves_the_mark_waiting()
    {
        using var f = FirstCwSessionUnmeasured();
        f.Noise.OnModelCostsRemembered();   // raised, but this source still has no figure
        Assert.Equal("premium_cw", Mark(f));
        Assert.Empty(f.Spoken.Said);
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.OnModelCostsRemembered();
        Assert.Equal("standard_cw", Model(f));
    }

    [Fact]
    public void A_CW_model_chosen_with_a_figure_is_never_looked_at()
    {
        using var f = new NoiseFixture();
        f.Cost.PremiumCw99thPercentileMs = 4.0;
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.OnModeChanged("CW");
        Assert.Equal("premium_cw", Model(f));
        Assert.Equal("", Mark(f));
        f.Spoken.Clear();
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.OnModelCostsRemembered();
        Assert.Equal("premium_cw", Model(f));
        Assert.Empty(f.Spoken.Said);
    }

    [Fact]
    public void A_CW_model_the_operator_set_on_its_lever_is_never_looked_at_even_when_it_is_the_same_model()
    {
        using var f = FirstCwSessionUnmeasured();
        var lever = f.Noise.Levers("nnr", includeAdvanced: true).First(l => l.Descriptor?.Name == "model");
        f.Noise.Set(lever, null, "standard_cw", "test");
        f.Noise.Set(lever, null, "premium_cw", "test");
        Assert.Equal("premium_cw", Model(f));
        Assert.Equal("", Mark(f));
        f.Spoken.Clear();
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.OnModelCostsRemembered();
        Assert.Equal("premium_cw", Model(f));
        Assert.Empty(f.Spoken.Said);
    }

    [Fact]
    public void A_CW_model_the_operator_stepped_to_by_a_favourite_is_never_looked_at()
    {
        using var f = FirstCwSessionUnmeasured();
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nnr", Model = "premium_cw" });
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nnr", Model = "standard_cw" });
        f.Noise.BeginSession();
        // The first step lands on the Premium CW favourite: the model it already
        // holds, now chosen by the operator's own hand.
        f.Noise.Step(+1, "test");
        Assert.Equal("NNR Premium CW on.", f.Spoken.Last);
        Assert.Equal("premium_cw", Model(f));
        Assert.Equal("", Mark(f));
        f.Spoken.Clear();
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.OnModelCostsRemembered();
        Assert.Equal("premium_cw", Model(f));
    }

    private sealed class MemoryPresets : INoisePresetStore
    {
        public AudioChainPresets Store = AudioChainPresets.CreateDefaults();
        public AudioChainPresets Load() => Store;
        public bool Save(AudioChainPresets store) { Store = store; return true; }
    }

    [Fact]
    public void A_CW_model_a_preset_brought_is_the_operators_and_is_never_looked_at()
    {
        using var f = FirstCwSessionUnmeasured();
        f.Noise.PresetStore = new MemoryPresets();
        Assert.NotNull(f.Noise.SavePreset("Mine", includeNoise: true, includeChain: false, PresetCollision.Ask, "test"));
        Assert.Equal("premium_cw", Mark(f));   // saving chooses nothing
        f.Noise.ApplyPreset("Mine", "test");
        Assert.Equal("premium_cw", Model(f));
        Assert.Equal("", Mark(f));
        f.Spoken.Clear();
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.OnModelCostsRemembered();
        Assert.Equal("premium_cw", Model(f));
    }

    [Fact]
    public void A_figure_arriving_outside_CW_waits_and_the_next_CW_visit_chooses_by_it()
    {
        using var f = FirstCwSessionUnmeasured();
        f.Radio.Mode = "USB";
        f.Noise.OnModeChanged("USB");   // the voice model comes back
        Assert.False(NoiseModeRules.IsCwModel(Model(f)));
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.OnModelCostsRemembered();
        Assert.Equal("premium_cw", Mark(f));   // nothing to switch outside CW: it waits
        f.Radio.Mode = "CW";
        f.Noise.OnModeChanged("CW");
        Assert.Equal("standard_cw", Model(f));
        Assert.Equal("", Mark(f));
    }

    [Fact]
    public void A_provisional_model_saved_before_its_figure_is_looked_at_when_CW_is_next_entered_with_one()
    {
        // A session that ended in CW before the helper's figure arrived: the
        // slot holds Premium CW and the memory says it is provisional.
        using var f = new NoiseFixture();
        var mem = f.Config.NoiseModeMemory;
        mem.Cw.Known = true;
        mem.Cw.Neural = new NoiseSlotMemory { On = true, EngineId = "nnr" };
        mem.LastGroup = "cw";
        mem.NnrVoiceModelOwed = "standard";
        mem.NnrCwModelProvisional = "premium_cw";
        f.Pipeline.NeuralSlot.Select("nnr", out _);
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium_cw"));
        f.Pipeline.NeuralSlot.Enabled = true;
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Radio.Mode = "CW";
        f.Noise.Attach("CW");
        Assert.Equal("standard_cw", Model(f));
        Assert.Equal("", Mark(f));
        Assert.Contains(f.Spoken.Said, s => s.Text == Lexicon.Get("audio.noise_surface.cw_model_measured", f.Spoken.Verbosity, ("model", "Standard CW")));
    }

    [Fact]
    public void A_CW_model_changed_by_any_other_road_is_not_looked_at()
    {
        using var f = FirstCwSessionUnmeasured();
        // A road that does not report itself: the slot's model simply differs.
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "standard_cw"));
        f.Cost.PremiumCw99thPercentileMs = 4.0;
        f.Noise.OnModelCostsRemembered();
        Assert.Equal("standard_cw", Model(f));
        Assert.Equal("", Mark(f));
        Assert.Empty(f.Spoken.Said);
    }

    [Fact]
    public void The_provisional_mark_is_saved_with_the_receive_config()
    {
        var config = new AudioOutputConfig();
        config.NoiseModeMemory.NnrCwModelProvisional = "premium_cw";
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(AudioOutputConfig));
        using var w = new System.IO.StringWriter();
        serializer.Serialize(w, config);
        using var r = new System.IO.StringReader(w.ToString());
        var back = (AudioOutputConfig)serializer.Deserialize(r)!;
        Assert.Equal("premium_cw", back.NoiseModeMemory.NnrCwModelProvisional);
        // A config from before this reads as nothing provisional.
        Assert.Equal("", new NoiseModeMemoryConfig().NnrCwModelProvisional);
    }
}
