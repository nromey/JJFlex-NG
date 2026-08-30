#nullable enable

using System;

namespace Radios
{
    /// <summary>
    /// State machine for the connect-flow quiet scope (#395): the window of
    /// time in which a connect flow owns the operator's ears, so the flow's
    /// own window churn — activation changes, focus restores, menu swaps —
    /// does not narrate itself over the connect narration.
    ///
    /// <para><b>Pure state, no UI.</b> The WPF half (MainWindow) owns the
    /// dispatcher, the failsafe timer and the landing; every DECISION lives
    /// here so it can be pinned by tests without constructing a window. The
    /// original Track D implementation kept all of this as loose fields
    /// inside MainWindow and shipped with zero assertions over the state
    /// machine — and the first stuck scope reached the operator as a
    /// two-minute total lockout (2026-08-30, three times in one day).</para>
    ///
    /// <para><b>The lifecycle.</b> A scope is entered by whichever begin
    /// arrives first (the menu command, the rescue button, WireRadioEvents
    /// for auto-connect and retry legs) and finishes from a Background-
    /// priority dispatcher post after the flow ends. DOORS (the menu command,
    /// the rescue button) bracket the whole flow in a try/finally; while one
    /// is open, only the door's own end request lets the finish post, because
    /// the flow pumps messages while it runs and an inner finish would land
    /// while the Connecting window is still up. A generation counter keeps a
    /// finish posted by an abandoned leg from ending the scope of the leg
    /// that superseded it.</para>
    /// </summary>
    public sealed class ConnectQuietScope
    {
        /// <summary>
        /// How long a scope may stay open before the failsafe closes it.
        ///
        /// <para><b>Why ten seconds and not two minutes.</b> The scope exists
        /// to cover a burst of window churn measuring about one second (the
        /// 2026-08-29 capture: five window announcements inside the connect's
        /// two sentences), plus the menu route's run-up — the discovery
        /// settling window blocks about 5.5 s and the picker is engaged by
        /// about 6.2 s after the menu command (traces of 2026-08-30). A scope
        /// still open at ten seconds is therefore already a defect, and the
        /// failsafe's job is to make the damage small. The original value was
        /// 120 seconds, sized to "outlast any legitimate connect" — and on
        /// 2026-08-30 the operator met it three times as a two-minute outage:
        /// while a scope is open the dialog-close focus repair stands down,
        /// so a connect flow whose foreground escaped left the keyboard dead
        /// and the screen reader silent until the failsafe's landing repaired
        /// focus. For a blind operator an application that has stopped
        /// speaking and stopped answering keys is indistinguishable from a
        /// crashed one; he killed the process rather than wait. Two minutes
        /// of that is not a safety net, it is the failure.</para>
        ///
        /// <para><b>Firing is routine, not an alarm.</b> The doors bracket
        /// the whole modal picker, so an operator who browses the roster for
        /// eleven seconds holds the scope open past this deadline in a
        /// perfectly healthy session. The finish handles that: its landing
        /// stands down when a window of ours holds the foreground, so a
        /// healthy expiry only lifts the suppressions — silently. Only when
        /// the landing actually has to repair (focus genuinely stranded)
        /// does the operator hear about it. A connect leg that legitimately
        /// outlives the deadline (a slow SmartLink walk) loses its
        /// suppression for the tail of the churn — a little extra narration,
        /// accepted deliberately over any repeat of the lockout.</para>
        /// </summary>
        public const int FailsafeMs = 10_000;

        /// <summary>
        /// How long the stand-down waits before checking whether the focus
        /// repair it suppressed was actually needed. Long enough for the
        /// flow's next window to arrive and take the foreground (the
        /// Connecting window is shown immediately after the picker closes;
        /// the picker's ContentRendered activation follows the settling
        /// window within a beat) — short enough that a genuinely stranded
        /// keyboard is repaired in under a second instead of after the
        /// failsafe.
        /// </summary>
        public const int StrandedFocusRescueDelayMs = 750;

        /// <summary>What a call to <see cref="Begin"/> found.</summary>
        public enum BeginKind
        {
            /// <summary>No scope was open; this call opened one.</summary>
            Fresh,
            /// <summary>A scope was already open; this call extended it.</summary>
            Extended,
        }

