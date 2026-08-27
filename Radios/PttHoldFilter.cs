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
/// <b>Evidence-gated.</b> Nothing changes until the synthetic signature has
/// been seen with its own eyes: a key-up arriving less than
/// <see cref="ImplausibleReleaseMs"/> after its key-down, which no human
/// produces on a deliberately held chord. On a reader that passes holds
/// through that never happens, every release is passed on immediately, and the
/// cost of this class is exactly zero.
/// </para>
///
/// <para>
/// ── WHY THIS WAS REWRITTEN, 2026-08-26 ──────────────────────────────
/// </para>
/// <para>
/// The first version shipped, armed correctly, absorbed correctly, and the
/// radio still keyed and unkeyed three times on a single held press. Confirmed
/// in <c>JJFlexRadioTrace-20260826-181039.txt</c>, two independent episodes,
/// both opening at exactly 353 ms — and 353 ms is not a JAWS number. It is
/// OURS. It is the deferral window elapsing.
/// </para>
/// <para>
/// <b>The defect was that one window was doing two jobs.</b> A held key
/// produces gaps of two quite different sizes: the FIRST gap of a press is the
/// Windows repeat delay (~500 ms — nothing repeats before then), and every gap
/// after it is the repeat interval (~250 ms). The filter learned from whatever
/// gaps it saw and kept one number. During a long hold it saw dozens of
/// ~250 ms repeat gaps, its eight-slot history filled with them, the window
/// shrank to about 350 ms — and then the FIRST gap of the very next press,
/// which is twice that, ran the window out and unkeyed the transmitter. So it
/// chopped once at the start of every press, forever, and the more the operator
/// used it the more certain that became. Learning made it worse, which is why
/// it looked like the fix had not been applied.
/// </para>
/// <para>
/// <b>The repair is to stop conflating them.</b> Two windows now:
/// <see cref="FirstGapDeferMs"/>, sized from the operator's OWN Windows repeat
/// delay via <see cref="KeyRepeatTiming"/>, and <see cref="RepeatGapDeferMs"/>,
/// learned from the pair spacing as before. A repeat gap can never shorten the
/// first-gap window, because they are not measurements of the same thing.
/// Note what is NOT here: no constant tuned to 353, or to 512, or to anything
/// measured on one machine on one evening. The number that produced the bug is
/// read from the machine that produces it.
/// </para>
/// <para>
/// <b>A short press does not pay for this.</b> A tap comfortably shorter than
/// the repeat delay CANNOT have been synthesised — nothing had repeated yet —
/// so its release is genuine by mechanism and is passed through at once. The
/// long window applies only where it is needed: a press that lasted long enough
/// for the reader to have started synthesising.
/// </para>
/// <para>
/// <b>And the timer is corroborated, not trusted.</b> When a deferral runs out
/// the host may offer a <see cref="PhysicalKeyDown"/> probe — the operating
/// system's own answer to "is that key down right now", which no amount of
/// synthesised event traffic changes. It can only ever EXTEND a hold, never
/// shorten one, and it is bounded, so a probe that is wrong costs a fraction of
/// a second of carrier and cannot key the transmitter open. If it is wrong in
/// the other direction it simply never fires and the windows above carry the
/// whole fix on their own.
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
    /// Floor under both windows. Below this a deferral cannot outlast even the
    /// fastest synthesis, so it would absorb nothing while still costing the
    /// operator latency — the worst of both.
    /// </summary>
    public const int DefaultDeferMs = 350;

    /// <summary>
    /// Ceiling on the REPEAT-stream window. Also bounds the extra carrier a
    /// deferred release can cost in the middle of a hold, which is the case
    /// that happens on every transmission.
    /// </summary>
    public const int MaxDeferMs = 750;

    /// <summary>
    /// Ceiling on the FIRST-gap window. Larger than
    /// <see cref="MaxDeferMs"/> because it is bounding a different quantity:
    /// Windows allows a repeat delay of a full second, and a window that could
    /// not reach it would fail exactly as the first version did. It is paid at
    /// most once per press, and only by a press already long enough to have
    /// started repeating.
    /// </summary>
    public const int MaxFirstGapDeferMs = 1600;

    /// <summary>
    /// A down this long after a release is a new press, not synthesis, and
    /// teaches the filter nothing.
    /// </summary>
    private const int RekeyLearnWindowMs = 1000;

    /// <summary>
    /// How long each <see cref="PhysicalKeyDown"/> corroboration buys. Short on
    /// purpose: it is a re-check, not a window, so a probe stuck at "down"
    /// costs a quarter second at a time rather than a whole deferral.
    /// </summary>
    public const int ProbeRecheckMs = 250;

    /// <summary>
    /// How many consecutive probe corroborations may extend one deferral before
    /// the release happens regardless. Bounds the carrier a lying probe can
    /// ever cost at <see cref="ProbeRecheckMs"/> times this — under a second.
    /// A genuinely held key produces a key-down long before this runs out.
    /// </summary>
    public const int MaxProbeExtensions = 4;

    private const int LearnedHistory = 8;

    /// <summary>
    /// True once a synthetic release has been seen this session. Sticky on
    /// purpose — nothing that has been learned about the reader stops being
    /// true because one hold ended.
    /// </summary>
    public bool SynthesisDetected { get; private set; }

    /// <summary>How many implausibly fast releases have been seen. Zero under NVDA.</summary>
    public int SyntheticReleaseCount { get; private set; }

    /// <summary>
    /// The window for the FIRST gap of a press — from the operator's own
    /// Windows repeat delay, plus half again of grace. Nothing can repeat or be
    /// synthesised before this has elapsed, so a shorter window guarantees the
    /// chop the first version shipped.
    /// </summary>
    public int FirstGapDeferMs { get; private set; } =
        Clamp(KeyRepeatTiming.DefaultDelayMs * 3 / 2, DefaultDeferMs, MaxFirstGapDeferMs);

    /// <summary>
    /// The window for gaps INSIDE an established repeat stream — largest
    /// learned pair spacing plus half again, bounded by
    /// <see cref="MaxDeferMs"/>. This is the one that governs how quickly a
    /// genuine release is noticed, because a genuine release almost always
    /// happens mid-hold.
    /// </summary>
    public int RepeatGapDeferMs { get; private set; } = DefaultDeferMs;

    /// <summary>
    /// The window to arm right now: the first-gap window until this press has
    /// had a synthetic pair absorbed, the repeat-stream window afterwards.
    /// </summary>
    public int DeferMs => _inRepeatStream ? RepeatGapDeferMs : FirstGapDeferMs;

    /// <summary>
    /// True once at least one synthetic pair of the CURRENT press has been
    /// absorbed — i.e. the reader's repeat stream is running and its cadence,
    /// not the repeat delay, is what the next gap will be.
    /// </summary>
    public bool InRepeatStream => _inRepeatStream;

    /// <summary>
    /// The operator's Windows repeat delay in milliseconds, as told to us by
    /// the host. Kept as well as the derived window because it is the
    /// mechanism behind the genuine-tap rule: a press over before repeating
    /// could have begun cannot have been synthesised.
    /// </summary>
    public int KeyRepeatDelayMs { get; private set; } = KeyRepeatTiming.DefaultDelayMs;

    /// <summary>
    /// The operating system's answer to "is the push-to-talk key physically
    /// down right now", or null when the host cannot offer one.
    ///
    /// Consulted ONLY when a deferral runs out, and able only to extend the
    /// hold — see the class remarks. Injected rather than called directly so
    /// the whole state machine stays replayable in tests with no keyboard.
    /// </summary>
    public Func<bool>? PhysicalKeyDown { get; set; }

    /// <summary>
    /// What the probe said the last time a deferral ran out: true, false, or
    /// null for "not asked / no probe". Reported in the trace so a bench run
    /// answers, with evidence, whether the operating system's key state
    /// survives a synthesising reader — a question this design does not depend
    /// on but would like settled.
    /// </summary>
    public bool? LastProbeSaidDown { get; private set; }

    /// <summary>How many times a probe has extended a deferral. Zero when the probe never fires.</summary>
    public int ProbeExtensions { get; private set; }

    private bool _holding;
    private long _downMs;
    private bool _pendingRelease;
    private long _pendingUpMs;
    private bool _inRepeatStream;
    private int _extensionsThisRelease;
    private long _lastUpMs = long.MinValue;
    private long _lastReleaseMs = long.MinValue;
    private readonly long[] _gaps = new long[LearnedHistory];
    private int _gapCount;

    /// <summary>
    /// Tell the filter this machine's keyboard repeat delay. Called once at
    /// startup from <see cref="KeyRepeatTiming.DelayMs"/>; a test passes it
    /// directly.
    /// </summary>
    public void SetKeyRepeatDelay(int delayMs)
    {
        if (delayMs <= 0) return;
        KeyRepeatDelayMs = delayMs;
        FirstGapDeferMs = Clamp(delayMs * 3 / 2, DefaultDeferMs, MaxFirstGapDeferMs);
    }

    /// <summary>The chord's key-down arrived.</summary>
    public DownAction NoteDown(long nowMs)
    {
        if (_pendingRelease)
        {
            // A down while a release was pending: the hold never physically
            // ended. The gap from the up to this down IS the reader's pair
            // spacing — learn it, and note that the repeat stream is now
            // running, so the next gap will be a repeat gap and not another
            // repeat-delay-sized one.
            Learn(nowMs - _pendingUpMs);
            _pendingRelease = false;
            _extensionsThisRelease = 0;
            _inRepeatStream = true;
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
            return Defer(nowMs);
        }

        if (SynthesisDetected)
        {
            // A press that ended well before this machine could have begun
            // repeating cannot have been synthesised — there was nothing to
            // synthesise from yet — so this up is the operator's own and goes
            // straight through. The factor of two is margin, not tuning: it
            // keeps a press that lands NEAR the repeat delay (where the first
            // synthetic up of a hold appears, and where it always looks like a
            // plausible human press) on the deferring side, where it belongs.
            if (!_inRepeatStream && heldMs * 2 <= KeyRepeatDelayMs)
            {
                _holding = false;
                _lastReleaseMs = nowMs;
                return UpAction.ReleaseNow;
            }

            // Otherwise defer, even though it looks plausible: the FIRST
            // synthetic up of every press measures from the original down and
            // always looks plausible. This is what makes the second and later
            // holds of a synthesising session seamless from their first
            // millisecond.
            return Defer(nowMs);
        }

        _holding = false;
        _inRepeatStream = false;
        _lastReleaseMs = nowMs;
        return UpAction.ReleaseNow;
    }

    private UpAction Defer(long nowMs)
    {
        _pendingRelease = true;
        _pendingUpMs = nowMs;
        _extensionsThisRelease = 0;
        return UpAction.DeferRelease;
    }

    /// <summary>
    /// The host's deferral timer fired. True when the release should actually
    /// happen — no down arrived to claim it as synthetic, and the operating
    /// system does not say the key is still physically down.
    ///
    /// When it returns FALSE with a release still pending, the host must re-arm
    /// its timer for <see cref="NextRecheckMs"/> and ask again. That is the
    /// probe extending the hold, and it is bounded.
    /// </summary>
    public bool DeferralElapsed(long nowMs)
    {
        if (!_pendingRelease) return false;

        // Corroborate before unkeying. A synthesising reader can fabricate any
        // amount of event traffic; it does not reach into the operating
        // system's idea of which keys are down.
        var probe = PhysicalKeyDown;
        if (probe != null && _extensionsThisRelease < MaxProbeExtensions)
        {
            bool down;
            try { down = probe(); }
            catch { down = false; }   // an unusable probe must not hold the transmitter

            LastProbeSaidDown = down;
            if (down)
            {
                _extensionsThisRelease++;
                ProbeExtensions++;
                return false;         // still held — keep transmitting, ask again
            }
        }
        else if (probe == null)
        {
            LastProbeSaidDown = null;
        }

        _pendingRelease = false;
        _extensionsThisRelease = 0;
        _holding = false;
        _inRepeatStream = false;
        _lastReleaseMs = nowMs;
        return true;
    }

    /// <summary>
    /// How long the host should wait before calling
    /// <see cref="DeferralElapsed"/> again after it returned false with a
    /// release still pending.
    /// </summary>
    public int NextRecheckMs => ProbeRecheckMs;

    /// <summary>True while a deferred release is waiting to be resolved.</summary>
    public bool ReleasePending => _pendingRelease;

    /// <summary>
    /// External teardown (radio closed, TX forced off). Clears the in-flight
    /// hold; keeps what has been learned — that describes the screen reader,
    /// not the hold.
    /// </summary>
    public void Reset()
    {
        _holding = false;
        _pendingRelease = false;
        _inRepeatStream = false;
        _extensionsThisRelease = 0;
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

        // Sized from the repeat stream, and applied ONLY to the repeat stream.
        // Letting this touch FirstGapDeferMs is the bug this rewrite exists to
        // remove: the repeat cadence is roughly half the repeat delay, so a
        // shared number is dragged below the gap it most needs to bridge.
        RepeatGapDeferMs = Clamp((int)(max * 3 / 2), DefaultDeferMs, MaxDeferMs);
    }

    private static int Clamp(int value, int low, int high) =>
        value < low ? low : (value > high ? high : value);
}
