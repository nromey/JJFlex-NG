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
        public void A_positive_margin_too_small_for_the_meters_precision_is_refused_as_a_clear_line_equal_to_the_trigger()
        {
            // Astra's Track IJK review, blocker 7. Validation compared the two
            // boundaries in double, judgement compares them in float: a margin
            // of 0.0000001 at 60 passed as distinct and judged as equal, which
            // is the zero-margin chatter under a margin the editor accepted.
            // The question is asked at the precision the answer is given in.
            var p = (Good() with { Hysteresis = 0.0000001 }).Validate().Single();
            Assert.Equal("hysteresis", p.Field);
            Assert.Equal("alarms.validation.clear_equals_trigger", p.LexiconKey);

            // A delta alarm's clear line is threshold minus margin too.
            var delta = AlarmDefinition.NewRiseFromBaseline("d", "Rise", "0000", MeterSelector.From(AlarmMonitorTests.PaTemp), 5, 0.0000001);
            Assert.Equal("alarms.validation.clear_equals_trigger", delta.Validate().Single().LexiconKey);

            // The positive controls: a margin a float can hold is accepted, and
            // the shipped presets' margins are all of that kind.
            Assert.Empty((Good() with { Hysteresis = 0.01 }).Validate());
            Assert.Empty((Good() with { Threshold = 12, Direction = AlarmDirection.AtOrBelow, Hysteresis = 0.2 }).Validate());
        }

        [Fact]
        public void A_margin_below_the_meters_precision_cannot_make_one_reading_both_on_the_alarm_side_and_clear()
        {
            var above = Good() with { Hysteresis = 0.0000001 };
            Assert.True(above.IsOnAlarmSide(60));
            Assert.False(above.IsBeyondClear(60));        // the pair Astra named: both were true
            Assert.True(above.IsBeyondClear(59.99));       // strictly past the line clears

            var below = Good() with { Direction = AlarmDirection.AtOrBelow, Threshold = 12, Hysteresis = 0.0000001 };
            Assert.True(below.IsOnAlarmSide(12));
            Assert.False(below.IsBeyondClear(12));
            Assert.True(below.IsBeyondClear(12.01));

            var delta = AlarmDefinition.NewRiseFromBaseline("d", "Rise", "0000", MeterSelector.From(AlarmMonitorTests.PaTemp), 5, 0.0000001);
            Assert.False(delta.IsChangeBeyondClear(5));
            Assert.True(delta.IsChangeBeyondClear(4.99));

            // Positive control: a representable margin keeps the inclusive clear.
            var banded = Good() with { Hysteresis = 0.5 };
            Assert.True(banded.IsOnAlarmSide(60));
            Assert.False(banded.IsBeyondClear(60));
            Assert.True(banded.IsBeyondClear(59.5));
        }

        [Fact]
        public void The_rising_fast_preset_ships_disabled()
        {
            Assert.False(AlarmDefinition.NewRisingFast("t", "Rising", "0000", MeterSelector.From(AlarmMonitorTests.PaTemp)).Enabled);
        }
    }
}
