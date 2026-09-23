using System;
using System.Diagnostics;
using System.Threading;
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
    /// </summary>
    public static class CaptureSeal
    {
        /// <summary>
        /// Seal the running session with the <c>connection_dropped</c> outcome
        /// and hand back the full path of the archive, or null if nothing was
        /// sealed. Installed by the application at startup; null until then, and
        /// a null hook makes every call below a no-op that says so in the trace.
        ///
        /// <para>A hook rather than a call, because the sealing lives in the VB
        /// application (<c>globals.vb</c>) and this assembly is referenced BY
        /// it. Same seam, and for the same reason, as
        /// <c>JJFlexWpf.DiagnosticsBridge</c>.</para>
        /// </summary>
        /// <remarks>Argument is the outcome detail recorded on the manifest
        /// entry.</remarks>
        public static Func<string, string> SealHook { get; set; }

        /// <summary>
        /// Raised once a drop has sealed a capture, carrying where it landed.
        /// The WPF layer subscribes and shows the operator the path; anything
        /// else that wants to know may too. Raised on a background thread —
        /// subscribers marshal for themselves.
        /// </summary>
        public static event Action<CaptureSealNotice> SealedAfterDrop;

        /// <summary>
        /// Zero once this trace session's seal has been spent. One seal per
        /// session: a drop can raise the removal more than once (the object and
        /// the re-added sighting), and a second seal would archive an empty
        /// session and put a second dialog on the operator.
        /// </summary>
        private static int _armed = 1;

        /// <summary>
        /// A new trace session has begun, so a drop can seal again. Called from
        /// <c>BeginNewTraceSession</c> — one re-arm per session, at the only
        /// place that knows a session started.
        /// </summary>
        public static void Rearm() => Interlocked.Exchange(ref _armed, 1);

        /// <summary>True while a drop would still seal. Diagnostic and test
        /// surface; the decision itself is <see cref="TrySpendSeal"/>.</summary>
        public static bool Armed => Volatile.Read(ref _armed) == 1;

        /// <summary>
        /// Claim this session's one seal. Returns true to exactly one caller.
        /// </summary>
        public static bool TrySpendSeal() => Interlocked.Exchange(ref _armed, 0) == 1;

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
        /// session and announce where it landed.
        ///
        /// <para>Returns immediately: the sealing itself compresses a file that
        /// can be megabytes, and this is called from FlexLib's own removal
        /// handler, on FlexLib's thread, in the middle of a teardown. Blocking
        /// that to zip a log would be a hang in the one situation where the
        /// application most needs to stay responsive.</para>
        /// </summary>
        public static void AfterConnectionDrop(string radioName)
        {
            if (!TrySpendSeal())
            {
                Tracing.TraceLine(
                    "CaptureSeal: this session's capture was already sealed by an earlier drop — not sealing again",
                    TraceLevel.Info);
                return;
            }

            var hook = SealHook;
            if (hook == null)
            {
                // Said out loud rather than swallowed. A missing hook means the
                // wiring never ran, and the symptom — a capture that quietly
                // says clean_exit — is indistinguishable from the bug this
                // class exists to fix.
                Tracing.TraceLine(
                    "CaptureSeal: the radio's connection dropped but no seal hook is installed — "
                    + "the session will be archived as an ordinary one (wiring defect)",
                    TraceLevel.Warning);
                return;
            }

            string name = radioName ?? string.Empty;
            Task.Run(() => SealNow(hook, name));
        }

        private static void SealNow(Func<string, string> hook, string radioName)
        {
            string path = null;
            try
            {
                Tracing.TraceLine(
                    "CaptureSeal: sealing the running capture as " + TraceSessionOutcome.ConnectionDropped
                    + " — " + OutcomeDetail(radioName),
                    TraceLevel.Warning);
                path = hook(OutcomeDetail(radioName));
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
