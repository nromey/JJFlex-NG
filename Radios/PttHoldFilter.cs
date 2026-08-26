using System;

namespace Radios;

/// <summary>
/// Absorbs the synthetic key-release pairs some screen readers substitute for
/// a held key, so a held push-to-talk stays keyed (#216).
/// </summary>
/// <remarks>
/// <para>
/// <b>The fault this exists for.</b> JAWS does not deliver held keys: it
/// synthesises key-down/key-up PAIRS — measured on Noel's machine 2026-08-24
/// with Freight Fate's key probe at roughly 250 ms apart (242-272), the first
/// pair at the Windows repeat delay (~512 ms), each up following its down
/// almost immediately. NVDA passes an unscripted key straight through. A PTT
/// that unkeys on key-up therefore chops a held transmission four times a
/// second under one reader and works perfectly under the other, and the
/// operator — holding the key, talking — has no way to know.
/// </para>
/// <para>
/// <b>Whether JAWS does this for the Ctrl+Space CHORD is still unverified</b>
/// — it may only synthesise for keys its scripts own (the arrows). This class
/// is built so that question does not have to be answered before shipping:
/// it changes NOTHING until it has seen the synthetic signature with its own
/// eyes. The signature is a key-up arriving less than
/// <see cref="ImplausibleReleaseMs"/> after its key-down — no human releases
/// a deliberately held chord in under 50 ms. On a reader that passes holds
/// through, that never happens and every release is passed on immediately,
/// exactly as before.
/// </para>
/// <para>
/// <b>Once the signature has been seen</b>, releases are DEFERRED by
/// <see cref="DeferMs"/> instead of acted on: if another key-down for the
/// chord arrives inside the window, the queue of synthetic taps reads as the
/// single continuous hold it physically is and the transmitter never drops.
/// The spacing is LEARNED from the pairs themselves — the same approach
/// Freight Fate landed in e1a656a5 — never hardcoded to the one measured
/// machine. The stated price, which the task judged worth paying: under a
/// synthesising reader the release reads about a third of a second late.
/// Under NVDA the price is zero, because the filter never arms.
/// </para>
/// <para>
/// The first hold of a synthesising session still takes ONE chop (the first
/// synthetic up measures from the original key-down, so it looks like a
/// plausible half-second press). Everything after it is continuous. Arming is
/// session-sticky: which screen reader is running does not change mid-hold.
/// </para>
/// <para>
/// Pure logic, host-clocked (the caller passes milliseconds), no UI types —
/// lives in Radios so Radios.Tests can replay the measured JAWS stream
/// against it. The WPF wiring in MainWindow owns the actual timer.
/// </para>
/// </remarks>
public sealed class PttHoldFilter
{
    /// <summary>What the host should do with a key-down.</summary>
    public enum DownAction
    {
        /// <summary>A fresh press — key the transmitter.</summary>
        Press,
        /// <summary>
        /// The synthetic re-down of a hold being absorbed. The transmitter
        /// never dropped; cancel the pending deferred release and carry on.
        /// </summary>
        ContinueHold,
    }

    /// <summary>What the host should do with a key-up.</summary>
    public enum UpAction
    {
        /// <summary>A genuine release — unkey now.</summary>
        ReleaseNow,
        /// <summary>
        /// Possibly synthetic. Schedule <see cref="DeferralElapsed"/> after
        /// <see cref="DeferMs"/>; only if no down arrives first is it real.
        /// </summary>
        DeferRelease,
    }

    /// <summary>
    /// Shorter than any human release of a deliberately held chord. Mirrors
    /// PttSafetyController's detection constant; a synthetic pair's up
    /// arrives near zero, a human tap runs 80 ms or more.
    /// </summary>
    public const int ImplausibleReleaseMs = 50;

    /// <summary>
    /// The deferral before any spacing has been learned. Above the measured
    /// 250 ms pair spacing with margin, below anything an operator would
    /// read as a stuck transmitter.
    /// </summary>
    public const int DefaultDeferMs = 350;

    /// <summary>
    /// Hard ceiling however slow a reader's synthesis runs. Also bounds the
    /// extra carrier a deferred release can ever cost.
    /// </summary>
    public const int MaxDeferMs = 750;

    /// <summary>
    /// A down this long after a release is a new press, not synthesis, and
    /// teaches the filter nothing.
    /// </summary>
    private const int RekeyLearnWindowMs = 1000;

    private const int LearnedHistory = 8;

