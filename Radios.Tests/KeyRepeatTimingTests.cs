using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The operator's own keyboard auto-repeat settings, converted from the
    /// Windows setting indexes to milliseconds.
    /// </summary>
    /// <remarks>
    /// This matters because #216's repair reads the repeat delay off the
    /// machine instead of hardcoding a number measured on one desk. If the
    /// conversion is wrong, the bridge that keeps a held PTT keyed is sized
    /// wrong on every machine at once — and it is wrong silently, which is the
    /// failure this project keeps paying for. The conversions are pure so they
    /// can be checked without a keyboard.
    /// </remarks>
    public class KeyRepeatTimingTests
    {
        [Fact]
        public void The_four_delay_settings_map_to_the_documented_quarter_seconds()
        {
            Assert.Equal(250, KeyRepeatTiming.DelayMsFromSetting(0));
            Assert.Equal(500, KeyRepeatTiming.DelayMsFromSetting(1));
            Assert.Equal(750, KeyRepeatTiming.DelayMsFromSetting(2));
            Assert.Equal(1000, KeyRepeatTiming.DelayMsFromSetting(3));
        }

        [Fact]
        public void The_windows_default_setting_is_the_fallback_we_use_when_it_cannot_be_read()
        {
            // Not a coincidence worth losing: the 2026-08-24 key-probe
            // measurements were taken at this setting, which is why the first
            // synthetic pair landed near 512 ms there.
            Assert.Equal(KeyRepeatTiming.DefaultDelayMs, KeyRepeatTiming.DelayMsFromSetting(1));
        }

        [Fact]
        public void An_out_of_range_setting_is_clamped_rather_than_producing_nonsense()
        {
            Assert.Equal(250, KeyRepeatTiming.DelayMsFromSetting(-4));
            Assert.Equal(1000, KeyRepeatTiming.DelayMsFromSetting(99));
            Assert.Equal(400, KeyRepeatTiming.RepeatPeriodMsFromSetting(-1));
            Assert.Equal(33, KeyRepeatTiming.RepeatPeriodMsFromSetting(99));
        }

        [Fact]
        public void The_repeat_period_runs_from_slow_to_fast_across_the_range()
        {
            Assert.Equal(400, KeyRepeatTiming.RepeatPeriodMsFromSetting(0));
            Assert.Equal(33, KeyRepeatTiming.RepeatPeriodMsFromSetting(31));

            int previous = int.MaxValue;
            for (int s = 0; s <= 31; s++)
            {
                int period = KeyRepeatTiming.RepeatPeriodMsFromSetting(s);
                Assert.True(period <= previous, $"setting {s} should not be slower than {s - 1}");
                previous = period;
            }
        }

        [Fact]
        public void Reading_the_machine_never_throws_and_never_returns_something_unusable()
        {
            // The point is the fallback: a hold watchdog has to work on a
            // machine that will not answer.
            int delay = KeyRepeatTiming.DelayMs();
            Assert.InRange(delay, 250, 1000);

            int period = KeyRepeatTiming.RepeatPeriodMs();
            Assert.InRange(period, 33, 400);
        }
    }
}
