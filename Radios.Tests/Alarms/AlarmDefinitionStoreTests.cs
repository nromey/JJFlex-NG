using System;
using System.IO;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>Per-serial, atomic, versioned, and unreadable means unavailable — never an empty healthy set.</summary>
    public sealed class AlarmDefinitionStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-alarmstore-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static AlarmDefinitionFile Sample(string serial) => new AlarmDefinitionFile
        {
            RadioSerial = serial,
            Definitions =
            {
                AlarmDefinition.NewLevel("a1", "PA temperature", serial, MeterSelector.From(AlarmMonitorTests.PaTemp),
                    AlarmDirection.AtOrAbove, 60, 2) with { Enabled = true, Revision = 3, PresetKey = "alarms.preset.pa_temperature" },
            },
            RecordOnlyMeters = { MeterSelector.From(AlarmMonitorTests.PaTemp) with { Name = "FWDPWR" } },
        };

        [Fact]
        public void No_file_is_an_empty_usable_set()
        {
            var store = new AlarmDefinitionStore(_root);
            var load = store.Load("1234-5678-9012-3456");
            Assert.Equal(AlarmStoreState.Empty, load.State);
            Assert.True(load.IsUsable);
            Assert.Empty(load.File.Definitions);
        }

        [Fact]
        public void A_saved_file_round_trips_every_field_that_matters()
        {
            var store = new AlarmDefinitionStore(_root);
            Assert.True(store.Save(Sample("1234-5678-9012-3456")));
            var load = store.Load("1234-5678-9012-3456");

            Assert.Equal(AlarmStoreState.Loaded, load.State);
            var d = Assert.Single(load.File.Definitions);
            Assert.Equal("a1", d.Id);
            Assert.Equal(3, d.Revision);
            Assert.True(d.Enabled);
            Assert.Equal(60, d.Threshold);
            Assert.Equal(2, d.Hysteresis);
            Assert.Equal("PATEMP", d.Selector.Name);
            Assert.Equal("TX-", d.Selector.Source);
            Assert.Equal(4, d.Selector.SourceIndex);
            Assert.Equal(Flex.Smoothlake.FlexLib.MeterUnits.DegreesC, d.Selector.Units);
            Assert.Equal("alarms.preset.pa_temperature", d.PresetKey);
            Assert.Equal("FWDPWR", Assert.Single(load.File.RecordOnlyMeters).Name);
        }

        [Fact]
        public void The_file_lives_under_the_per_serial_radios_folder()
        {
            var store = new AlarmDefinitionStore(_root);
            store.Save(Sample("1234-5678-9012-3456"));
            Assert.True(File.Exists(Path.Combine(_root, "radios", "1234-5678-9012-3456", "alarms.json")));
        }

        [Fact]
        public void An_unreadable_file_is_unavailable_not_empty_and_is_left_in_place()
        {
            var store = new AlarmDefinitionStore(_root);
            string path = store.PathFor("1234-5678-9012-3456");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");

            var load = store.Load("1234-5678-9012-3456");
            Assert.Equal(AlarmStoreState.Unavailable, load.State);
            Assert.False(load.IsUsable);
            Assert.Contains("JSON", load.Problem);
            Assert.Equal("{ this is not json", File.ReadAllText(path));
        }

        [Fact]
        public void A_newer_schema_is_refused_rather_than_guessed_at()
        {
            var store = new AlarmDefinitionStore(_root);
            string path = store.PathFor("1234-5678-9012-3456");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"SchemaVersion\": 99, \"Definitions\": [] }");
            var load = store.Load("1234-5678-9012-3456");
            Assert.Equal(AlarmStoreState.Unavailable, load.State);
            Assert.Contains("newer", load.Problem);
        }

        [Fact]
        public void Saving_leaves_no_temp_file_behind_and_the_previous_file_survives_a_failed_write()
        {
            var store = new AlarmDefinitionStore(_root);
            store.Save(Sample("1234-5678-9012-3456"));
            string path = store.PathFor("1234-5678-9012-3456");
            Assert.False(File.Exists(path + ".tmp"));

            // A serial that cannot be a directory is refused; the first file is untouched.
            var bad = Sample("1234-5678-9012-3456");
            bad.RadioSerial = new string('x', 300);
            Assert.False(store.Save(bad));
            Assert.Equal(AlarmStoreState.Loaded, store.Load("1234-5678-9012-3456").State);
        }

        [Fact]
        public void A_serial_is_sanitised_so_it_cannot_leave_the_radios_folder()
        {
            string safe = AlarmDefinitionStore.SafeSerial("/../../evil");
            Assert.Equal("_______evil", safe);
            Assert.DoesNotContain("..", safe);
            Assert.DoesNotContain("/", safe);
            Assert.Equal("1234-5678-9012-3456", AlarmDefinitionStore.SafeSerial("1234-5678-9012-3456"));
            Assert.Equal("unknown-radio", AlarmDefinitionStore.SafeSerial(""));
        }
    }
}
