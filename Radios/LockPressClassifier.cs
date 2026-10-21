using System;

namespace Radios;

/// <summary>
/// Decides, for the transmit lock (Shift+Space), whether a key-down is the
/// operator's press or a screen reader's synthesised re-down of a key that
/// never came up. One instance serves every door into the lock: Home's
/// step 3, the dialog door, and the system-wide chord (#778c repair round
/// two, Noel 2026-10-21 09:51: "simplify yes").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the push-to-talk filter.</b> The lock is a TOGGLE. Push to talk
/// can afford to guess that a release was synthetic and wait, because the
/// cost of a wrong guess is a fraction of a second of carrier. A toggle that
/// guesses swallows a deliberate press: the first repair absorbed a 120 ms
/// Shift+Space that arrived 133 ms after a synthesised pair, and the
/// transmitter stayed locked with nothing said. This class never guesses
/// about what comes next. It classifies each press by its OWN measured
/// duration.
/// </para>
/// <para>
/// <b>The two delivery shapes.</b> NVDA and plain Windows deliver a held key
/// as one down, OS repeats carrying the repeat flag, and one up; the callers
/// drop the repeats, so this class sees one down and one up. JAWS delivers a
/// held key as down/up PAIRS: the first at the Windows repeat delay
/// (measured 2026-08-24, about 512 ms), the rest about 250 ms apart
/// (242 to 272), each up 3 to 5 ms after its down; the first physical down
/// has been seen both with no up until the first pair's (517 ms) and with an
/// up 3 ms after it. See <c>Radios.Tests.PttHoldFilterTests</c> and
/// <c>memory/project_jaws_does_not_deliver_held_keys.md</c>.
/// </para>
/// <para>
/// <b>The rule.</b>
/// A down that arrives after a quiet gap, longer than <see cref="PairWindowMs"/>
/// since the last up, is the ordinary press: it toggles AT ONCE, so the
/// everyday press keeps its feel. A down that arrives inside that window of
/// the previous up is decided at ITS up: a press shorter than
/// <see cref="SyntheticPressMs"/> is a synthesised pair and is ignored; a
/// press of human length toggles. A down while the key is still considered
/// down, inside the window of the previous down, is a re-down with no release
/// between and is ignored, the same thing the repeat flag means. So a held key
/// under JAWS toggles once at its first down; under NVDA once; two real taps
/// toggle twice; and a deliberate tap straight after a synthesised stream
/// toggles at its up. No timer, no deferral, no learning.
/// </para>
/// <para>
/// <b>What a pending press costs.</b> A press decided at its up toggles a
/// human press-length late, under 200 ms, and only when it follows another
/// press within the window. A pending press whose up never arrives does not
/// toggle and nothing is said: that needs the key to go down inside the
/// window and the release to land outside every window of ours, which is rare
/// and is reported as such.
/// </para>
/// <para>
/// <b>The window.</b> Sized from this machine's Windows repeat delay, the
/// same way <see cref="PttHoldFilter.FirstGapDeferMs"/> is: the first
/// synthesised pair lands at the repeat delay, so the window must outlast it,
/// and the 250 ms stream is well inside. Not a constant measured on one desk.
/// </para>
/// <para>
/// <b>A door with no key-up.</b> The system-wide chord posts one toggle per
/// press and swallows its own release, so MainWindow never sees an up from
/// it. Such a down cannot be decided later: <see cref="NoteDownWithoutUp"/>
/// toggles it at once and leaves nothing held or pending. Pairs from a
/// synthesising reader on THAT chord are the hook's own business.
/// </para>
/// <para>
/// Pure logic, host-clocked, no UI types, so the measured streams replay in
/// Radios.Tests.
/// </para>
/// </remarks>
public sealed class LockPressClassifier
{
    /// <summary>What the host should do with a key-down.</summary>
    public enum DownAction
    {
        /// <summary>The ordinary press: toggle the lock now.</summary>
        Toggle,
        /// <summary>Too soon after the last up to tell; its up will decide.</summary>
        Pending,
        /// <summary>A re-down of a key that never came up. Nothing to do.</summary>
        Ignore,
    }

