using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Seals the running diagnostic capture when the RADIO's connection dies,
    /// and tells whoever is listening where the file went.
    ///
    /// <para><b>The gap this closes.</b> <c>JJTrace.TraceSessionOutcome</c> has
    /// defined <c>connection_dropped</c> since Sprint 29 and <b>no archive on
    /// this machine has ever carried it</b> — 231 zips, every one of them
    /// <c>clean_exit</c>, <c>killed</c>, <c>no_radios</c> or
    /// <c>slice_unavailable</c> (counted 2026-09-22). The reason is that
    /// nothing on the drop path archived anything: <c>ArchiveCurrentTraceSession</c>
    /// had exactly two callers, <c>ApplicationEvents.vb</c> and
    /// <c>DebugInfo.vb</c>, and both pass <c>CleanExit</c>. A radio dying
    /// mid-transmit left the capture open, the operator carried on, and hours
    /// later the app closed and sealed the whole evening as a normal one. <b>A
    /// radio death and a quiet evening produced the same file.</b></para>
    ///
    /// <para><b>Why it has to be told rather than inferred.</b> The outcome is
    /// decided by <c>TraceSession.MarkOutcome</c>, first call wins, and
    /// <c>ToManifestEntry</c> defaults an unmarked session to <c>clean_exit</c>
    /// the moment <c>End()</c> is called. So the drop must mark it AT THE DROP;
    /// there is no later point where the truth is still available.</para>
    ///
    /// <para><b>Only a radio-side loss.</b> The operator's own Disconnect is
    /// <c>RadioRemovalKind.SelfInitiated</c> and seals nothing — the manifest
    /// already has that vocabulary, and tagging a deliberate hang-up
    /// <c>connection_dropped</c> would poison the one query this outcome
    /// exists to answer.</para>
    ///
    /// <para><b>Why the seal is not conditional on a capture being running.</b>
    /// Whatever session is open is the evidence, standing log or detailed
    /// capture. Sealing it costs one zip and gains a file with the right word on
    /// it; declining to seal because the operator had not pressed Ctrl+J Ctrl+D
    /// would mean the unplanned case — which is every case that matters — keeps
    /// producing files that say <c>clean_exit</c>.</para>
    ///
    /// <para><b>THE REQUEST IS BOUND AT THE DROP, not resolved at the archive
    /// (#596's cousin, corrected Sprint 45 Track H2).</b> The first build queued
    /// only a radio name and let the worker ask, whenever it finally ran, "what
    /// is the current session?" — so a Stop, a new capture, a log toggle or an
    /// exit arriving in between made it archive a DIFFERENT session, or none,
    /// and nothing said so. <c>gpt-6-sol</c> called that the central merge
    /// blocker (<c>for-claude/2026-09-22-codex-verify-track-h.md</c>). The drop
    /// now reads <see cref="TraceSessionContext.Current"/> on its own thread,
    /// carries that session's id to the worker, and the worker seals only if
    /// that same session is still the one recording. If it has gone, nothing is
    /// archived and the trace says which session was wanted.</para>
    ///
    /// <para><b>And the duplicate is recognised by the drop, not by a bit.</b>
    /// The first build spent one process-global flag per trace session — and
    /// the seal's own restart of the standing log RE-ARMED it, so a second
    /// removal from the same drop arriving after the restart sealed the fresh,
    /// empty log and put a second window in front of an operator whose radio
    /// had just died. The claim is now keyed on the removal itself: the same
    /// <c>Radio</c> object is the same drop, whatever session is current by
    /// then. That key is exact, because
    /// <c>FlexBase.ClassifyRadioRemoval</c> only calls a removal a drop of OUR
    /// radio when the object IS <c>theRadio</c>, so a genuinely new drop needs
    /// a new connection first.</para>
    /// </summary>
    public static class CaptureSeal
    {
        /// <summary>
        /// Seal the trace session the drop was about, with the
        /// <c>connection_dropped</c> outcome, and hand back the full path of the
        /// archive, or null if nothing was sealed. Installed by the application
        /// at startup; null until then, and a null hook makes every call below a
        /// no-op that says so in the trace.
        ///
        /// <para>A hook rather than a call, because the sealing lives in the VB
        /// application (<c>globals.vb</c>) and this assembly is referenced BY
        /// it. Same seam, and for the same reason, as
        /// <c>JJFlexWpf.DiagnosticsBridge</c>.</para>
        /// </summary>
        /// <remarks>First argument is the id of the session that was recording
        /// when the connection dropped — the hook archives THAT session or
        /// nothing. Second is the outcome detail recorded on the manifest
        /// entry.</remarks>
        public static Func<Guid, string, string> SealHook { get; set; }

        /// <summary>
        /// Raised once a drop has sealed a capture, carrying where it landed.
        /// The WPF layer subscribes and shows the operator the path; anything
        /// else that wants to know may too. Raised on a background thread —
        /// subscribers marshal for themselves.
        /// </summary>
        public static event Action<CaptureSealNotice> SealedAfterDrop;

        /// <summary>
        /// How the sealing work reaches a background thread. Production queues
        /// it; a test replaces it so the lifecycle race can be driven
        /// deliberately — queue the drop, end the session, THEN run the worker
        /// and watch it decline. That race is not visible to any test that
        /// cannot control when the worker wakes, which is why it survived the
        /// first build's thirty-seven tests.
        /// </summary>
        internal static Action<Action> Queue { get; set; } = work => Task.Run(work);

        /// <summary>
        /// Two removals carrying the same <c>Radio</c> object are the same drop,
        /// however far apart — but only within this window, because nothing here
        /// has been able to verify that FlexLib always hands back a NEW object
        /// after a reconnect. Beyond it the claim lapses and a second drop of
        /// what looks like the same radio seals again.
        ///
        /// <para>A minute is two orders of magnitude more than the duplicate
        /// needs: the neighbouring removal work measured a re-added sighting at
        /// 119 ms. The bound only decides which way an unverified case falls,
        /// and it falls towards sealing, because a spurious refusal loses the
        /// file this whole bridge exists to produce.</para>
        /// </summary>
        internal const int SameDropWindowMs = 60_000;

        private static readonly object _claimGate = new object();
        private static WeakReference _claimedRadio;
        private static Guid _claimedSession;
        private static int _claimedTick;
        private static bool _hasClaim;

        /// <summary>
        /// Claim this drop. Returns true to exactly one removal per drop.
        ///
        /// <para>The key is the removal, not the session and not a flag. A
        /// process-global bit could not do this job: the seal restarts the
        /// standing log, restarting a log begins a session, and the session is
        /// what re-armed the bit — so the guard disarmed itself in time for the
        /// duplicate it existed to refuse.</para>
        /// </summary>
        private static bool TryClaimDrop(object dropToken, Guid sessionId, int nowTick)
        {
            lock (_claimGate)
            {
                if (_hasClaim && IsTheSameDrop(dropToken, sessionId, nowTick)) return false;
                _hasClaim = true;
                _claimedRadio = dropToken == null ? null : new WeakReference(dropToken);
                _claimedSession = sessionId;
                _claimedTick = nowTick;
                return true;
            }
        }

        private static bool IsTheSameDrop(object dropToken, Guid sessionId, int nowTick)
        {
            if ((nowTick - _claimedTick) > SameDropWindowMs) return false;

            object prior = _claimedRadio?.Target;
            if (dropToken != null && prior != null) return ReferenceEquals(dropToken, prior);

            // No object to compare — a caller that had none, or the radio we
            // claimed against has been collected. Fall back to the session the
            // claim was bound to: one seal per session is the old rule, and it
            // is still the safe answer when identity is unavailable.
            return _claimedSession == sessionId;
        }

        /// <summary>Forget the claim. Tests only: production never needs this,
        /// because a new drop brings a new radio object with it.</summary>
        internal static void ForgetClaimForTests()
        {
            lock (_claimGate)
            {
                _hasClaim = false;
                _claimedRadio = null;
                _claimedSession = Guid.Empty;
                _claimedTick = 0;
            }
        }

        /// <summary>
        /// The outcome detail written onto the manifest entry. A sentence, not a
        /// code: the trace browser shows this text, and "the radio's connection
        /// dropped" is what a person reading a list of sessions needs to see.
        /// </summary>
        public static string OutcomeDetail(string radioName) =>
            string.IsNullOrWhiteSpace(radioName)
                ? "The radio's connection dropped while this session was running"
                : "The connection to " + radioName.Trim() + " dropped while this session was running";

        /// <summary>
        /// A radio we were connected to went away without us asking. Seal the
        /// session that was recording AT THIS MOMENT, and announce where it
        /// landed.
        ///
        /// <para>Returns immediately: the sealing itself compresses a file that
        /// can be megabytes, and this is called from FlexLib's own removal
        /// handler, on FlexLib's thread, in the middle of a teardown. Blocking
        /// that to zip a log would be a hang in the one situation where the
        /// application most needs to stay responsive.</para>
        ///
        /// <para>Which is exactly why the session has to be read HERE, on this
        /// thread, before returning. Everything after this line is running in a
        /// world where the operator may already have stopped the capture,
        /// started another, toggled logging or closed the app.</para>
        /// </summary>
        /// <param name="dropToken">The removal's own identity — the
        /// <c>Radio</c> object FlexLib handed to the removal handler. Two
        /// removals carrying the same object are one drop. May be null; the
        /// claim then falls back to one seal per session.</param>
        /// <param name="radioName">The radio's nickname, for the sentence on the
        /// manifest entry and in the operator's window.</param>
        public static void AfterConnectionDrop(object dropToken, string radioName)
        {
            TraceSession session = TraceSessionContext.Current;
            if (session == null)
            {
                // Not a defect and not silence: nothing was being recorded, so
                // there is no evidence to seal. Said out loud because "no
                // connection_dropped archive appeared" needs to be answerable
                // afterwards, and "logging was off" is one of the answers.
                Tracing.TraceLine(
                    "CaptureSeal: the radio's connection dropped but nothing was recording — no session to seal",
                    TraceLevel.Warning);
                return;
            }

            var hook = SealHook;
            if (hook == null)
            {
                // Said out loud rather than swallowed. A missing hook means the
                // wiring never ran, and the symptom — a capture that quietly
                // says clean_exit — is indistinguishable from the bug this
                // class exists to fix. The claim is deliberately NOT taken: an
                // unwired call seals nothing, so it must not also consume the
                // drop and refuse a later, correctly wired one.
                Tracing.TraceLine(
                    "CaptureSeal: the radio's connection dropped but no seal hook is installed — "
                    + "the session will be archived as an ordinary one (wiring defect)",
                    TraceLevel.Warning);
                return;
            }

            Guid sessionId = session.SessionId;
            if (!TryClaimDrop(dropToken, sessionId, Environment.TickCount))
            {
                Tracing.TraceLine(
                    "CaptureSeal: this is the same drop that already sealed — not sealing again",
                    TraceLevel.Info);
                return;
            }

            string name = radioName ?? string.Empty;
            Queue(() => SealNow(hook, name, sessionId));
        }

        private static void SealNow(Func<Guid, string, string> hook, string radioName, Guid sessionId)
        {
            string path = null;
            try
            {
                // The session this drop is ABOUT, checked against the one
                // recording now. Anything that ended or replaced it between the
                // drop and this moment means the evidence has already been
                // archived — or thrown — by somebody else, and sealing whatever
                // happens to be open instead would put the wrong file in front
                // of the operator under the right name.
                TraceSession live = TraceSessionContext.Current;
                if (live == null || live.SessionId != sessionId)
                {
                    Tracing.TraceLine(
                        "CaptureSeal: the recording that was running when the connection dropped ("
                        + sessionId + ") has already ended — nothing was archived for this drop",
                        TraceLevel.Warning);
                    return;
                }

                Tracing.TraceLine(
                    "CaptureSeal: sealing the running capture as " + TraceSessionOutcome.ConnectionDropped
                    + " — " + OutcomeDetail(radioName),
                    TraceLevel.Warning);
                path = hook(sessionId, OutcomeDetail(radioName));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CaptureSeal: sealing failed: " + ex.Message, TraceLevel.Error);
            }

            if (string.IsNullOrEmpty(path))
            {
                // Nothing to point the operator at. Saying nothing is right
                // here: a dialog offering a path that does not exist is worse
                // than no dialog, and the standing log — restarted by the hook —
                // carries this line.
                Tracing.TraceLine(
                    "CaptureSeal: no archive was produced, so there is no path to show the operator",
                    TraceLevel.Warning);
                return;
            }

            Tracing.TraceLine("CaptureSeal: sealed to " + path, TraceLevel.Warning);

            try
            {
                SealedAfterDrop?.Invoke(new CaptureSealNotice(radioName, path));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CaptureSeal: telling the operator failed: " + ex.Message, TraceLevel.Error);
            }
        }
    }
}
