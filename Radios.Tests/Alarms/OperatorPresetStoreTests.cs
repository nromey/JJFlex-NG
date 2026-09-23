using System;
using System.IO;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>The operator's own presets: named by them, kept on their machine, applied by re-discovering the meter.</summary>
    public sealed class OperatorPresetStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-oppreset-" + Guid.NewGuid().ToString("N"));
        private static readonly MeterDescriptor Pa = PaTemperatureReplayFixture.Meter;

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static AlarmDefinition Mine() =>
            AlarmDefinition.NewLevel("orig", "Mine", "8600", MeterSelector.From(Pa), AlarmDirection.AtOrAbove, 55, 2)
                with { Enabled = true, Revision = 4, IsTest = true, PresetKey = "alarms.preset.pa_temperature" };

        [Fact]
        public void A_saved_preset_drops_the_radio_the_id_the_intent_and_the_shipped_key()
        {
            var store = new OperatorPresetStore(_root);
            Assert.True(store.Save("  My line  ", Mine()));
            var p = Assert.Single(store.Load(out string problem));
            Assert.Equal("", problem);
            Assert.Equal("My line", p.Name);
            Assert.Equal("", p.Definition.Id);
            Assert.Equal("", p.Definition.RadioSerial);
            Assert.False(p.Definition.Enabled);
            Assert.False(p.Definition.IsTest);
            Assert.Equal("", p.Definition.PresetKey);
            Assert.Equal(55, p.Definition.Threshold);
            Assert.Equal("PATEMP", p.Definition.Selector.Name);
        }

        [Fact]
        public void Saving_the_same_name_replaces_and_the_id_is_kept()
        {
            var store = new OperatorPresetStore(_root);
            store.Save("My line", Mine());
            string id = store.Load(out _)[0].Id;
            store.Save("my LINE", Mine() with { Threshold = 58 });
            var p = Assert.Single(store.Load(out _));
            Assert.Equal(id, p.Id);
            Assert.Equal(58, p.Definition.Threshold);
        }

        [Fact]
        public void Apply_re_resolves_the_meter_on_the_target_radio_and_reports_when_it_is_not_there()
        {
            var store = new OperatorPresetStore(_root);
            store.Save("My line", Mine());
            OperatorPreset p = store.Load(out _)[0];

            var def = OperatorPresetStore.Apply(p, "6300", new[] { Pa with { Index = 9 } }, out MeterSelectorResolution r);
            Assert.True(r.IsResolved);
            Assert.Equal(9, r.Match!.Index);
            Assert.Equal("6300", def.RadioSerial);
            Assert.NotEqual("", def.Id);
            Assert.False(def.Enabled);

            OperatorPresetStore.Apply(p, "8400", Array.Empty<MeterDescriptor>(), out MeterSelectorResolution missing);
            Assert.Equal(MeterSelectorStatus.Missing, missing.Status);
        }

        [Fact]
        public void An_unreadable_file_is_reported_and_never_overwritten()
        {
            var store = new OperatorPresetStore(_root);
            Directory.CreateDirectory(_root);
            File.WriteAllText(store.Path_, "{ nope");
            Assert.Empty(store.Load(out string problem));
            Assert.NotEqual("", problem);
            Assert.False(store.Save("x", Mine()));
            Assert.Equal("{ nope", File.ReadAllText(store.Path_));
        }

        [Fact]
        public void A_blank_or_over_long_name_is_refused_and_delete_removes_by_id()
        {
            var store = new OperatorPresetStore(_root);
            Assert.False(store.Save("   ", Mine()));
            Assert.False(store.Save(new string('n', AlarmDefinition.MaxNameLength + 1), Mine()));
            store.Save("Keep", Mine());
            string id = store.Load(out _)[0].Id;
            Assert.True(store.Delete(id));
            Assert.Empty(store.Load(out _));
        }
    }
}