        /// <summary>What an end request should do.</summary>
        public enum EndDecision
        {
            /// <summary>No scope is open — nothing to end.</summary>
            NotOpen,
            /// <summary>A door still holds the flow open; its own end
            /// request (in its finally, after the flow truly returned) is
            /// the one that will finish the scope.</summary>
            DeferredToDoor,
            /// <summary>The finish should be posted (at Background priority,
            /// guarded by <see cref="ShouldRunPostedFinish"/>).</summary>
            FinishDue,
        }

        /// <summary>What the finish should do.</summary>
        public enum FinishKind
        {
            /// <summary>No scope is open — nothing to finish.</summary>
            NotOpen,
            /// <summary>A radio powered on inside the scope: the connect
            /// narration owns the announcement; only ensure focus is
            /// somewhere real, silently, while the scope is still marked
            /// quiet — then <see cref="Close"/>.</summary>
            PowerOnQuietNormalize,
            /// <summary>Nothing powered on (cancel, failure, or a rescued
            /// stuck scope): <see cref="Close"/> first so the landing may
            /// speak, then run the standard return-to-app landing.</summary>
            NoPowerOnLanding,
        }

        /// <summary>True while a connect flow owns announcements and focus.</summary>
        public bool IsQuiet { get; private set; }

        /// <summary>Did a radio actually power on during the current scope?</summary>
        public bool SawPowerOn { get; private set; }

        /// <summary>
        /// How many doors (menu command, rescue button) currently hold the
        /// scope open. Never negative: a door end after a failsafe already
        /// closed the scope decrements only what is there.
        /// </summary>
        public int DoorDepth { get; private set; }

        /// <summary>
        /// Invalidates finishes posted by superseded legs of the flow. Bumped
        /// on every <see cref="Begin"/>, fresh or extending.
        /// </summary>
        public int Generation { get; private set; }

        /// <summary>
        /// Enter the scope. Idempotent — the first entry wins; a re-entry
        /// from a retry leg extends the same scope. Pass <paramref name="door"/>
        /// true from the call sites that BRACKET the whole flow; their
        /// matching end request is the only one honored while they are open.
        /// A fresh begin resets the door depth: any door count left behind by
        /// a failsafe-closed scope belonged to that scope, not this one.
        /// </summary>
        public BeginKind Begin(bool door = false)
        {
            Generation++;
            BeginKind kind;
            if (!IsQuiet)
            {
                IsQuiet = true;
                SawPowerOn = false;
                DoorDepth = 0;
                kind = BeginKind.Fresh;
            }
            else
            {
                kind = BeginKind.Extended;
            }
            if (door) DoorDepth++;
            return kind;
        }

        /// <summary>
        /// Request the end of the scope. Safe to call from every exit of
        /// every door — the caller acts on the decision returned.
        /// </summary>
        public EndDecision RequestEnd(bool door = false)
        {
            if (door && DoorDepth > 0) DoorDepth--;
            if (!IsQuiet) return EndDecision.NotOpen;
            if (DoorDepth > 0) return EndDecision.DeferredToDoor;
            return EndDecision.FinishDue;
        }

        /// <summary>
        /// Gate for a finish that was posted when <see cref="RequestEnd"/>
        /// returned <see cref="EndDecision.FinishDue"/>: run it only if the
        /// scope is still open and no newer leg has taken it over.
        /// </summary>
        public bool ShouldRunPostedFinish(int generationAtPost)
            => IsQuiet && generationAtPost == Generation;

        /// <summary>
        /// A radio really arrived. Recorded only while a scope is open — a
        /// re-raised power event mid-session records nothing.
        /// </summary>
        public void NotePowerOn()
        {
            if (IsQuiet) SawPowerOn = true;
        }

        /// <summary>
        /// What the finish should do right now. Pure peek — call
        /// <see cref="Close"/> to actually end the scope, in the order the
        /// outcome requires (power-on normalizes while still quiet, the
        /// landing runs after the scope is cleared so it may speak).
        /// </summary>
        public FinishKind DecideFinish()
        {
            if (!IsQuiet) return FinishKind.NotOpen;
            return SawPowerOn ? FinishKind.PowerOnQuietNormalize : FinishKind.NoPowerOnLanding;
        }

        /// <summary>End the scope. Idempotent.</summary>
        public void Close()
        {
            IsQuiet = false;
            SawPowerOn = false;
        }
    }
}
