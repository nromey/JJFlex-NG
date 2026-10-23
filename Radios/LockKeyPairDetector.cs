#nullable enable
using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Detection only: counts a transmit-lock key whose release arrives too
    /// soon after its press to be a human letting go (#800, Phase 2 as
    /// settled on 2026-10-22). It changes nothing the operator hears or keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a counter and not a guard.</b> JAWS was measured on this key on
    /// 2026-10-22 05:08: a held Shift+Space arrived as one key-down, repeat
    /// key-downs every ~31 ms from 500 ms in, and one key-up — the same shape
    /// as NVDA, and no synthesised down/up pairs. Quick taps were 63 to 94 ms.
    /// The down/up PAIRS measured on 2026-08-24 were on the Up arrow, a key
    /// JAWS scripts; Shift+Space evidently has no script. So the repeat rule
    /// (<c>MainWindow.LockKeyDown</c> ignores repeats) covers both readers on
    /// the evidence there is, and nobody has measured a pair on this key. A
    /// pair window short enough to let a real lock-then-unlock inside half a
    /// second register twice is too short to absorb a pair, and one long
    /// enough to absorb a pair swallows the quick unlock — the contradiction
    /// that sank the dialog lock's first two rounds. So: count it, trace it,
    /// build no behaviour from a number nobody measured. If pairs ever appear
    /// on this key the trace says so, and a measured fix follows.
    /// </para>
    /// <para>
    /// The same pattern <c>PttSafetyController</c> uses for implausibly fast
    /// push-to-talk releases, with the same threshold.
    /// </para>
    /// </remarks>
    public sealed class LockKeyPairDetector
    {
        /// <summary>
        /// Shorter than any human release. The controller's push-to-talk
        /// detector uses the same 50 ms, and a source pin keeps them equal: a
        /// deliberate tap runs 60 ms or more (the shortest measured on this
        /// key was 62 ms), a synthetic pair is near zero.
        /// </summary>
        public const int ImplausibleReleaseMs = 50;

        private long _downMs = -1;

        /// <summary>How many lock-key releases arrived too fast to be human. Zero on both readers as measured.</summary>
        public int ImplausiblePairs { get; private set; }

        /// <summary>The first (non-repeat) down of a lock press.</summary>
        public void NoteDown(long nowMs) => _downMs = nowMs;

        /// <summary>
        /// The release of the Space key. Ignored when no lock press is in
        /// flight (a push-to-talk release comes through the same handler).
        /// </summary>
        /// <returns>True when this release was implausibly fast and was counted.</returns>
        public bool NoteUp(long nowMs)
        {
            if (_downMs < 0) return false;
            long held = nowMs - _downMs;
            _downMs = -1;
            if (held >= ImplausibleReleaseMs) return false;

            ImplausiblePairs++;
            // First occurrence only, as the controller does: if this is real it
            // happens several times a second, and a line per occurrence is the
            // trace flood that has cost this project debugging sessions.
            if (ImplausiblePairs == 1)
            {
                Tracing.TraceLine("LockKey: key-up arrived " + held + " ms after key-down — too fast to be a human"
                    + " letting go. The screen reader may be synthesising down/up pairs for Shift+Space,"
                    + " which no measurement has shown on this key (#800). Nothing was changed: the"
                    + " press toggled the lock once, as any press does. Further occurrences counted, not logged.",
                    TraceLevel.Error);
            }
            return true;
        }
    }
}
