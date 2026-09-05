using System;
using System.Threading;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The duck's timing ladder (#535): the shipped step is the shipped timing,
/// no step can defeat the duck's purpose or outlast its bound, and changing
/// the step never touches a deadline already written.
/// </summary>
/// <remarks>
/// <para>
/// Windowless. <see cref="RxDuck"/> is arithmetic and a clock; nothing here
/// needs a dispatcher, a window or a desktop, so these run even when
/// <c>DeskGuard</c> refuses the rest of the tier — and they must stay that way,
/// because a duck test that needed a window would never run on a live desk.
/// </para>
/// <para>
/// <see cref="RxDuck"/> is process-wide static state. Every test that touches
/// it restores what it found in a <c>finally</c>, and the assembly runs its
/// tests serially (see <c>AssemblyInfo.cs</c>), so nothing else observes the
/// state mid-test. The deadline itself cannot be reset — that is the design —
/// so tests that write one keep it short and the expiry test waits it out.
/// </para>
/// </remarks>
public sealed class RxDuckTimingTests
{
    /// <summary>
    /// The shortest first note of any warning earcon: Problem recorded opens
    /// with 440 Hz for 90 ms (<c>EarconPlayer.ProblemRecordedTone</c>). An
    /// attack longer than this reaches depth after the note it was meant to
    /// make room for.
    /// </summary>
    private const int ShortestWarningNoteMs = 90;

    private static readonly RxDuckTimingPreset[] Ladder =
    {
        RxDuckTimingPreset.Quick,
        RxDuckTimingPreset.Gentle,
        RxDuckTimingPreset.Lingering,
    };

    [Fact]
    public void QuickAtTheShippedDepthIsTheShippedTiming()
    {
        // 25 / 60 / 90 were the constants before #535. Quick is the control
        // arm of the hypothesis that timing, not depth, made the dip
        // distracting, and a control arm that drifted would prove nothing.
        Assert.Equal(25, RxDuck.AttackMsFor(RxDuckTimingPreset.Quick));
        Assert.Equal(60, RxDuck.HoldMsFor(RxDuckTimingPreset.Quick));
        Assert.Equal(90, RxDuck.ReleaseMsFor(RxDuckTimingPreset.Quick, RxDuck.DefaultDepthDb));
        Assert.Equal(RxDuckTimingPreset.Quick, RxDuck.DefaultTiming);
    }

    [Fact]
    public void EveryStepLandsInsideTheShortestWarningNote()
    {
        foreach (var step in Ladder)
        {
            int attack = RxDuck.AttackMsFor(step);
            Assert.True(attack > 0, step + " has no attack at all, which is a click.");
            Assert.True(attack <= ShortestWarningNoteMs,
                step + " attacks in " + attack + " ms, longer than the " + ShortestWarningNoteMs
                + " ms first note of the shortest warning. The duck would reach depth after the "
                + "sound it exists to make room for.");
        }
    }

    [Fact]
    public void ReleaseNeverBeatsTheAttackAndNeverExceedsTheCap()
    {
        foreach (var step in Ladder)
        {
            int attack = RxDuck.AttackMsFor(step);
            for (float depth = 0f; depth <= RxDuck.MaxDepthDb; depth += 0.5f)
            {
                int release = RxDuck.ReleaseMsFor(step, depth);
                Assert.True(release >= attack,
                    step + " at " + depth + " dB releases in " + release + " ms, faster than its "
                    + attack + " ms attack — a duck that lets go faster than it grabbed sounds "
                    + "like a fault.");
                Assert.True(release <= RxDuck.MaxReleaseMs,
                    step + " at " + depth + " dB releases in " + release + " ms, past the "
                    + RxDuck.MaxReleaseMs + " ms bound the operator was promised.");
            }
        }
    }

    [Fact]
    public void ReleaseGrowsWithDepthSoTheRateStaysConstant()
    {
        foreach (var step in Ladder)
        {
            int previous = 0;
            for (int depth = 0; depth <= (int)RxDuck.MaxDepthDb; depth++)
            {
                int release = RxDuck.ReleaseMsFor(step, depth);
                Assert.True(release >= previous,
                    step + " releases in " + release + " ms at " + depth + " dB but " + previous
                    + " ms at " + (depth - 1) + " dB. A deeper dip must come back over a longer "
                    + "time, or the same setting gets steeper as the depth goes up.");
                previous = release;
            }
        }

        // And not merely non-decreasing: at the shipped depth versus the
        // deepest, every step genuinely lengthens.
        foreach (var step in Ladder)
            Assert.True(
                RxDuck.ReleaseMsFor(step, RxDuck.MaxDepthDb)
                    > RxDuck.ReleaseMsFor(step, RxDuck.DefaultDepthDb),
                step + " releases no more slowly at " + RxDuck.MaxDepthDb + " dB than at "
                + RxDuck.DefaultDepthDb + " dB.");
    }

