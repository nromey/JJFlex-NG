using Xunit;

namespace Radios.Tests;

/// <summary>
/// A push-to-talk key held when the connection fell does not key the radio
/// when it comes back; a real release and a later press do (#596). Replays
/// both readers' streams against the pure guard on a host clock.
/// </summary>
/// <remarks>
/// The streams are the measured ones from <see cref="PttHoldFilter"/>'s
/// remarks: a reader that passes holds through sends Windows' auto-repeat
/// downs about every 30 ms and one up; JAWS sends down/up PAIRS, the up a few
/// milliseconds after its down, the first pair at the Windows repeat delay
/// (about 512 ms) and the rest about 250 ms apart.
/// </remarks>
public sealed class PttHeldThroughDropTests
{
    private const int RepeatDelayMs = 512;

    /// <summary>The gap the host arms with: the hold filter's first-gap window on this repeat delay.</summary>
    private static int Gap()
    {
        var filter = new PttHoldFilter();
        filter.SetKeyRepeatDelay(RepeatDelayMs);
        return filter.FirstGapDeferMs;
    }

    [Fact]
    public void Unarmed_it_swallows_nothing()
    {
        var guard = new PttHeldThroughDrop();
        Assert.False(guard.SwallowDown(1_000));
        Assert.False(guard.NoteUp(1_100));
        Assert.False(guard.SwallowDown(1_200));
    }

    [Fact]
    public void A_held_key_auto_repeating_across_the_drop_and_the_reconnect_keys_nothing()
    {
        var guard = new PttHeldThroughDrop();
        guard.Arm(Gap());   // the connection falls at t=0 with the key down

        // Thirty seconds of repeats: the radio gone, then back, key still held.
        for (long t = 30; t <= 30_000; t += 30)
            Assert.True(guard.SwallowDown(t), "a repeat at " + t + " ms keyed the radio");
        Assert.True(guard.Armed);

        // Let go, wait, press: that is a press.
        guard.NoteUp(30_010);
        Assert.False(guard.SwallowDown(32_000), "a real press after the release was swallowed");
        Assert.False(guard.Armed);
    }

    [Fact]
    public void A_held_key_under_jaws_across_the_drop_and_the_reconnect_keys_nothing()
    {
        var guard = new PttHeldThroughDrop();
        guard.Arm(Gap());

        // JAWS: first pair at the repeat delay, then a pair every 250 ms, each
        // up five milliseconds after its down. Every down is the same hold.
        long t = RepeatDelayMs;
        Assert.True(guard.SwallowDown(t));
        guard.NoteUp(t + 5);
        for (int pair = 0; pair < 120; pair++)
        {
            t += 250;
            Assert.True(guard.SwallowDown(t), "a synthesised down at " + t + " ms keyed the radio");
            guard.NoteUp(t + 5);
        }

        // The operator lets go: no down follows the last up. The next real
        // press comes well after the gap.
        Assert.False(guard.SwallowDown(t + 5 + Gap() + 200), "a real press after the release was swallowed");
    }

    [Fact]
    public void The_first_jaws_gap_is_inside_the_window()
    {
        // The longest gap inside one JAWS hold is the first, from the first
        // synthesised up to the next down. The window it arms with must cover
        // it, or the second down of every held press would key.
        Assert.True(Gap() > RepeatDelayMs, "the first-gap window " + Gap() + " ms does not cover a " + RepeatDelayMs + " ms repeat delay");
    }

    [Fact]
    public void A_release_and_repress_faster_than_the_gap_is_swallowed_and_the_next_one_keys()
    {
        var guard = new PttHeldThroughDrop();
        guard.Arm(Gap());
        guard.NoteUp(100);
        Assert.True(guard.SwallowDown(300), "the safe side of the ambiguity: too fast to tell from a reader's pair");
        guard.NoteUp(400);
        Assert.False(guard.SwallowDown(400 + Gap() + 1));
    }
}
