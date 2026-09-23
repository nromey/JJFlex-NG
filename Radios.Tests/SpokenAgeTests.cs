using System;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The age phrases that are read aloud beside a meter value. Pinned after
    /// Noel heard "1 seconds ago" from the alarm dialog on 2026-09-22 — the
    /// helper had been in use on every meter surface and nobody had read the
    /// assembled sentence at exactly one.
    /// </summary>
    public class SpokenAgeTests
    {
        [Theory]
        [InlineData(0.5, "under a second")]
        [InlineData(1, "1 second")]
        [InlineData(2, "2 seconds")]
        [InlineData(45, "45 seconds")]
        [InlineData(89, "89 seconds")]
        [InlineData(90, "1 minute")]
        [InlineData(119, "1 minute")]
        [InlineData(120, "2 minutes")]
        [InlineData(89 * 60, "89 minutes")]
        [InlineData(90 * 60, "1 hour")]
        [InlineData(3 * 3600, "3 hours")]
        public void MeterAge_IsSingularAtOne(double seconds, string expected)
        {
            Assert.Equal(expected, MeterInventory.DescribeAge(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void CaptureDuration_NeverSaysAboutUnderAMinute()
        {
            // The frame no longer carries "about"; the minutes phrases do.
            // So a short capture assembles to "..., under a minute." and a
            // longer one to "..., about 3 minutes." — never "about under".
            var frame = Lexicon.Get("logging.capture.saved",
                ("started", "9:14 PM"), ("duration", Lexicon.Get("logging.time.under_a_minute")));
            Assert.Equal("Capture saved: 9:14 PM, under a minute.", frame);
            Assert.DoesNotContain("about under", frame);

            var one = Lexicon.Get("logging.time.minutes_one", ("minutes", 1));
            var many = Lexicon.Get("logging.time.minutes_many", ("minutes", 3));
            Assert.Equal("about 1 minute", one);
            Assert.Equal("about 3 minutes", many);
        }
    }
}
