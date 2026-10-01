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
        public void A_file_that_parses_but_is_not_usable_is_unavailable_with_the_reason_and_left_in_place()
        {
            // Astra's Track I review, finding 13: "loaded" meant "parsed".
            // A null element threw after the file was reported loaded; blank
            // and duplicate ids were skipped in silence; a definition that
            // would never pass its own validation ran anyway.
            var store = new AlarmDefinitionStore(_root);
            string path = store.PathFor("1234-5678-9012-3456");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string nullElement = "{ \"SchemaVersion\": 1, \"Definitions\": [ null ] }";
            File.WriteAllText(path, nullElement);
            var load = store.Load("1234-5678-9012-3456");
            Assert.Equal(AlarmStoreState.Unavailable, load.State);
            Assert.Contains("definition 1 of 1 is empty", load.Problem);
            Assert.Equal(nullElement, File.ReadAllText(path));   // preserved for the operator to look at

            store.Save(Sample("1234-5678-9012-3456"));
            string json = File.ReadAllText(path);

            // Two definitions with one id. Save does not validate — only Load
            // does, because the file is what a hand can edit.
            var twice = Sample("1234-5678-9012-3456");
            twice.Definitions.Add(twice.Definitions[0] with { Name = "The same again" });
            store.Save(twice);
            load = store.Load("1234-5678-9012-3456");
            Assert.Equal(AlarmStoreState.Unavailable, load.State);
            Assert.Contains("repeats the id a1", load.Problem);

            // A definition its own validation refuses: a negative clear margin.
            File.WriteAllText(path, json.Replace("\"Hysteresis\": 2", "\"Hysteresis\": -2"));
            load = store.Load("1234-5678-9012-3456");
            Assert.Equal(AlarmStoreState.Unavailable, load.State);
            Assert.Contains("hysteresis", load.Problem);
            Assert.Contains("PA temperature", load.Problem);

            // A blank id.
            File.WriteAllText(path, json.Replace("\"Id\": \"a1\"", "\"Id\": \"\""));
            load = store.Load("1234-5678-9012-3456");
            Assert.Equal(AlarmStoreState.Unavailable, load.State);
            Assert.Contains("has no id", load.Problem);

            // Positive control: the file as saved is loaded and usable.
            File.WriteAllText(path, json);
            Assert.Equal(AlarmStoreState.Loaded, store.Load("1234-5678-9012-3456").State);
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