    /// <summary>What the host should do with a key-up.</summary>
    public enum UpAction
    {
        /// <summary>Nothing: the press already toggled at its down, was synthesised, or was never ours.</summary>
        Nothing,
        /// <summary>A pending press of human length: toggle the lock now.</summary>
        Toggle,
    }

    /// <summary>
    /// A press shorter than this was not a hand on a key. The same number as
    /// <see cref="PttHoldFilter.ImplausibleReleaseMs"/>: a synthesised pair's
    /// up follows its down by single milliseconds, a human tap runs 80 ms or
    /// more.
    /// </summary>
    public const int SyntheticPressMs = PttHoldFilter.ImplausibleReleaseMs;

    /// <summary>
    /// A down this long after the last up, or after the last down with no up
    /// between, is a new press. Repeat delay plus half again, bounded like the
    /// push-to-talk filter's first-gap window.
    /// </summary>
    public int PairWindowMs { get; private set; } =
        Clamp(KeyRepeatTiming.DefaultDelayMs * 3 / 2, PttHoldFilter.DefaultDeferMs, PttHoldFilter.MaxFirstGapDeferMs);

    /// <summary>The operator's Windows repeat delay, as told to us by the host.</summary>
    public int KeyRepeatDelayMs { get; private set; } = KeyRepeatTiming.DefaultDelayMs;

    /// <summary>True while a press is undecided, waiting for its up.</summary>
    public bool PressPending => _down && _pending;

    private bool _down;
    private bool _pending;
    private long _downMs = long.MinValue;
    private long _lastUpMs = long.MinValue;

    /// <summary>
    /// Tell the classifier this machine's keyboard repeat delay. Called once
    /// from the host; a test passes it directly.
    /// </summary>
    public void SetKeyRepeatDelay(int delayMs)
    {
        if (delayMs <= 0) return;
        KeyRepeatDelayMs = delayMs;
        PairWindowMs = Clamp(delayMs * 3 / 2, PttHoldFilter.DefaultDeferMs, PttHoldFilter.MaxFirstGapDeferMs);
    }

    /// <summary>The lock chord's key-down arrived, repeats already removed by the caller.</summary>
    public DownAction NoteDown(long nowMs)
    {
        if (_down && nowMs - _downMs < PairWindowMs)
        {
            // A down with no up since the last down, inside the window: the
            // reader re-sent a key that is still held. Outside the window the
            // previous press's up was lost to a window we do not see, and
            // this is a fresh press rather than a key stuck for ever.
            return DownAction.Ignore;
        }

        _down = true;
        _downMs = nowMs;
        if (_lastUpMs != long.MinValue && nowMs - _lastUpMs < PairWindowMs)
        {
            _pending = true;
            return DownAction.Pending;
        }

        _pending = false;
        return DownAction.Toggle;
    }

    /// <summary>
    /// A key-down from a door that will never deliver the up. Decided now: it
    /// toggles, and drops any press this class was still waiting on.
    /// </summary>
    public DownAction NoteDownWithoutUp(long nowMs)
    {
        _down = false;
        _pending = false;
        _downMs = nowMs;
        return DownAction.Toggle;
    }

    /// <summary>
    /// A Space key-up arrived, whatever the modifiers say by now. Every Space
    /// up on every door comes here, including push to talk's, so an up for a
    /// down this class never saw is nothing.
    /// </summary>
    public UpAction NoteUp(long nowMs)
    {
        if (!_down)
        {
            _lastUpMs = nowMs;
            return UpAction.Nothing;
        }

        _down = false;
        _lastUpMs = nowMs;
        bool wasPending = _pending;
        _pending = false;
        if (!wasPending) return UpAction.Nothing;

        long heldMs = nowMs - _downMs;
        return heldMs >= 0 && heldMs < SyntheticPressMs ? UpAction.Nothing : UpAction.Toggle;
    }

    /// <summary>
    /// Escape, or anything else that ends a transmission outside the key
    /// edges: forget the press in flight so its up cannot toggle the lock
    /// back on. The timing memory stays, so a reader still streaming pairs
    /// for a key the operator has not let go of is still inside the window.
    /// </summary>
    public void Reset()
    {
        _down = false;
        _pending = false;
    }

    private static int Clamp(int value, int low, int high) =>
        value < low ? low : (value > high ? high : value);
}
