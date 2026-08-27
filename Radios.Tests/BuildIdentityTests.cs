using System;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The build facts the Ctrl+J, Alt+V chord speaks (#269), and the About
    /// page shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both read <c>DiagnosticSnapshot.BuildStamp</c>, which is the point: #269
    /// says "read from that — do not build a second version-reporting path",
    /// because a second assembler is how About and the spoken answer end up
    /// disagreeing about what is running.
    /// </para>
    /// <para>
    /// The values themselves cannot be asserted here — this runs inside a test
    /// host, so the entry assembly is the runner, not jjflexible.exe. What CAN
    /// be pinned is the shape: that both date forms come off one instant, that
    /// nothing throws in a tree with no manifest, and that the probe is cached.
    /// Pressing the key on a real build is what checks the values, per the
    /// standing rule.
    /// </para>
    /// </remarks>
    public class BuildIdentityTests
    {
        [Fact]
        public void Both_date_forms_come_off_one_instant()
        {
            // The written form is for a page you read; the spoken form is for a
            // tester saying it down a phone. One DateTime feeds both so they
            // cannot drift into naming different days.
            var id = new DiagnosticSnapshot.BuildIdentity(
                "4.1.16.1024", new DateTime(2026, 8, 27, 21, 40, 0), "Debug", "abc1234");

            Assert.Equal("2026-08-27", id.Date);
            Assert.Equal("August 27, 2026", id.DateSpoken);
        }

        [Fact]
        public void The_spoken_date_never_leads_with_digits_that_could_be_a_version()
        {
            // A hyphenated ISO date read aloud beside a 4-part version is two
            // number strings in one sentence, and the listener has to work out
            // which is which. The month by name separates them by ear.
            var id = new DiagnosticSnapshot.BuildIdentity(
                "4.1.16.1024", new DateTime(2026, 1, 5), "Release", null);

            Assert.Equal("January 5, 2026", id.DateSpoken);
        }

        [Fact]
        public void An_unknown_build_time_leaves_both_date_forms_null_rather_than_guessing()
        {
            // The handler drops to the no-date sentence on null. A fabricated
            // date would be worse than no date: it is the field a tester quotes.
            var id = new DiagnosticSnapshot.BuildIdentity("4.1.16.1024", null, "Debug", null);

            Assert.Null(id.Date);
            Assert.Null(id.DateSpoken);
            Assert.Equal("4.1.16.1024", id.Version);
        }

        [Fact]
        public void The_real_probe_answers_without_throwing_and_is_cached()
        {
            // A guarded probe that throws on a tree with no install-manifest
            // would take out the keypress. This tree is exactly that case.
            var first = DiagnosticSnapshot.BuildStamp;
            var second = DiagnosticSnapshot.BuildStamp;

            Assert.NotNull(first);
            Assert.Same(first, second);
        }

        [Fact]
        public void The_configuration_probe_commits_to_an_answer()
        {
            // Debug or Release — never null and never a third thing. The chord
            // exists so a tester can say which build they are on, and "unknown"
            // is the one answer that helps nobody.
            string configuration = DiagnosticSnapshot.BuildStamp.Configuration;

            Assert.True(configuration == "Debug" || configuration == "Release",
                "expected Debug or Release, got: " + (configuration ?? "null"));
        }
    }
}
