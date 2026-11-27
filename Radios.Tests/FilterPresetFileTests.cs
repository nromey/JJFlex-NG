#nullable enable
using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// One filter preset as a file another operator can import (#314): export
    /// writes it, import adds it to its mode's list, numbers a colliding name,
    /// changes nothing but the list in memory, and never moves, renames or
    /// rewrites the file it reads. Windowless: the editor only calls these.
    /// </summary>
    public sealed class FilterPresetFileTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "jjflex-filterpreset-" + Guid.NewGuid().ToString("N"));

        public FilterPresetFileTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string PathTo(string name) => Path.Combine(_dir, name);

        [Fact]
        public void An_exported_preset_reads_back_whole()
        {
            string file = PathTo("cw tight.xml");
            Assert.True(FilterPresets.ExportPreset(file, "CW", new FilterPreset("Contest", -75, 75)));

            Assert.True(FilterPresets.TryReadPresetFile(file, "cw tight", out var read));
            Assert.Equal("CW", read!.Mode);
            Assert.Equal("Contest", read.Name);
            Assert.Equal(-75, read.Low);
            Assert.Equal(75, read.High);
        }

        [Fact]
        public void Import_adds_to_the_files_mode_after_that_modes_defaults()
        {
            string file = PathTo("p.xml");
            FilterPresets.ExportPreset(file, "CW", new FilterPreset("Contest", -75, 75));
            FilterPresets.TryReadPresetFile(file, "p", out var read);

            var presets = new FilterPresets();
            int defaults = presets.GetPresetsForMode("CW").Count;
            var (added, mode) = presets.ImportPreset(read!, currentMode: "SSB");

            Assert.Equal("CW", mode);
            Assert.Equal("Contest", added.Name);
            var cw = presets.GetPresetsForMode("CW");
            Assert.Equal(defaults + 1, cw.Count);
            Assert.Same(added, cw.Last());
            // The other modes are untouched.
            Assert.DoesNotContain(presets.Modes, m => m.Mode == "SSB");
        }

        [Fact]
        public void A_colliding_name_is_numbered_letter_case_aside()
        {
            var presets = new FilterPresets();
            var file = new FilterPresetFile { Mode = "SSB", Name = "Wide", Low = 100, High = 2900 };

            Assert.Equal("Wide 2", presets.ImportPreset(file, "SSB").Added.Name);
            Assert.Equal("Wide 3", presets.ImportPreset(file, "SSB").Added.Name);
            var lower = new FilterPresetFile { Mode = "SSB", Name = "wide", Low = 100, High = 2900 };
            Assert.Equal("wide 4", presets.ImportPreset(lower, "SSB").Added.Name);
            Assert.Equal("Fresh", presets.ImportPreset(
                new FilterPresetFile { Mode = "SSB", Name = "Fresh", Low = 100, High = 2900 }, "SSB").Added.Name);
        }

        [Fact]
        public void A_file_naming_no_mode_goes_into_the_mode_shown()
        {
            File.WriteAllText(PathTo("bare.xml"),
                "<?xml version=\"1.0\"?><FilterPreset Version=\"1\"><Name>Mine</Name><Low>200</Low><High>2600</High></FilterPreset>");
            Assert.True(FilterPresets.TryReadPresetFile(PathTo("bare.xml"), "bare", out var read));
            var presets = new FilterPresets();
            Assert.Equal("DIGI", presets.ImportPreset(read!, currentMode: "DIGI").Mode);
        }

        [Fact]
        public void A_file_naming_no_preset_name_takes_the_files_name()
        {
            File.WriteAllText(PathTo("ragchew.xml"),
                "<?xml version=\"1.0\"?><FilterPreset Mode=\"SSB\"><Low>100</Low><High>3100</High></FilterPreset>");
            Assert.True(FilterPresets.TryReadPresetFile(PathTo("ragchew.xml"), "ragchew", out var read));
            Assert.Equal("ragchew", read!.Name);
        }

        [Theory]
        [InlineData("not a preset at all")]
        [InlineData("")]
        [InlineData("<?xml version=\"1.0\"?><FilterPreset Mode=\"SSB\"><Name>Upside</Name><Low>2800</Low><High>100</High></FilterPreset>")]
        [InlineData("<?xml version=\"1.0\"?><FilterPreset Mode=\"RTTY\"><Name>Other</Name><Low>100</Low><High>600</High></FilterPreset>")]
        [InlineData("<?xml version=\"1.0\"?><FilterPresets><Modes /></FilterPresets>")]
        public void An_unreadable_file_is_refused_and_left_exactly_where_and_as_it_was(string content)
        {
            string file = PathTo("bad.xml");
            File.WriteAllText(file, content);
            byte[] before = File.ReadAllBytes(file);

            Assert.False(FilterPresets.TryReadPresetFile(file, "bad", out var read));
            Assert.Null(read);

            // Never set aside, renamed or rewritten: it is someone's file.
            Assert.Equal(new[] { file }, Directory.GetFiles(_dir));
            Assert.Equal(before, File.ReadAllBytes(file));
        }

        [Fact]
        public void A_missing_file_is_refused()
        {
            Assert.False(FilterPresets.TryReadPresetFile(PathTo("nowhere.xml"), "nowhere", out _));
        }

        [Fact]
        public void Import_changes_only_the_list_in_memory()
        {
            // An operator store on disk, then an import: the store is not
            // rewritten (the editor saves only on OK), and the file read is
            // left as it was.
            var store = new FilterPresets();
            Assert.True(store.Save(_dir, "Op"));
            string storePath = PathTo("Op_filterPresets.xml");
            byte[] storeBefore = File.ReadAllBytes(storePath);

            string shared = PathTo("shared.xml");
            FilterPresets.ExportPreset(shared, "SSB", new FilterPreset("Shared", 150, 2650));
            byte[] sharedBefore = File.ReadAllBytes(shared);

            var loaded = FilterPresets.Load(_dir, "Op");
            Assert.True(FilterPresets.TryReadPresetFile(shared, "shared", out var read));
            loaded.ImportPreset(read!, "SSB");

            Assert.Equal(storeBefore, File.ReadAllBytes(storePath));
            Assert.Equal(sharedBefore, File.ReadAllBytes(shared));
            Assert.Equal(2, Directory.GetFiles(_dir).Length);
        }

        [Fact]
        public void The_editor_shows_every_mode_an_import_can_name()
        {
            Assert.Equal(new[] { "SSB", "CW", "DIGI", "AM", "FM" }, FilterPresets.ModeKeys);
            foreach (string variant in new[] { "USB", "LSB", "CWL", "DIGU", "SAM", "NFM" })
            {
                string file = PathTo(variant + ".xml");
                FilterPresets.ExportPreset(file, variant, new FilterPreset("V", 100, 600));
                Assert.True(FilterPresets.TryReadPresetFile(file, variant, out var read), variant);
                Assert.Contains(read!.Mode, FilterPresets.ModeKeys);
            }
        }
    }
}
