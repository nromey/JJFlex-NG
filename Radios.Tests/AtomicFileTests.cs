using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HamBands;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The operator settings stores write whole or not at all: a failure part
    /// way through a save leaves the previous file as it was, never an empty or
    /// half-written one that loads as defaults. One helper,
    /// <see cref="AtomicFile"/>, and the stores converted to it.
    /// </summary>
    public sealed class AtomicFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-atomic-" + Guid.NewGuid().ToString("N"));

        public AtomicFileTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void A_write_that_fails_part_way_leaves_the_previous_file_byte_for_byte()
        {
            string path = Path.Combine(_dir, "settings.xml");
            byte[] before = Encoding.UTF8.GetBytes("<settings>the operator's saved choices</settings>");
            File.WriteAllBytes(path, before);

            var thrown = Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, s =>
            {
                s.Write(new byte[] { 0x3C, 0x6E }, 0, 2);   // part of the new file
                throw new InvalidOperationException("serializer failed");
            }));

            Assert.Equal("serializer failed", thrown.Message);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(AtomicFile.TempFilesFor(path));   // the temporary file is removed on failure
            Assert.Single(Directory.GetFiles(_dir));
        }

        [Fact]
        public void A_write_that_succeeds_replaces_the_file_and_leaves_no_temporary_file()
        {
            string path = Path.Combine(_dir, "settings.xml");
            File.WriteAllText(path, "old");

            AtomicFile.Write(path, s => { var b = Encoding.UTF8.GetBytes("new"); s.Write(b, 0, b.Length); });

            Assert.Equal("new", File.ReadAllText(path));
            Assert.Empty(AtomicFile.TempFilesFor(path));
        }

        [Fact]
        public void A_first_write_creates_the_file()
        {
            string path = Path.Combine(_dir, "fresh.json");
            AtomicFile.WriteAllText(path, "{}");
            Assert.Equal("{}", File.ReadAllText(path));
        }

        [Fact]
        public void Text_is_written_as_File_WriteAllText_writes_it_with_no_byte_order_mark()
        {
            string ours = Path.Combine(_dir, "ours.json");
            string theirs = Path.Combine(_dir, "theirs.json");
            const string text = "{ \"name\": \"Zoë\" }";

            AtomicFile.WriteAllText(ours, text);
            File.WriteAllText(theirs, text);

            Assert.Equal(File.ReadAllBytes(theirs), File.ReadAllBytes(ours));
        }

        [Fact]
        public void A_temporary_file_a_crash_left_behind_is_removed_by_a_later_save_once_it_is_old()
        {
            string path = Path.Combine(_dir, "settings.xml");
            string leftover = AtomicFile.NewTempPathFor(path);
            File.WriteAllText(leftover, "left behind by an earlier crash");
            File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow - AtomicFile.LeftoverAge - TimeSpan.FromMinutes(1));
            Assert.Single(AtomicFile.TempFilesFor(path));   // positive control: the scan sees the leftover

            AtomicFile.WriteAllText(path, "saved");

            Assert.Equal("saved", File.ReadAllText(path));
            Assert.Empty(AtomicFile.TempFilesFor(path));
            Assert.Single(Directory.GetFiles(_dir));
        }

        [Fact]
        public void A_young_temporary_file_is_left_alone_because_it_may_be_another_save_in_flight()
        {
            string path = Path.Combine(_dir, "settings.xml");
            string inFlight = AtomicFile.NewTempPathFor(path);
            File.WriteAllText(inFlight, "another save's pending contents");

            AtomicFile.WriteAllText(path, "saved");

            Assert.Equal("saved", File.ReadAllText(path));
            Assert.Equal("another save's pending contents", File.ReadAllText(inFlight));
        }

        [Fact]
        public void Each_write_has_its_own_temporary_file()
        {
            string path = Path.Combine(_dir, "settings.xml");
            Assert.NotEqual(AtomicFile.NewTempPathFor(path), AtomicFile.NewTempPathFor(path));
            Assert.Equal(_dir, Path.GetDirectoryName(AtomicFile.NewTempPathFor(path)));   // same folder, same volume
        }

        // ---- two saves of one file at once ----

        [Fact]
        public void A_save_that_fails_while_another_waits_to_replace_the_file_removes_only_its_own_temporary_file()
        {
            // Writer A has written and closed its temporary file and is about to
            // replace the real one. Writer B, saving the same file, starts and
            // fails part-way. With one temporary name shared by every write, B
            // truncated A's pending file and its cleanup deleted it, so A's
            // replace found nothing and A failed.
            string path = Path.Combine(_dir, "settings.xml");
            File.WriteAllText(path, "old");
            Exception? bThrew = null;
            bool bRan = false;

            AtomicFile.BeforeReplaceForTests = p =>
            {
                if (p != path || bRan) return;
                bRan = true;
                AtomicFile.BeforeReplaceForTests = null;
                bThrew = Record.Exception(() => AtomicFile.Write(path, s =>
                {
                    s.Write(new byte[] { 0x42 }, 0, 1);
                    throw new InvalidOperationException("B's serializer failed");
                }));
            };
            try
            {
                AtomicFile.WriteAllText(path, "A");   // must not throw
            }
            finally
            {
                AtomicFile.BeforeReplaceForTests = null;
            }

            Assert.True(bRan, "positive control: B ran inside A's window");
            Assert.IsType<InvalidOperationException>(bThrew);
            Assert.Equal("B's serializer failed", bThrew!.Message);
            Assert.Equal("A", File.ReadAllText(path));
            Assert.Empty(AtomicFile.TempFilesFor(path));
        }

        [Fact]
        public void A_save_that_starts_while_another_is_still_writing_gets_its_own_file_and_succeeds()
        {
            // Writer A is part-way through writing. Writer B saves the same file
            // in full. With a shared temporary name B could not even open it.
            string path = Path.Combine(_dir, "settings.xml");
            Exception? bThrew = null;
            bool bRan = false;

            AtomicFile.Write(path, s =>
            {
                s.Write(new byte[] { 0x41 }, 0, 1);
                bRan = true;
                bThrew = Record.Exception(() => AtomicFile.WriteAllText(path, "B"));
            });

            Assert.True(bRan);
            Assert.Null(bThrew);
            Assert.Equal("A", File.ReadAllText(path));   // the last to replace wins; the file is whole
            Assert.Empty(AtomicFile.TempFilesFor(path));
        }

        // ---- the stores keep what their callers see ----

        [Fact]
        public void A_store_that_reported_failure_with_false_still_does_and_leaves_no_temporary_file()
        {
            // The target is a folder, so the replace cannot happen: the old
            // truncate-first write failed here too, and returned false.
            string op = "Op_" + Guid.NewGuid().ToString("N");
            string target = Path.Combine(_dir, op + "_filterPresets.xml");
            Directory.CreateDirectory(target);

            Assert.False(new FilterPresets().Save(_dir, op));
            Assert.Empty(AtomicFile.TempFilesFor(target));
        }

        [Fact]
        public void A_store_that_rethrew_still_rethrows()
        {
            string op = "Op_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(_dir, op + "_autoConnectV2.xml"));

            Assert.ThrowsAny<Exception>(() => new AutoConnectConfig().Save(_dir, op));
            Assert.Empty(AtomicFile.TempFilesFor(Path.Combine(_dir, op + "_autoConnectV2.xml")));
        }

        [Fact]
        public void A_store_that_swallowed_and_traced_still_does()
        {
            string op = "Op_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(_dir, op + "_licenseConfig.xml"));

            new LicenseConfig().Save(_dir, op);   // must not throw
        }

        [Fact]
        public void The_license_round_trips_through_the_new_write()
        {
            string op = "Op_" + Guid.NewGuid().ToString("N");
            new LicenseConfig { LicenseClass = Bands.Licenses.general, TxLockout = true }.Save(_dir, op);

            var back = LicenseConfig.Load(_dir, op);
            Assert.Equal(Bands.Licenses.general, back.LicenseClass);
            Assert.True(back.TxLockout);
            Assert.Single(Directory.GetFiles(_dir));
        }

        [Fact]
        public void Push_to_talk_and_filter_presets_round_trip_through_the_new_write()
        {
            string op = "Op_" + Guid.NewGuid().ToString("N");
            new PttConfig().Save(_dir, op);
            Assert.NotNull(PttConfig.Load(_dir, op));

            Assert.True(new FilterPresets().Save(_dir, op));
            Assert.NotNull(FilterPresets.Load(_dir, op));

            Assert.Empty(Directory.GetFiles(_dir, "*" + AtomicFile.TempSuffix));   // no temporary file of any store left
        }

        // ---- no converted store goes back to truncating first ----

        private static readonly string[] ConvertedStores =
        {
            "PersonalData.vb",
            "Radios/PttConfig.cs",
            "Radios/LicenseConfig.cs",
            "Radios/AccessibilityConfig.cs",
            "Radios/BandMemory.cs",
            "Radios/FilterPresets.cs",
            "Radios/KeyerSpeedStore.cs",
            "Radios/SystemWideKeysConfig.cs",
            "Radios/DiagnosticsConfig.cs",
            "Radios/AutoConnectConfig.cs",
            "Radios/ConnectPathLearningConfig.cs",
            "Radios/SmartLinkAccountManager.cs",
            "Radios/PanAdapterManager.cs",
            "Radios/PanRanges.cs",
        };

        /// <summary>A write that truncates the real file before the new contents exist.</summary>
        private static readonly Regex TruncateFirst = new(
            @"(?<![A-Za-z_])File\.(Create|WriteAllText|WriteAllBytes|WriteAllLines|OpenWrite)\(|FileMode\.Create\b|SetLength\(0\)",
            RegexOptions.Compiled);

        private static IEnumerable<string> TruncateFirstLines(string source) =>
            source.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("//") && !l.StartsWith("'") && TruncateFirst.IsMatch(l));

        [Fact]
        public void The_scan_finds_a_planted_truncate_first_write()
        {
            Assert.Single(TruncateFirstLines("x\n    using var fs = File.Create(path);\ny"));
            Assert.Single(TruncateFirstLines("x\n    s = File.Open(path, FileMode.Create)\ny"));
            Assert.Single(TruncateFirstLines("x\n    File.WriteAllText(path, json);\ny"));
            Assert.Empty(TruncateFirstLines("x\n    AtomicFile.WriteAllText(path, json);\ny"));
            Assert.Empty(TruncateFirstLines("x\n    s = File.Open(path, FileMode.CreateNew)\ny"));
        }

        [Fact]
        public void No_converted_store_truncates_its_file_before_writing()
        {
            string root = RepoRoot();
            var offenders = new List<string>();
            foreach (string rel in ConvertedStores)
            {
                string source = File.ReadAllText(Path.Combine(root, rel));
                Assert.Contains("AtomicFile.Write", source);   // positive control: the file is the store we think it is
                offenders.AddRange(TruncateFirstLines(source).Select(l => rel + ": " + l));
            }
            Assert.Empty(offenders);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }
    }
}
