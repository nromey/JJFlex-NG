namespace Radios;

/// <summary>
/// A push-to-talk key the operator was holding when the radio's connection
/// fell stays dead until it is let go: a reconnect never inherits a transmit
/// intent from before the drop (#596).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot be "the next down is a fresh press".</b> The drop path
/// releases the push-to-talk controller and forgets the key edges in flight,
/// which is what every other end of a transmission outside the key edges does.
/// But a key still physically held keeps sending downs: Windows' auto-repeat
/// every ~30 ms under a reader that passes holds through, and under JAWS a
/// train of synthesised down/up PAIRS about 250 ms apart, the first at the
/// Windows repeat delay (measured 2026-08-24; see <see cref="PttHoldFilter"/>).
/// With the edges forgotten, the first of those to arrive after the radio came
/// back would read as a press and key a freshly connected radio with no
/// deliberate act, which is the one thing a push-to-talk key exists to prevent.
/// </para>
/// <para>
/// <b>The rule.</b> Once armed, every down is swallowed until an up has been
/// seen AND no down has followed it for longer than <see cref="ReleaseGapMs"/>,
/// which is how long a held key can go quiet between a synthesised up and its
/// next down. A real release followed, after that, by a real press keys as it
/// always did. A release and re-press faster than the gap is swallowed too,
/// and the operator presses again: the safe side of an ambiguity, on a path
/// that only exists after a drop.
/// </para>
/// <para>
/// Armed only when a key was in flight at the drop. Arming with nothing held
/// would swallow the operator's first deliberate press after the reconnect.
/// Pure and host-clocked, so a test can replay both readers' streams.
/// </para>
/// </remarks>
public sealed class PttHeldThroughDrop
{
    private bool _armed;
    private long _lastUpMs = long.MinValue;

    /// <summary>True while a key held through a drop is being ignored.</summary>
    public bool Armed => _armed;

    /// <summary>
    /// How long after an up, with no down, the key counts as let go. Set at
    /// <see cref="Arm"/> from <see cref="PttHoldFilter.FirstGapDeferMs"/>,
    /// which is sized from this machine's own repeat delay and so covers the
    /// longest gap a synthesising reader leaves inside one hold.
    /// </summary>
    public int ReleaseGapMs { get; private set; } = PttHoldFilter.DefaultDeferMs;

    /// <summary>The connection fell with a push-to-talk key in flight.</summary>
    public void Arm(int releaseGapMs)
    {
        _armed = true;
        _lastUpMs = long.MinValue;
        ReleaseGapMs = releaseGapMs > 0 ? releaseGapMs : PttHoldFilter.DefaultDeferMs;
    }

    /// <summary>Forget the drop, e.g. at teardown.</summary>
    public void Disarm()
    {
        _armed = false;
        _lastUpMs = long.MinValue;
    }

    /// <summary>
    /// The push-to-talk chord went down. True when this down belongs to the
    /// key held through the drop and must not key; false when it is a press
    /// (and the guard has let go).
    /// </summary>
    public bool SwallowDown(long nowMs)
    {
        if (!_armed) return false;
        if (_lastUpMs != long.MinValue && nowMs - _lastUpMs > ReleaseGapMs)
        {
            Disarm();
            return false;
        }
        _lastUpMs = long.MinValue;   // the key is still held: wait for its next up
        return true;
    }

    /// <summary>
    /// The push-to-talk chord's key came up. True when it belonged to the key
    /// held through the drop, so the host has no hold of its own to release.
    /// </summary>
    public bool NoteUp(long nowMs)
    {
        if (!_armed) return false;
        _lastUpMs = nowMs;
        return true;
    }
}
