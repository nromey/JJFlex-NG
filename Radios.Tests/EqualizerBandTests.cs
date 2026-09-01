using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 43 Track C (#431, #457). The equalizer surface covers every band
    /// the radio has, and can still say so after somebody edits it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a test and not a comment.</b> The band list was eight of nine for
    /// seven months. Nothing failed: a missing band produces no error, no
    /// warning and no visible gap — a preset simply cannot carry it, and a
    /// dialog simply does not show it. The old wrapper even carried a comment
    /// explaining the omission, which is the failure mode the track contract
    /// names outright: every "please keep these in step" comment in this
    /// codebase has eventually been ignored by a future editor.
    /// </para>
    /// <para>
    /// <b>The first test is the load-bearing one</b>, because it asks FlexLib
    /// rather than a list somebody typed. If FlexRadio ever adds or drops a
    /// band, this fails on the next build instead of on a user report.
    /// </para>
    /// </remarks>
    public sealed class EqualizerBandTests
    {
        /// <summary>
        /// Every band FlexLib's Equalizer carries, read out of the type itself.
        /// </summary>
        private static int[] FlexLibBands()
        {
            var rx = new Regex(@"^level_(\d+)Hz$", RegexOptions.CultureInvariant);
            return typeof(Flex.Smoothlake.FlexLib.Equalizer)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => rx.Match(p.Name))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .OrderBy(hz => hz)
                .ToArray();
        }

        /// <summary>
        /// The positive control the negative result needs. If the reflection
        /// above ever stops matching — a rename, a move to fields, a different
        /// naming convention — every other assertion built on it would pass
        /// vacuously by comparing two empty sets. So prove it found something
        /// real, and something we can name, before trusting it.
        /// </summary>
        [Fact]
        public void Reflection_actually_finds_FlexLib_bands()
        {
            int[] bands = FlexLibBands();

            Assert.NotEmpty(bands);
            Assert.Contains(1000, bands);   // a band no equalizer could lack
            Assert.Contains(32, bands);     // the one this work is about
        }

        [Fact]
        public void Band_table_covers_every_band_the_radio_carries()
        {
            Assert.Equal(FlexLibBands(), FlexBase.EqBandHz);
        }

        [Fact]
        public void Band_table_is_nine_bands_lowest_first()
        {
            Assert.Equal(new[] { 32, 63, 125, 250, 500, 1000, 2000, 4000, 8000 },
                         FlexBase.EqBandHz);
        }

        [Fact]
        public void Band_table_is_strictly_ascending_and_has_no_duplicates()
        {
            int[] bands = FlexBase.EqBandHz;
            for (int i = 1; i < bands.Length; i++)
            {
                Assert.True(bands[i] > bands[i - 1],
                    $"band {i} ({bands[i]} Hz) must be above band {i - 1} ({bands[i - 1]} Hz) — " +
                    "the reading order of the dialog IS the frequency order");
            }
        }

        [Fact]
        public void Every_band_index_reads_back_what_was_written()
        {
            var s = new FlexBase.TxEqSettings();

            // A different value per band, so a getter or setter wired to the
            // wrong field is caught rather than hidden behind matching numbers.
            for (int i = 0; i < FlexBase.EqBandHz.Length; i++)
            {
                FlexBase.SetEqBand(s, i, i - 4);   // -4 .. +4, spans zero
            }

            for (int i = 0; i < FlexBase.EqBandHz.Length; i++)
            {
                Assert.Equal(i - 4, FlexBase.GetEqBand(s, i));
            }
        }

        [Fact]
        public void The_bottom_band_is_reachable_by_index_zero()
        {
            var s = new FlexBase.TxEqSettings();
            FlexBase.SetEqBand(s, 0, -7);

            Assert.Equal(-7, s.Hz32);
            Assert.Equal(-7, FlexBase.GetEqBand(s, 0));
            Assert.Equal(32, FlexBase.EqBandHz[0]);
        }

        [Fact]
        public void Levels_are_clamped_to_the_radios_range()
        {
            var s = new FlexBase.TxEqSettings();

            FlexBase.SetEqBand(s, 0, 500);
            Assert.Equal(FlexBase.EqLevelMax, FlexBase.GetEqBand(s, 0));

            FlexBase.SetEqBand(s, 0, -500);
            Assert.Equal(FlexBase.EqLevelMin, FlexBase.GetEqBand(s, 0));
        }

        [Fact]
        public void An_index_off_the_end_is_ignored_rather_than_thrown()
        {
            var s = new FlexBase.TxEqSettings();
            FlexBase.SetEqBand(s, 9, 5);
            FlexBase.SetEqBand(s, -1, 5);

            Assert.Equal(0, FlexBase.GetEqBand(s, 9));
            Assert.Equal(0, FlexBase.GetEqBand(s, -1));

            // and nothing real was touched on the way past
            for (int i = 0; i < FlexBase.EqBandHz.Length; i++)
                Assert.Equal(0, FlexBase.GetEqBand(s, i));
        }

        [Fact]
        public void Transmit_and_receive_snapshots_are_different_types()
        {
            // A receive curve handed to the transmit apply would be silent,
            // wrong and on the air. The compiler refuses it because these are
            // separate types; this records that as intent rather than accident.
            Assert.NotEqual(typeof(FlexBase.TxEqSettings), typeof(FlexBase.RxEqSettings));
            Assert.True(typeof(FlexBase.EqSettings).IsAssignableFrom(typeof(FlexBase.TxEqSettings)));
            Assert.True(typeof(FlexBase.EqSettings).IsAssignableFrom(typeof(FlexBase.RxEqSettings)));
        }

        /// <summary>
        /// The snapshot types carry exactly the nine bands and nothing else, so
        /// a tenth field added without a matching index cannot sit there
        /// unreachable.
        /// </summary>
        [Fact]
        public void The_snapshot_carries_one_field_per_band_and_no_more()
        {
            var rx = new Regex(@"^Hz(\d+)$", RegexOptions.CultureInvariant);
            int[] fields = typeof(FlexBase.TxEqSettings)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => rx.Match(f.Name))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .OrderBy(hz => hz)
                .ToArray();

            Assert.Equal(FlexBase.EqBandHz, fields);
        }
    }

    /// <summary>
    /// The preset file carries all nine bands, and an older file that carries
    /// eight is still recognisable as such.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the #427 shape the finding named: something recorded with no
    /// route back. A preset that captures a bottom band it cannot restore is
    /// worse than one that never captured it, because the operator believes
    /// the curve is saved.
    /// </para>
    /// </remarks>
    public sealed class AudioChainPresetEqTests
    {
        private static string TempFile()
            => Path.Combine(Path.GetTempPath(), "jjflex-eq-" + Guid.NewGuid().ToString("N") + ".xml");

        [Fact]
        public void All_nine_bands_survive_a_save_and_load()
        {
            var preset = new AudioChainPreset("nine bands")
            {
                SchemaVersion = AudioChainPreset.CurrentSchemaVersion,
                TxEqCaptured = true,
                TxEqEnabled = true,
                TxEq32 = -9,
                TxEq63 = -6,
                TxEq125 = -3,
                TxEq250 = -1,
                TxEq500 = 0,
                TxEq1000 = 1,
                TxEq2000 = 3,
                TxEq4000 = 6,
                TxEq8000 = 9,
            };

            string path = TempFile();
            try
            {
                Assert.True(preset.Save(path));
                Assert.True(AudioChainPreset.TryLoad(path, out var loaded));

                Assert.True(loaded.TxEqCaptured);
                Assert.True(loaded.TxEqEnabled);
                Assert.Equal(-9, loaded.TxEq32);
                Assert.Equal(-6, loaded.TxEq63);
                Assert.Equal(-3, loaded.TxEq125);
                Assert.Equal(-1, loaded.TxEq250);
                Assert.Equal(0, loaded.TxEq500);
                Assert.Equal(1, loaded.TxEq1000);
                Assert.Equal(3, loaded.TxEq2000);
                Assert.Equal(6, loaded.TxEq4000);
                Assert.Equal(9, loaded.TxEq8000);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void The_bottom_band_is_actually_written_to_the_file()
        {
            // Not just a round trip through the same object graph: read the
            // XML back as text and prove the element is on disk, because a
            // property the serializer silently skipped would still round-trip
            // in memory within one process.
            var preset = new AudioChainPreset("on disk")
            {
                SchemaVersion = AudioChainPreset.CurrentSchemaVersion,
                TxEqCaptured = true,
                TxEq32 = -4,
            };

            string path = TempFile();
            try
            {
                Assert.True(preset.Save(path));
                string xml = File.ReadAllText(path);
                Assert.Contains("<TxEq32>-4</TxEq32>", xml);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void A_file_written_before_the_bottom_band_existed_is_still_identifiable()
        {
            // A version 1 file has eight bands and no TxEq32 element at all.
            // It must load, and it must still SAY it is version 1 — the
            // absence has to stay detectable, because 0 is a legal band level
            // and so the value itself can never reveal it.
            string xml =
                "<?xml version=\"1.0\"?>\n" +
                "<AudioChainPreset schemaVersion=\"1\" " +
                "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
                "  <Name>old eight band</Name>\n" +
                "  <TxEqCaptured>true</TxEqCaptured>\n" +
                "  <TxEqEnabled>true</TxEqEnabled>\n" +
                "  <TxEq63>2</TxEq63>\n" +
                "  <TxEq8000>-2</TxEq8000>\n" +
                "</AudioChainPreset>\n";

            string path = TempFile();
            try
            {
                File.WriteAllText(path, xml);
                Assert.True(AudioChainPreset.TryLoad(path, out var loaded));

                Assert.Equal(1, loaded.SchemaVersion);
                Assert.True(loaded.SchemaVersion < AudioChainPreset.CurrentSchemaVersion);
                Assert.Equal(2, loaded.TxEq63);
                Assert.Equal(-2, loaded.TxEq8000);
                Assert.Equal(0, loaded.TxEq32);   // absent, not chosen
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void The_schema_version_this_build_writes_knows_about_the_bottom_band()
        {
            Assert.True(AudioChainPreset.CurrentSchemaVersion >= 2,
                "TxEq32 arrived at schema version 2; a build that writes a lower " +
                "version number would label its own nine-band files as eight-band ones");

            var captured = new AudioChainPreset("fresh");
            Assert.Equal(0, captured.SchemaVersion);   // absence stays detectable
        }
    }
}
