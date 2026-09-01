using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Remembers that transmit audio was seen to arrive, so the "check
    /// microphone" warning is not re-run on every over (#459).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Static on purpose.</b> The thing that invalidates a proof is usually
    /// nowhere near the thing that took it — an audio device dialog, a mic
    /// profile being applied, a reconnect. Making the invalidation reachable
    /// from anywhere in one line is the whole point; a hook that needs a
    /// reference threaded to it is a hook that the next author quietly does not
    /// call, and this project already knows what an uncalled safety hook looks
    /// like.
    /// </para>
    /// <para>
    /// The decisions are pure and live in <see cref="TransmitSafety"/>; this
    /// holds only the two facts they need and the clock.
    /// </para>
    /// </remarks>
    public static class MicPathVerification
    {
        private static readonly object Gate = new object();
        private static bool _have;
        private static string _signature = "";
        private static Stopwatch _since;

        /// <summary>
        /// Record that audio arrived on the path described by
        /// <paramref name="signature"/>.
        /// </summary>
        public static void NoteVerified(string signature)
        {
            lock (Gate)
            {
                bool renewing = _have && string.Equals(_signature, signature ?? "",
                                                       StringComparison.Ordinal);
                _have = true;
                _signature = signature ?? "";
                _since = Stopwatch.StartNew();
                if (!renewing)
                    Tracing.TraceLine(
                        "MicPathVerification: transmit audio arrived — the path is proven for "
                        + TransmitSafety.MicVerifiedForSeconds.ToString("F0") + " s",
                        TraceLevel.Info);
            }
        }

        /// <summary>
        /// Whether the path in front of us right now is one we have already
        /// proven, recently enough to still believe.
        /// </summary>
        public static bool Holds(string signatureNow)
        {
            lock (Gate)
            {
                double age = _since?.Elapsed.TotalSeconds ?? double.MaxValue;
                bool holds = TransmitSafety.MicVerificationStillHolds(
                    _have, age, _signature, signatureNow);

                // Drop a proof that has stopped applying rather than leaving it
                // to be re-evaluated forever: the next transmission then starts
                // watching from scratch, which is what we want.
                if (_have && !holds)
                {
                    _have = false;
                    _signature = "";
                    _since = null;
                    Tracing.TraceLine(
                        "MicPathVerification: the proven transmit audio path no longer applies"
                        + (age > TransmitSafety.MicVerifiedForSeconds
                            ? " — it expired" : " — the audio path changed"),
                        TraceLevel.Info);
                }
                return holds;
            }
        }

        /// <summary>
        /// Throw the proof away because something that could change the audio
        /// path just changed.
        /// </summary>
        /// <param name="reason">
        /// What changed, in a few plain words, for the trace. Never spoken.
        /// </param>
        public static void Invalidate(string reason)
        {
            lock (Gate)
            {
                if (!_have) return;
                _have = false;
                _signature = "";
                _since = null;
            }
            Tracing.TraceLine(
                "MicPathVerification: dropped — " + (reason ?? "the audio path changed")
                + ". The next transmission checks transmit audio again.",
                TraceLevel.Info);
        }

        /// <summary>FOR TESTS ONLY.</summary>
        internal static void ResetForTests()
        {
            lock (Gate) { _have = false; _signature = ""; _since = null; }
        }
    }
}