    [Fact]
    public void TheLadderIsOrderedOnEveryAxis()
    {
        for (int i = 1; i < Ladder.Length; i++)
        {
            var lower = Ladder[i - 1];
            var upper = Ladder[i];
            Assert.True(RxDuck.AttackMsFor(upper) > RxDuck.AttackMsFor(lower),
                upper + " does not ease in more slowly than " + lower + ".");
            Assert.True(RxDuck.HoldMsFor(upper) > RxDuck.HoldMsFor(lower),
                upper + " does not hang longer than " + lower + ".");
            Assert.True(
                RxDuck.ReleaseMsFor(upper, RxDuck.DefaultDepthDb)
                    > RxDuck.ReleaseMsFor(lower, RxDuck.DefaultDepthDb),
                upper + " does not come back more slowly than " + lower + " at the shipped depth.");
        }
    }

    [Fact]
    public void ChangingTheStepIsLiveAndLeavesTheDeadlineAlone()
    {
        var saved = Saved.Capture();
        try
        {
            RxDuck.Enabled = true;
            RxDuck.DepthDb = RxDuck.DefaultDepthDb;
            RxDuck.Timing = RxDuckTimingPreset.Quick;

            // A short request: Quick's hold is 60 ms, so this deadline is
            // about 110 ms out and gone long before the expiry test below.
            RxDuck.RequestFor(50);
            Assert.True(RxDuck.TargetGain < 1f, "A live request did not duck.");

            RxDuck.Timing = RxDuckTimingPreset.Lingering;

            // Live: the audio thread's next buffer sees the new attack and
            // release. Untouched: the request made under Quick still stands.
            Assert.Equal(RxDuck.AttackMsFor(RxDuckTimingPreset.Lingering), RxDuck.AttackMs);
            Assert.Equal(
                RxDuck.ReleaseMsFor(RxDuckTimingPreset.Lingering, RxDuck.DefaultDepthDb),
                RxDuck.ReleaseMs);
            Assert.True(RxDuck.TargetGain < 1f,
                "Changing the timing step ended a duck that was in progress. The deadline is "
                + "the only thing that ends a duck, and the timing must not touch it.");
        }
        finally
        {
            saved.Restore();
        }
    }

    [Fact]
    public void TheDuckExpiresOnItsOwn()
    {
        var saved = Saved.Capture();
        try
        {
            RxDuck.Enabled = true;
            RxDuck.DepthDb = RxDuck.DefaultDepthDb;
            RxDuck.Timing = RxDuckTimingPreset.Quick;

            RxDuck.RequestFor(1);
            Assert.True(RxDuck.TargetGain < 1f, "A live request did not duck.");

            // 1 ms of earcon plus Quick's 60 ms hold. Any deadline left by the
            // test above is at most about 110 ms out. Wait well past both.
            Thread.Sleep(400);

            Assert.Equal(1f, RxDuck.TargetGain);
        }
        finally
        {
            saved.Restore();
        }
    }

    [Fact]
    public void ParseIsLenientAndRoundTrips()
    {
        Assert.Equal(RxDuckTimingPreset.Gentle, RxDuck.ParseTiming("gentle"));
        Assert.Equal(RxDuckTimingPreset.Lingering, RxDuck.ParseTiming("  Lingering "));
        Assert.Equal(RxDuckTimingPreset.Quick, RxDuck.ParseTiming("QUICK"));

        // The cases a downgrade or a hand edit produce, none of which may
        // throw or cost the operator the rest of their audio config.
        Assert.Equal(RxDuck.DefaultTiming, RxDuck.ParseTiming(null));
        Assert.Equal(RxDuck.DefaultTiming, RxDuck.ParseTiming(""));
        Assert.Equal(RxDuck.DefaultTiming, RxDuck.ParseTiming("Brisk"));
        Assert.Equal(RxDuck.DefaultTiming, RxDuck.ParseTiming("7"));

        foreach (var step in Ladder)
            Assert.Equal(step, RxDuck.ParseTiming(RxDuck.TimingName(step)));
    }

    [Fact]
    public void AnUndefinedStepFallsBackRatherThanIndexingOffTheLadder()
    {
        var saved = Saved.Capture();
        try
        {
            RxDuck.Timing = (RxDuckTimingPreset)42;
            Assert.Equal(RxDuck.DefaultTiming, RxDuck.Timing);
            Assert.Equal(RxDuck.AttackMsFor(RxDuck.DefaultTiming), RxDuck.AttackMs);

            // The per-step lookups take the same care, since a persisted or
            // computed value can reach them without going through the setter.
            Assert.Equal(RxDuck.AttackMsFor(RxDuck.DefaultTiming), RxDuck.AttackMsFor((RxDuckTimingPreset)(-1)));
            Assert.Equal(RxDuck.TimingName(RxDuck.DefaultTiming), RxDuck.TimingName((RxDuckTimingPreset)99));
        }
        finally
        {
            saved.Restore();
        }
    }

    private readonly struct Saved
    {
        private readonly bool _enabled;
        private readonly float _depthDb;
        private readonly RxDuckTimingPreset _timing;

        private Saved(bool enabled, float depthDb, RxDuckTimingPreset timing)
        {
            _enabled = enabled;
            _depthDb = depthDb;
            _timing = timing;
        }

        public static Saved Capture() => new(RxDuck.Enabled, RxDuck.DepthDb, RxDuck.Timing);

        public void Restore()
        {
            RxDuck.Enabled = _enabled;
            RxDuck.DepthDb = _depthDb;
            RxDuck.Timing = _timing;
        }
    }
}
