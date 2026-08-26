using System;
using System.Diagnostics;
using JJTrace;

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// The diagnostic capture's lifetime around one Fixer run: turned on when
    /// the run begins, announced out loud, turned off and archived when the
    /// run ends — and left exactly as it was found when it was already
    /// running (#173's ruling, learned the hard way by jjprobe).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why announce (#194):</b> a sighted operator gets a recording light;
    /// a blind operator gets nothing. A capture this class starts without a
    /// word would be one more piece of instrumentation running with no
    /// perceptible presence — the exact defect class the register names. So
    /// starting speaks, stopping speaks, and a capture that was already
    /// running says nothing, because this class changed nothing.
    /// </para>
    /// <para>
    /// <b>One packager.</b> Stopping goes through the same plumbing the
    /// Diagnostics tab and the Ctrl+J Ctrl+D chord use, which archives the
    /// session through <c>JJTrace.SessionArchive</c>. Nothing here zips,
    /// copies or files anything itself.
    /// </para>
    /// <para>
    /// The plumbing arrives as delegates because it lives behind
    /// <c>DiagnosticsBridge</c> on the WPF side, which this assembly cannot
    /// reference — and because delegates are what make the leave-as-found
    /// rules testable without a real trace stack.
    /// </para>
    /// </remarks>
    public sealed class FixerCaptureScope
    {
        /// <summary>The capture machinery, as the host has it. Any member may
        /// be null; missing plumbing reads as "capture not available".</summary>
        public sealed class Plumbing
        {
            /// <summary>Is the capture machinery wired at all?</summary>
            public Func<bool> IsAvailable;

            /// <summary>Is a detailed capture running right now?</summary>
            public Func<bool> IsCapturing;

            /// <summary>Start a detailed capture; the string is the reason
            /// recorded in the session manifest.</summary>
            public Action<string> Start;

            /// <summary>Stop the running capture and archive it.</summary>
            public Action Stop;

            /// <summary>Full path of the archive the last capture produced.</summary>
            public Func<string> LastArchivePath;

            /// <summary>Speak a sentence at critical verbosity.</summary>
            public Action<string> Announce;
        }

        private readonly Plumbing _plumbing;
        private bool _ended;

        /// <summary>True when this scope started the capture — and is
        /// therefore the one that must stop it. False means leave-as-found:
        /// the capture belongs to whoever started it.</summary>
        public bool WeStartedIt { get; private set; }

        /// <summary>What happened, in words the run record carries. Never
        /// empty after <see cref="Begin"/>.</summary>
        public string Note { get; private set; } = "";

        /// <summary>Where the capture's archive landed. Set by
        /// <see cref="End"/>, and only when this scope owned the capture.</summary>
        public string ArchivePath { get; private set; } = "";

        private FixerCaptureScope(Plumbing plumbing)
        {
            _plumbing = plumbing;
        }

        /// <summary>
        /// Open the scope for a run. Never throws; whatever happened is in
        /// <see cref="Note"/>.
        /// </summary>
        /// <param name="setName">The stage set's name, e.g. "Transmit" — the
        /// capture reason reads "Transmit checks run A52-5T2".</param>
        public static FixerCaptureScope Begin(string runId, string setName, Plumbing plumbing)
        {
            var scope = new FixerCaptureScope(plumbing);
            try
            {
                if (plumbing?.IsAvailable == null || plumbing.Start == null
                    || plumbing.IsCapturing == null || !SafeBool(plumbing.IsAvailable))
                {
                    scope.Note = "Diagnostic capture is not available, so no recording "
                               + "accompanies this run.";
                    return scope;
                }

                if (SafeBool(plumbing.IsCapturing))
                {
                    // Leave-as-found. It was on when we arrived; it stays on
                    // when we leave, and stopping it at the end would take away
                    // a recording the operator started for their own reasons.
                    scope.Note = "The diagnostic capture was already running when this run "
                               + "began and was left running. Its saved session covers more "
                               + "than this run.";
                    return scope;
                }

                plumbing.Start((setName ?? "").Trim().Length > 0
                    ? setName.Trim() + " checks run " + runId
                    : "Check run " + runId);

                // Trust the state, not the call: a Start that failed silently
                // must not leave this scope believing it owns a capture, or
                // End would stop a capture somebody else starts later.
                if (SafeBool(plumbing.IsCapturing))
                {
                    scope.WeStartedIt = true;
                    scope.Note = "A diagnostic recording ran alongside this run and was "
                               + "saved when it ended.";
                    Say(plumbing, Lexicon.Get("audio.fixer.capture_started"));
                }
                else
                {
                    scope.Note = "A diagnostic recording could not be started for this run.";
                    Tracing.TraceLine("FixerCaptureScope: capture did not start for run "
                        + runId, TraceLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                scope.Note = "A diagnostic recording could not be started for this run.";
                Tracing.TraceLine("FixerCaptureScope: begin failed — " + ex.Message,
                                  TraceLevel.Warning);
            }
            return scope;
        }

        /// <summary>
        /// Close the scope. Stops and archives the capture only when this
        /// scope started it; announces the stop; idempotent, so every close
        /// path — orderly close, abandon, failed init — can call it without
        /// coordination. Never throws.
        /// </summary>
        public void End()
        {
            if (_ended) return;
            _ended = true;

            if (!WeStartedIt) return;   // leave it as we found it

            try
            {
                _plumbing.Stop?.Invoke();
                try { ArchivePath = _plumbing.LastArchivePath?.Invoke() ?? ""; }
                catch { ArchivePath = ""; }
                Say(_plumbing, Lexicon.Get("audio.fixer.capture_stopped"));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerCaptureScope: stopping the capture failed — "
                    + ex.Message, TraceLevel.Warning);
            }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read != null && read(); } catch { return false; }
        }

        private static void Say(Plumbing plumbing, string sentence)
        {
            try { plumbing?.Announce?.Invoke(sentence); }
            catch { /* an announcement must never cost the run anything */ }
        }
    }
}