    /// <summary>
    /// True once a synthetic release has been seen this session. Sticky on
    /// purpose — the screen reader does not change under a running app.
    /// </summary>
    public bool SynthesisDetected { get; private set; }

    /// <summary>How many implausibly fast releases have been seen. Zero under NVDA.</summary>
    public int SyntheticReleaseCount { get; private set; }

    /// <summary>
    /// Current deferral: largest learned pair spacing times 1.5 (spacing plus
    /// grace), clamped to [<see cref="DefaultDeferMs"/>, <see cref="MaxDeferMs"/>].
    /// </summary>
    public int DeferMs { get; private set; } = DefaultDeferMs;

    private bool _holding;
    private long _downMs;
    private bool _pendingRelease;
    private long _pendingUpMs;
    private long _lastUpMs = long.MinValue;
    private long _lastReleaseMs = long.MinValue;
    private readonly long[] _gaps = new long[LearnedHistory];
    private int _gapCount;

    /// <summary>The chord's key-down arrived.</summary>
    public DownAction NoteDown(long nowMs)
    {
        if (_pendingRelease)
        {
            // A down while a release was pending: the hold never physically
            // ended. The gap from the up to this down IS the reader's pair
            // spacing — learn it.
            Learn(nowMs - _pendingUpMs);
            _pendingRelease = false;
            _holding = true;
            _downMs = nowMs;
            return DownAction.ContinueHold;
        }

        // A down shortly after an ACTUAL release, on an armed session, means
        // the deferral was too short for this reader's spacing (it released,
        // then the synthetic re-down arrived). Learn the real spacing so the
        // next cycle absorbs; measured from the up event, which is when the
        // reader started its gap.
        if (SynthesisDetected && _lastUpMs != long.MinValue &&
            _lastReleaseMs != long.MinValue &&
            nowMs - _lastReleaseMs < RekeyLearnWindowMs)
        {
            Learn(nowMs - _lastUpMs);
        }

        _holding = true;
        _downMs = nowMs;
        return DownAction.Press;
    }

    /// <summary>The chord's key-up arrived.</summary>
    public UpAction NoteUp(long nowMs)
    {
        _lastUpMs = nowMs;
        if (!_holding)
        {
            _lastReleaseMs = nowMs;
            return UpAction.ReleaseNow;
        }

        long heldMs = nowMs - _downMs;
        if (heldMs >= 0 && heldMs < ImplausibleReleaseMs)
        {
            SyntheticReleaseCount++;
            SynthesisDetected = true;
            _pendingRelease = true;
            _pendingUpMs = nowMs;
            return UpAction.DeferRelease;
        }

        if (SynthesisDetected)
        {
            // Armed: even a plausible-looking up defers, because the FIRST
            // synthetic up of every press measures from the original down and
            // always looks plausible. This is what makes the second and later
            // holds of a synthesising session seamless from their first
            // millisecond.
            _pendingRelease = true;
            _pendingUpMs = nowMs;
            return UpAction.DeferRelease;
        }

        _holding = false;
        _lastReleaseMs = nowMs;
        return UpAction.ReleaseNow;
    }

    /// <summary>
    /// The host's deferral timer fired. True when the release should actually
    /// happen — no down arrived to claim it as synthetic.
    /// </summary>
    public bool DeferralElapsed(long nowMs)
    {
        if (!_pendingRelease) return false;
        _pendingRelease = false;
        _holding = false;
        _lastReleaseMs = nowMs;
        return true;
    }

    /// <summary>
    /// External teardown (radio closed, TX forced off). Clears the in-flight
    /// hold; keeps what has been learned — that describes the screen reader,
    /// not the hold.
    /// </summary>
    public void Reset()
    {
        _holding = false;
        _pendingRelease = false;
    }

    private void Learn(long gapMs)
    {
        if (gapMs <= 0 || gapMs > RekeyLearnWindowMs) return;
        _gaps[_gapCount % LearnedHistory] = gapMs;
        _gapCount++;

        long max = 0;
        int have = Math.Min(_gapCount, LearnedHistory);
        for (int i = 0; i < have; i++)
            if (_gaps[i] > max) max = _gaps[i];

        long defer = max * 3 / 2;   // spacing plus half again of grace
        if (defer < DefaultDeferMs) defer = DefaultDeferMs;
        if (defer > MaxDeferMs) defer = MaxDeferMs;
        DeferMs = (int)defer;
    }
}
