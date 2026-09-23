using System.Linq;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>Design section 4's validation list, one rule per test, each naming the field it lands on.</summary>
    public sealed class AlarmDefinitionValidationTests
    {
        private static AlarmDefinition Good() =>
            AlarmDefinition.NewLevel("id", "PA temperature", "0000", MeterSelector.From(AlarmMonitorTests.PaTemp),
                AlarmDirection.AtOrAbove, 60, 2);

        [Fact]
        public void A_sound_definition_has_no_problems()
        {
            Assert.Empty(Good().Validate());
        }

        [Fact]
        public void A_non_finite_threshold_lands_on_the_threshold_field()
        {
            var p = (Good() with { Threshold = double.NaN }).Validate().Single();
            Assert.Equal("threshold", p.Field);
            Assert.Equal("alarms.validation.threshold_not_finite", p.LexiconKey);
        }

        [Fact]
        public void Negative_hysteresis_is_refused()
        {
            var p = (Good() with { Hysteresis = -1 }).Validate().Single();
            Assert.Equal("hysteresis", p.Field);
        }

        [Fact]
        public void Zero_or_negative_freshness_is_refused()
        {
            Assert.Equal("freshness", (Good() with { FreshnessAllowanceSeconds = 0 }).Validate().Single().Field);
            Assert.Equal("freshness", (Good() with { FreshnessAllowanceSeconds = -5 }).Validate().Single().Field);
        }

        [Fact]
        public void An_invalid_trend_interval_is_refused()
        {
            var trend = AlarmDefinition.NewRisingFast("t", "Rising", "0000", MeterSelector.From(AlarmMonitorTests.PaTemp));
            Assert.Empty(trend.Validate());
            var bad = trend with { TrendIntervalMinSeconds = 96 };
            Assert.Equal("trend", bad.Validate().Single().Field);
        }

        [Fact]
        public void A_name_is_required_and_bounded_as_input()
        {
            Assert.Equal("name", (Good() with { Name = "  " }).Validate().Single().Field);
            Assert.Equal("name", (Good() with { Name = new string('x', AlarmDefinition.MaxNameLength + 1) }).Validate().Single().Field);
        }

        [Fact]
        public void An_unusual_threshold_outside_the_published_range_is_permitted()
        {
            // May be intentional; the editor shows a review message instead.
            Assert.Empty((Good() with { Threshold = 150 }).Validate());
        }

        [Fact]
        public void The_inclusive_boundaries_and_clear_lines_are_arithmetic_not_opinion()
        {
            var above = Good();
            Assert.True(above.IsOnAlarmSide(60));
            Assert.False(above.IsOnAlarmSide(59.999));
            Assert.Equal(58, above.ClearBoundary);
            Assert.True(above.IsBeyondClear(58));
            Assert.False(above.IsBeyondClear(58.001));

            var below = above with { Direction = AlarmDirection.AtOrBelow, Threshold = 12, Hysteresis = 0.2 };
            Assert.True(below.IsOnAlarmSide(12));
            Assert.False(below.IsOnAlarmSide(12.001));
            Assert.Equal(12.2, below.ClearBoundary, 9);
            Assert.True(below.IsBeyondClear(12.2));
        }

        [Fact]
        public void The_rising_fast_preset_ships_disabled()
        {
            Assert.False(AlarmDefinition.NewRisingFast("t", "Rising", "0000", MeterSelector.From(AlarmMonitorTests.PaTemp)).Enabled);
        }
    }
}
