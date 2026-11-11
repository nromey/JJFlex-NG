using System;
using System.IO;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 43 Track E, #318. Down-arrow walks the slice letters forwards,
    /// and an operator who prefers the old direction can have it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noel, 2026-08-11:</b> the slice list navigated bottom-to-top — you
    /// arrowed UP to go A, B, C. Reading order says Down should do that, and
    /// on a list a screen-reader operator has no visual layout to correct the
    /// mismatch against.
    /// </para>
    /// <para>
    /// <b>His ruling the same day was a setting, not a flip.</b> We pick the
    /// better default; anyone with months of the old direction in their
    /// fingers keeps it with one radio button. Both halves are pinned here,
    /// because a default that is right and a choice that does not persist is
    /// still only half the ruling.
    /// </para>
    /// </remarks>
    // AccessibilityConfig.Load records where the operator file lives, so
    // the classes that call it share this collection rather than racing.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SliceArrowOrderTests
    {
        [Fact]
        public void TheDefaultIsReadingOrder()
        {
            var config = new AccessibilityConfig();
            Assert.Equal(SliceArrowOrder.TopToBottom, config.SliceArrowOrder);
            Assert.Equal(1, config.SliceStepForDownArrow);
        }

        [Fact]
        public void ChoosingTheOldDirectionReversesTheStep()
        {
            var config = new AccessibilityConfig { SliceArrowOrder = SliceArrowOrder.BottomToTop };
            Assert.Equal(-1, config.SliceStepForDownArrow);
        }

        [Fact]
        public void AHandEditedConfigFileCannotProduceANonsenseDirection()
        {
            // Validate() runs after deserialisation. An out-of-range value must
            // fall back to the default rather than leaving the arrows meaning
            // nothing at all.
            var config = new AccessibilityConfig { SliceArrowOrder = (SliceArrowOrder)42 };
            config.Validate();
            Assert.Equal(SliceArrowOrder.TopToBottom, config.SliceArrowOrder);
        }

        [Fact]
        public void TheChoiceSurvivesASaveAndLoad()
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "jjflex-slice-order-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                new AccessibilityConfig { SliceArrowOrder = SliceArrowOrder.BottomToTop }
                    .Save(dir, "tester");

                var loaded = AccessibilityConfig.Load(dir, "tester");
                Assert.Equal(SliceArrowOrder.BottomToTop, loaded.SliceArrowOrder);
                Assert.Equal(-1, loaded.SliceStepForDownArrow);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
                // Leave the process-wide Current back at its default, since
                // Load and Save both set it as a side effect.
                AccessibilityConfig.Load(dir, "tester-absent");
            }
        }

        /// <summary>
        /// #517, ruled by Noel 2026-11-09 20:50 and 21:01: the Slice field's
        /// arrows switch slices again, unless "Tune with Up and Down on the Slice field"
        /// is checked, and while they switch slices they honour this setting.
        /// The Slice operations field never walked the slice list, so it never
        /// reads the setting. This test said "no arrow key cycles slices on
        /// Home" from the morning of 2026-11-09 until the 21:01 ruling reversed
        /// that; before the morning it pinned BOTH fields to the setting, which
        /// was never true of Slice operations.
        /// </summary>
        [Fact]
        public void TheSliceFieldsArrowsHonourTheSettingAndSliceOperationsNeverReadsIt()
        {
            string source = Read("JJFlexWpf/FreqOutHandlers.cs").Replace("\r\n", "\n");

            Assert.Contains("private static int SliceStepForDownArrow =>\n        Radios.AccessibilityConfig.Current.SliceStepForDownArrow;", source, StringComparison.Ordinal);
            Assert.Contains("private static int SliceStepForUpArrow => -SliceStepForDownArrow;", source, StringComparison.Ordinal);

            string slice = Body(source, "public void AdjustSlice(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Contains("CycleVFO(key == Key.Up ? SliceStepForUpArrow : SliceStepForDownArrow);", slice, StringComparison.Ordinal);

            string ops = Body(source, "public void AdjustSliceOps(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Contains("AdjustGain(", ops, StringComparison.Ordinal);   // positive control: the body was read
            Assert.DoesNotContain("SliceStepFor", ops, StringComparison.Ordinal);
            Assert.DoesNotContain("CycleVFO(", ops, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTwoArrowBoxesSurviveASaveAndLoadAndAnOldFileKeepsTheDefaults()
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "jjflex-slice-arrows-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                new AccessibilityConfig { SliceFieldArrowsTune = true, SliceOpsFieldArrowsTune = false }
                    .Save(dir, "tester");
                var loaded = AccessibilityConfig.Load(dir, "tester");
                Assert.True(loaded.SliceFieldArrowsTune);
                Assert.False(loaded.SliceOpsFieldArrowsTune);

                // A file written before the boxes existed: the initialisers win.
                File.WriteAllText(Path.Combine(dir, "older_accessibilityConfig.xml"),
                    "<?xml version=\"1.0\"?><AccessibilityConfig><SliceArrowOrder>BottomToTop</SliceArrowOrder></AccessibilityConfig>");
                var older = AccessibilityConfig.Load(dir, "older");
                Assert.Equal(SliceArrowOrder.BottomToTop, older.SliceArrowOrder);   // the file was read
                Assert.False(older.SliceFieldArrowsTune);
                Assert.True(older.SliceOpsFieldArrowsTune);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
                AccessibilityConfig.Load(dir, "tester-absent");
            }
        }

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " was not found");
            int open = source.IndexOf('{', at), depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException(signature);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject passes every absence check it makes.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
