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
        /// BOTH slice fields must honour it. A setting obeyed by one of them
        /// is worse than no setting: it would be lying about what it controls,
        /// and the operator would find the inconsistency by arrowing into it
        /// rather than by reading anything.
        /// </summary>
        [Fact]
        public void EveryArrowKeySliceCyclerReadsTheSetting()
        {
            string source = Read("JJFlexWpf/FreqOutHandlers.cs");

            // Positive control first: the file really does contain arrow-key
            // slice cycling, so an empty or wrongly-resolved read cannot pass
            // the absence check below.
            Assert.Contains("SliceStepForDownArrow", source, StringComparison.Ordinal);
            Assert.Contains("SliceStepForUpArrow", source, StringComparison.Ordinal);

            // No arrow-key case may hand the cycler a hard-coded direction.
            // Space, V and the letter keys are untouched by this — they select
            // by identity or cycle forward and have no direction to get wrong,
            // so only the sites reached from Key.Up / Key.Down are checked.
            var arrows = Regex.Matches(source,
                @"case\s+Key\.(Up|Down)\s*:\s*(?://[^\n]*\n\s*)*CycleVFO\(\s*(-?\d+)");

            Assert.True(arrows.Count == 0,
                "An arrow-key slice cycler is back to a fixed direction, so the "
                + "slice arrow order setting no longer controls it: "
                + string.Join("; ", System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Cast<Match>(arrows), m => m.Value.Trim()))
                + ". Use SliceStepForUpArrow / SliceStepForDownArrow (#318).");
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
