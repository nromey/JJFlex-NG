using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 45 Track D2 — the #529 focus watchdog's off switch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> The note that went to Don on 2026-09-05 says, in
    /// Noel's own words: <i>"if a JJ Flexible window ever comes to the front
    /// while you are doing something else, and you did not ask it to, that is
    /// this. Tell me when and what you were doing and I will switch it off.
    /// There is no setting for it yet, which is my fault and is on the list."</i>
    /// A promise made to a tester that can only be kept by rebuilding the
    /// application is not a promise the tester can act on.
    /// </para>
    /// <para>
    /// <b>Default ON, and it stays that way.</b> The watchdog's negative case
    /// was measured at the radio on 2026-09-05 — the desktop held the
    /// foreground over one of our modals for 115 seconds with the operator
    /// genuinely absent, and nothing reclaimed. This is the switch for when it
    /// misbehaves, not a retreat from it, so the default is pinned here.
    /// </para>
    /// <para>
    /// The decision half of the switch is pinned in
    /// <see cref="StrandedFocusSentinelTests"/>; this file pins the
    /// PERSISTENCE, the default, and the two wiring facts a refactor could
    /// silently undo.
    /// </para>
    /// </remarks>
    public sealed class FocusWatchdogSettingTests
    {
        [Fact]
        public void TheWatchdogIsOnUntilTheOperatorSaysOtherwise()
        {
            Assert.True(new AccessibilityConfig().ReclaimStolenForeground);
        }

        [Fact]
        public void TheChoiceSurvivesASaveAndLoad()
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "jjflex-focus-watchdog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                new AccessibilityConfig { ReclaimStolenForeground = false }.Save(dir, "tester");

                // Positive control: a value that round-trips as false must be
                // shown to round-trip as true as well, or "false" could just be
                // a config file that never loaded at all.
                Assert.False(AccessibilityConfig.Load(dir, "tester").ReclaimStolenForeground);

                new AccessibilityConfig { ReclaimStolenForeground = true }.Save(dir, "tester");
                Assert.True(AccessibilityConfig.Load(dir, "tester").ReclaimStolenForeground);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
                AccessibilityConfig.Load(dir, "tester-absent");
            }
        }

        /// <summary>
        /// Every operator upgrading into this build has an accessibility config
        /// file with no such element in it. A missing element must leave the
        /// watchdog ON — the setting is new, their preference is not recorded,
        /// and the safe reading of silence is the default. If XmlSerializer
        /// ever started zeroing absent booleans, every existing operator would
        /// silently lose the rescue and nothing would say so.
        /// </summary>
        [Fact]
        public void AConfigFileWrittenBeforeThisSettingExistedKeepsTheWatchdogOn()
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "jjflex-focus-watchdog-legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // Exactly what an older build wrote: the two settings that
                // existed then, and nothing else.
                File.WriteAllText(Path.Combine(dir, "tester_accessibilityConfig.xml"),
                    "<?xml version=\"1.0\"?>\r\n"
                    + "<AccessibilityConfig>\r\n"
                    + "  <DoubleTapTolerance>Relaxed</DoubleTapTolerance>\r\n"
                    + "  <SliceArrowOrder>BottomToTop</SliceArrowOrder>\r\n"
                    + "</AccessibilityConfig>\r\n");

                var loaded = AccessibilityConfig.Load(dir, "tester");

                // Positive control: the file really did load, so the assertion
                // below is about the absent element and not about a failed
                // read falling back to a fresh default object.
                Assert.Equal(DoubleTapTolerance.Relaxed, loaded.DoubleTapTolerance);
                Assert.Equal(SliceArrowOrder.BottomToTop, loaded.SliceArrowOrder);

                Assert.True(loaded.ReclaimStolenForeground);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
                AccessibilityConfig.Load(dir, "tester-absent");
            }
        }

        /// <summary>
        /// The dialog must run its verdict through the preference before acting
        /// on it. A refactor that calls Decide and switches on the result
        /// directly compiles, passes every sentinel test, and leaves the switch
        /// in Settings doing nothing at all — which is the promise to Don
        /// broken in the one way nobody would notice.
        /// </summary>
        [Fact]
        public void TheDialogRoutesItsVerdictThroughThePreference()
        {
            string source = Read("JJFlexWpf/JJFlexDialog.cs");

            // Positive control: this really is the file with the sentinel in
            // it, so the containment checks below are not passing on an empty
            // or wrongly-resolved read.
            Assert.Contains("StrandedFocusSentinel", source, StringComparison.Ordinal);
            Assert.Contains("ReclaimFromForeignThief", source, StringComparison.Ordinal);

            Assert.Contains("ReclaimStolenForeground", source, StringComparison.Ordinal);
            Assert.Contains("WithOperatorPreference", source, StringComparison.Ordinal);
            Assert.Contains("ReclaimSuppressedByPreference", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// Off must not mean invisible. A suppressed reclaim is traced, so a
        /// diagnostic bundle taken after the outage can say "this would have
        /// rescued you and you had it switched off" instead of showing a silent
        /// gap with no watchdog line in it at all.
        /// </summary>
        [Fact]
        public void ASuppressedReclaimIsWrittenDown()
        {
            string source = Read("JJFlexWpf/JJFlexDialog.cs");

            int at = source.IndexOf("ReportSuppressedReclaim(nint fg", StringComparison.Ordinal);
            Assert.True(at >= 0,
                "JJFlexDialog no longer has a ReportSuppressedReclaim method. A test that "
                + "cannot find its subject passes every absence check it makes.");

            string body = source.Substring(at, Math.Min(2_200, source.Length - at));
            Assert.Contains("TraceLevel.Info", body, StringComparison.Ordinal);
            Assert.Contains("NoteTheft", body, StringComparison.Ordinal);
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
