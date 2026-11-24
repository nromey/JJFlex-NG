using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track I, item 6: the transmit setup record at version 2 (the
/// expander's settings, the equaliser, the de-esser, the stage order), the
/// migration of a version 1 record (S7: a saved gate keeps exactly its sound;
/// a new or off one starts at 2:1), and the round trip through the per-radio
/// store and a preset. The live state's home (audioConfig.xml) is
/// JJFlexWpf's and is tested there. Every file lives in a scratch folder.
/// </summary>
public sealed class TxNoiseSetupVersionTwoTests : IDisposable
{
    private const string Radio = "1234-5678-9012-3456";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jjf-track-i-" + Guid.NewGuid().ToString("N"));

    public TxNoiseSetupVersionTwoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A version 1 file, as the BASE commit (8953bc2c) wrote it: produced by a
    /// scratch probe compiling that commit's record types and serializer, never
    /// copied from anyone's settings. "Desk mic" has its gate on (by hand, -42
    /// dB, depth 30); "Headset" has it off.
    /// </summary>
    internal const string VersionOneFile = """
<?xml version="1.0" encoding="utf-8"?>
<TxNoiseSetups xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" schemaVersion="1">
  <RadioId>1234-5678-9012-3456</RadioId>
  <Setups>
    <TxNoiseSetup>
      <Version>1</Version>
      <Name>Desk mic</Name>
      <Spectral>
        <Enabled>false</Enabled>
        <EngineId />
        <Engines />
      </Spectral>
      <Neural>
        <Enabled>true</Enabled>
        <EngineId>rnnoise</EngineId>
        <Engines>
          <DenoiseEngineSettings>
            <EngineId>rnnoise</EngineId>
            <Direction>Transmit</Direction>
            <Strength>0.8</Strength>
            <Values />
          </DenoiseEngineSettings>
        </Engines>
      </Neural>
      <Gate>
        <Enabled>true</Enabled>
        <AutoThreshold>false</AutoThreshold>
        <ThresholdMarginDb>9</ThresholdMarginDb>
        <ThresholdDb>-42</ThresholdDb>
        <AttackMs>4</AttackMs>
        <HoldMs>220</HoldMs>
        <ReleaseMs>260</ReleaseMs>
        <RangeDb>30</RangeDb>
      </Gate>
    </TxNoiseSetup>
    <TxNoiseSetup>
      <Version>1</Version>
      <Name>Headset</Name>
      <Spectral>
        <Enabled>false</Enabled>
        <EngineId />
        <Engines />
      </Spectral>
      <Neural>
        <Enabled>false</Enabled>
        <EngineId />
        <Engines />
      </Neural>
      <Gate>
        <Enabled>false</Enabled>
        <AutoThreshold>true</AutoThreshold>
        <ThresholdMarginDb>8</ThresholdMarginDb>
        <ThresholdDb>-60</ThresholdDb>
        <AttackMs>3</AttackMs>
        <HoldMs>150</HoldMs>
        <ReleaseMs>200</ReleaseMs>
        <RangeDb>18</RangeDb>
      </Gate>
    </TxNoiseSetup>
  </Setups>
</TxNoiseSetups>
""";

    /// <summary>The "Desk mic" setup alone, as a version 1 TxNoiseSetup element of any name (a preset's or the live state's).</summary>
    internal static string VersionOneSetupElement(string elementName)
    {
        int start = VersionOneFile.IndexOf("<TxNoiseSetup>", StringComparison.Ordinal);
        int end = VersionOneFile.IndexOf("</TxNoiseSetup>", StringComparison.Ordinal) + "</TxNoiseSetup>".Length;
        return VersionOneFile[start..end].Replace("TxNoiseSetup>", elementName + ">");
    }

    private TxNoiseSetupStore StoreWithVersionOne()
    {
        var store = new TxNoiseSetupStore(_root);
        var path = store.PathFor(Radio)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, VersionOneFile);
        return store;
    }

    private static void AssertKeptItsSound(TxNoiseSetup s)
    {
        Assert.Equal(TxNoiseSetup.CurrentVersion, s.Version);
        Assert.True(s.Gate.Enabled);
        Assert.True(s.Gate.GateSetting, "a saved gate that was on is the gate setting");
        Assert.False(s.Gate.SideChainFilter);
        Assert.Equal(0f, s.Gate.LookAheadMs);
        Assert.Equal((false, -42f, 9f, 4f, 220f, 260f, 30f),
            (s.Gate.AutoThreshold, s.Gate.ThresholdDb, s.Gate.ThresholdMarginDb, s.Gate.AttackMs, s.Gate.HoldMs, s.Gate.ReleaseMs, s.Gate.RangeDb));
        Assert.False(s.Eq.Enabled);
        Assert.False(s.DeEsser.Enabled);
        Assert.True(s.RunsDefaultOrder);
    }

    private static void AssertNewExpander(TxNoiseGateSetup g)
    {
        Assert.False(g.GateSetting);
        Assert.Equal(2f, g.RatioToOne);
        Assert.True(g.SideChainFilter);
        Assert.Equal(3f, g.LookAheadMs);
    }

    [Fact]
    public void A_version_one_file_loads_from_the_store_with_its_gate_kept_and_the_new_stages_off()
    {
        var store = StoreWithVersionOne();
        AssertKeptItsSound(store.Find(Radio, "Desk mic")!);
        var headset = store.Find(Radio, "Headset")!;
        Assert.Equal(TxNoiseSetup.CurrentVersion, headset.Version);
        Assert.False(headset.Gate.Enabled);
        AssertNewExpander(headset.Gate); // its first switch-on gets the new defaults
        Assert.Equal(18f, headset.Gate.RangeDb);
    }

    [Fact]
    public void A_version_one_setup_in_a_preset_keeps_its_gate_too()
    {
        string file = Path.Combine(_root, "preset.xml");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioChainPreset><Name>Old</Name>"
            + VersionOneSetupElement("TransmitNoise") + "</AudioChainPreset>");
        Assert.True(AudioChainPreset.TryLoad(file, out var preset, out _));
        AssertKeptItsSound(preset.TransmitNoise!);
    }

    // ── Repair 1, N1: the upgrade does not depend on element order (S7, Noel's ruling) ──

    /// <summary>"Desk mic"'s element in three shapes no base build wrote but a hand or another tool might: no Version, Gate before Version=1, and a newer version's record.</summary>
    internal static string Variant(string shape, string elementName)
    {
        string e = VersionOneSetupElement(elementName);
        return shape switch
        {
            "no_version" => e.Replace("<Version>1</Version>", ""),
            "gate_first" => MoveGateFirst(e),
            "newer" => e.Replace("<Version>1</Version>", "<Version>3</Version>"),
            _ => e,
        };
    }

    private static string MoveGateFirst(string e)
    {
        int a = e.IndexOf("<Gate>", StringComparison.Ordinal), b = e.IndexOf("</Gate>", StringComparison.Ordinal) + "</Gate>".Length;
        string gate = e[a..b];
        string rest = e.Remove(a, b - a);
        int v = rest.IndexOf("<Version>", StringComparison.Ordinal);
        return rest.Insert(v, gate);
    }

    /// <summary>What each shape must load as: the two old shapes keep the gate's sound exactly; a newer record is left exactly as it was read.</summary>
    internal static void AssertShape(string shape, TxNoiseSetup s)
    {
        if (shape == "newer")
        {
            Assert.Equal(3, s.Version);
            Assert.False(s.Gate.GateSetting); // untouched: a newer build's record is not this build's to change
            Assert.Equal(2f, s.Gate.RatioToOne);
            Assert.Equal(3f, s.Gate.LookAheadMs);
            return;
        }
        AssertKeptItsSound(s);
    }

    [Theory]
    [InlineData("no_version")]
    [InlineData("gate_first")]
    [InlineData("newer")]
    public void Every_element_order_upgrades_the_same_through_the_store(string shape)
    {
        var store = new TxNoiseSetupStore(_root);
        var path = store.PathFor(Radio)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><TxNoiseSetups schemaVersion=\"1\"><Setups>"
            + Variant(shape, "TxNoiseSetup") + "</Setups></TxNoiseSetups>");
        AssertShape(shape, store.Find(Radio, "Desk mic")!);
    }

    [Theory]
    [InlineData("no_version")]
    [InlineData("gate_first")]
    [InlineData("newer")]
    public void Every_element_order_upgrades_the_same_through_a_preset_file_and_the_preset_store(string shape)
    {
        string file = Path.Combine(_root, "preset.xml");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioChainPreset><Name>Old</Name>"
            + Variant(shape, "TransmitNoise") + "</AudioChainPreset>");
        Assert.True(AudioChainPreset.TryLoad(file, out var preset, out _));
        AssertShape(shape, preset.TransmitNoise!);

        string dir = Path.Combine(_root, "presets");
        string storeFile = Path.Combine(dir, "op_audioPresets.xml"); // AudioChainPresets' own name for an operator's presets
        Directory.CreateDirectory(Path.GetDirectoryName(storeFile)!);
        File.WriteAllText(storeFile, "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioChainPresets><Presets /><NoisePresets><AudioChainPreset><Name>Old</Name>"
            + Variant(shape, "TransmitNoise") + "</AudioChainPreset></NoisePresets></AudioChainPresets>");
        var loaded = AudioChainPresets.Load(dir, "op", out _);
        AssertShape(shape, loaded.NoisePresets.Single(p => p.Name == "Old").TransmitNoise!);
    }

    [Fact]
    public void A_record_with_no_version_but_the_expanders_fields_is_a_version_two_record_and_is_left_alone()
    {
        string e = VersionOneSetupElement("TransmitNoise").Replace("<Version>1</Version>", "")
            .Replace("</Gate>", "<RatioToOne>3</RatioToOne><GateSetting>false</GateSetting></Gate>");
        string file = Path.Combine(_root, "v2.xml");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioChainPreset><Name>V2</Name>" + e + "</AudioChainPreset>");
        Assert.True(AudioChainPreset.TryLoad(file, out var preset, out _));
        Assert.False(preset.TransmitNoise!.Gate.GateSetting);
        Assert.Equal(3f, preset.TransmitNoise.Gate.RatioToOne);
        Assert.Equal(TxNoiseSetup.CurrentVersion, preset.TransmitNoise.Version);
    }

    [Fact]
    public void A_new_setup_starts_at_two_to_one_with_the_filter_on_and_three_milliseconds()
    {
        var s = new TxNoiseSetup();
        Assert.Equal(2, s.Version);
        AssertNewExpander(s.Gate);
        Assert.Equal(JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder, s.StageOrder);
    }

    [Fact]
    public void The_old_per_microphone_gate_keeps_its_sound_and_the_recommended_one_is_new()
    {
        var on = TxNoiseSetupMigration.FromConditioning("x", new TxConditioningSettings { GateEnabled = true, GateRangeDb = 22 });
        Assert.True(on.Gate.GateSetting);
        Assert.False(on.Gate.SideChainFilter);
        Assert.Equal(0f, on.Gate.LookAheadMs);
        AssertNewExpander(TxNoiseSetupMigration.FromConditioning("y", new TxConditioningSettings()).Gate);
    }

    private static TxNoiseSetup Full() => new()
    {
        Name = "Full",
        Gate = new TxNoiseGateSetup { Enabled = true, RatioToOne = 3, GateSetting = false, SideChainFilter = true, SideChainLowCutHz = 180, SideChainHighCutHz = 3500, LookAheadMs = 5 },
        Eq = new ParametricEqSetup
        {
            Enabled = true, OutputGainDb = -2, PresetId = "dx",
            Bands =
            {
                new() { Kind = ParametricEqKinds.HighPass, FrequencyHz = 200, Q = 0.7071f },
                new() { Kind = ParametricEqKinds.Peaking, FrequencyHz = 2200, GainDb = 4, Q = 0.9f },
                new() { Kind = "tilt_from_a_later_build", FrequencyHz = 1000, GainDb = 1, Q = 1 },
            },
        },
        DeEsser = new TxDeEsserSetup { Enabled = true, FrequencyHz = 4500, AmountDb = 8, SensitivityDb = -35 },
        StageOrder = new[] { "noise", "expander", "clarity", "eq", "deesser" },
    };

    [Fact]
    public void A_full_record_round_trips_through_the_store_including_a_band_kind_and_a_stage_id_this_build_does_not_know()
    {
        var store = new TxNoiseSetupStore(_root);
        Assert.True(store.Save(Radio, Full()).Succeeded);
        var back = store.Find(Radio, "Full")!;
        Assert.True(back.SameContentAs(Full()));
        Assert.Equal(new[] { "noise", "expander", "clarity", "eq", "deesser" }, back.StageOrder);
        Assert.False(back.RunsDefaultOrder);
        Assert.Equal("tilt_from_a_later_build", back.Eq.Bands[2].Kind);
        // What runs leaves the unknown band out; nothing is lost from the record.
        Assert.Equal(2, back.Eq.ToDesign()!.BandCount);
        // Saved again, the unknown parts survive.
        Assert.True(store.Save(Radio, back).Succeeded);
        Assert.True(store.Find(Radio, "Full")!.SameContentAs(Full()));
    }

    [Fact]
    public void A_full_record_round_trips_through_a_preset()
    {
        string file = Path.Combine(_root, "p.xml");
        var preset = new AudioChainPreset { Name = "P", TransmitNoise = Full() };
        using (var fs = File.Create(file)) new XmlSerializer(typeof(AudioChainPreset)).Serialize(fs, preset);
        Assert.True(AudioChainPreset.TryLoad(file, out var back, out _));
        Assert.True(back.TransmitNoise!.SameContentAs(Full()));
    }

    [Theory]
    [InlineData("ratio")]
    [InlineData("gate_setting")]
    [InlineData("side_chain")]
    [InlineData("low_cut")]
    [InlineData("high_cut")]
    [InlineData("look_ahead")]
    [InlineData("eq_on")]
    [InlineData("eq_gain")]
    [InlineData("eq_band")]
    [InlineData("eq_preset")]
    [InlineData("deesser_on")]
    [InlineData("deesser_frequency")]
    [InlineData("deesser_amount")]
    [InlineData("deesser_sensitivity")]
    [InlineData("order")]
    public void SameContentAs_sees_a_change_in_each_new_field(string field)
    {
        var a = Full(); var b = Full();
        Action<TxNoiseSetup> change = field switch
        {
            "ratio" => s => s.Gate.RatioToOne = 4,
            "gate_setting" => s => s.Gate.GateSetting = true,
            "side_chain" => s => s.Gate.SideChainFilter = false,
            "low_cut" => s => s.Gate.SideChainLowCutHz = 120,
            "high_cut" => s => s.Gate.SideChainHighCutHz = 5000,
            "look_ahead" => s => s.Gate.LookAheadMs = 2,
            "eq_on" => s => s.Eq.Enabled = false,
            "eq_gain" => s => s.Eq.OutputGainDb = 1,
            "eq_band" => s => s.Eq.Bands[1].GainDb = 5,
            "eq_preset" => s => s.Eq.PresetId = "flat",
            "deesser_on" => s => s.DeEsser.Enabled = false,
            "deesser_frequency" => s => s.DeEsser.FrequencyHz = 5000,
            "deesser_amount" => s => s.DeEsser.AmountDb = 3,
            "deesser_sensitivity" => s => s.DeEsser.SensitivityDb = -20,
            _ => s => s.StageOrder = JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder,
        };
        change(b);
        Assert.False(a.SameContentAs(b));
    }

    [Fact]
    public void Clone_carries_every_public_property_deeply_so_a_field_added_later_fails_here_until_it_is_carried()
    {
        var original = Full();
        var copy = original.Clone();
        AssertDeepCopy(original, copy, "TxNoiseSetup");
    }

    /// <summary>Walks every public read-write property: values equal, and reference types (other than strings) distinct objects.</summary>
    private static void AssertDeepCopy(object a, object b, string path)
    {
        foreach (var p in a.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            object? va = p.GetValue(a), vb = p.GetValue(b);
            string at = path + "." + p.Name;
            if (va == null) { Assert.Null(vb); continue; }
            Assert.NotNull(vb);
            var t = p.PropertyType;
            if (t.IsValueType || t == typeof(string)) { Assert.Equal(va, vb); continue; }
            Assert.False(ReferenceEquals(va, vb), at + " is shared between a setup and its clone");
            if (va is IEnumerable ea && vb is IEnumerable eb)
            {
                var la = ea.Cast<object?>().ToList(); var lb = eb.Cast<object?>().ToList();
                Assert.Equal(la.Count, lb.Count);
                for (int i = 0; i < la.Count; i++)
                {
                    if (la[i] is null || la[i] is string || la[i]!.GetType().IsValueType) Assert.Equal(la[i], lb[i]);
                    else AssertDeepCopy(la[i]!, lb[i]!, at + "[" + i + "]");
                }
                continue;
            }
            AssertDeepCopy(va, vb, at);
        }
    }

    [Fact]
    public void Turning_the_expander_off_and_on_never_changes_its_settings()
    {
        var s = Full();
        var before = s.Gate.Clone();
        s.Gate.Enabled = false; s.Gate.Enabled = true;
        Assert.Equal((before.RatioToOne, before.GateSetting, before.SideChainFilter, before.LookAheadMs),
            (s.Gate.RatioToOne, s.Gate.GateSetting, s.Gate.SideChainFilter, s.Gate.LookAheadMs));
    }

    [Fact]
    public void Every_preset_loads_as_its_listed_bands_and_every_band_kind_is_one_the_equaliser_runs()
    {
        Assert.Contains(ParametricEqPresets.All, p => p.Id == "flat" && p.Bands.Count == 0);
        foreach (var p in ParametricEqPresets.All)
        {
            var setup = new ParametricEqSetup { Enabled = true, Bands = p.Bands.Select(b => b.Clone()).ToList(), OutputGainDb = p.OutputGainDb };
            var design = setup.ToDesign()!;
            Assert.Equal(p.Bands.Count, design.BandCount);
            Assert.All(p.Bands, b => Assert.NotNull(ParametricEqKinds.ToKind(b.Kind)));
            Assert.True(p.ForTransmit || p.ForReceive);
            // Loaded, every field is still the operator's: a copy, not the preset's own object.
            if (setup.Bands.Count > 0) { setup.Bands[0].FrequencyHz += 1; Assert.NotEqual(setup.Bands[0].FrequencyHz, p.Bands[0].FrequencyHz); }
        }
        Assert.NotEmpty(ParametricEqPresets.For(transmit: true));
        Assert.NotEmpty(ParametricEqPresets.For(transmit: false));
    }
}
