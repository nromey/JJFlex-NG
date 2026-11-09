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
        /// #517 (ruled by Noel 2026-09-20): the bare arrows TUNE on the Slice
        /// and Slice operations fields, so nothing walks the slice list by
        /// arrow and nothing in the field handlers reads this setting any
        /// more. This test said the opposite until 2026-11-09 (it pinned every
        /// arrow-key slice cycler to the setting); it now pins that the
        /// arrows no longer cycle slices at all, while the setting, its
        /// control and the saved value above stay as they are until Noel rules
        /// whether the setting waits for the slice layer or leaves Settings.
        /// </summary>
        [Fact]
        public void NoArrowKeyCyclesSlicesOnHomeSoTheSettingHasNoReaderThere()
        {
            string source = Read("JJFlexWpf/FreqOutHandlers.cs");

            // Positive control: the file still cycles slices (Space and V), so
            // an empty or wrongly-resolved read cannot pass the checks below.
            Assert.Contains("CycleVFO(1, wrap: true)", source, StringComparison.Ordinal);

            var arrows = Regex.Matches(source,
                @"case\s+Key\.(Up|Down)\s*:\s*(?://[^\n]*\n\s*)*CycleVFO\(");
            Assert.True(arrows.Count == 0,
                "An arrow key cycles slices on Home again, against #517: "
                + string.Join("; ", System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Cast<Match>(arrows), m => m.Value.Trim())));
            Assert.DoesNotContain("AccessibilityConfig.Current.SliceStepForDownArrow", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SliceStepForUpArrow =>", source, StringComparison.Ordinal);
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
