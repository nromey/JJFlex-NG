#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// The bookkeeping behind Radio Setup's step-2 line: one SmartLink
    /// registration check in flight at a time, whose answer belongs to the
    /// radio it was asked about and to no other.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect this exists to end.</b> Radio Setup kept a cached
    /// answer and the serial it was for, but the line it drew read the answer
    /// without comparing the serial to the radio on screen. So if radio A's
    /// check was in flight when the operator switched to radio B, B could not
    /// start its own check (one at a time), and when A's finished the step
    /// showed B as registered to A's account — or, if A's finished without an
    /// answer, left B on "Checking" with nothing running, because the refresh
    /// a completion asks for was forbidden from starting a check at all. Sol's
    /// review of Track L3 (2026-09-24) traced it; #352 carries it.</para>
    ///
    /// <para><b>The rules, each of which the suite drives.</b>
    /// <list type="bullet">
    /// <item>One check at a time. <see cref="TryBegin"/> refuses while one is
    /// in flight, and refuses a radio whose answer is already held.</item>
    /// <item>An answer is held for exactly one serial, the one that was
    /// asked. <see cref="AnswerFor"/> returns it for that serial and null for
    /// any other, so a switch to another radio can never paint the old
    /// radio's answer on the new one.</item>
    /// <item>A check that finished without an answer is recorded, not cached
    /// as an answer: <see cref="FinishedWithoutAnAnswerFor"/> is what lets the
    /// step say so instead of "Checking", and a later ordinary refresh may ask
    /// again (Track L3).</item>
    /// <item>After a completion, a check is owed to the radio on screen only
    /// when nothing is in flight and that radio has neither an answer nor an
    /// unanswered record — <see cref="ACheckIsOwedTo"/>. That is what starts
    /// B's check once A's completes, and it cannot loop: a radio whose check
    /// just finished, answered or not, is never owed another by its own
    /// completion.</item>
    /// </list></para>
    ///
    /// <para>Pure on purpose, with no dialog, no dispatcher and no radio, so
    /// the A-to-B transition can be driven in the suite rather than read from
    /// source text. The dialog owns the words; this owns whose answer is
    /// whose.</para>
    /// </remarks>
    public sealed class SmartLinkRegistrationCheckLedger
    {
        private string? _serialInFlight;
        private string? _answeredSerial;
        private SmartLinkRegistrationEvidence.Finding? _answer;
        private string? _unansweredSerial;

        /// <summary>The serial whose check is running now, or null.</summary>
        public string? SerialInFlight => _serialInFlight;

        /// <summary>A check is running now.</summary>
        public bool InFlight => _serialInFlight != null;

        /// <summary>
        /// Whether a finding counts as an answer. Unknown and NoAccount do
        /// not: the first says nothing was established, the second changes
        /// the moment the operator signs in, and neither should be pinned.
        /// </summary>
        public static bool IsAnAnswer(SmartLinkRegistrationEvidence.Finding? finding) =>
            finding is { } f
            && f.Verdict is not (FlexBase.SmartLinkRegistrationQuery.Unknown
                                 or FlexBase.SmartLinkRegistrationQuery.NoAccount);

        /// <summary>
        /// Claim the one check that may run. True means the caller runs it
        /// and must later call <see cref="Complete"/>; false means one is
        /// already in flight, or this serial's answer is already held.
        /// </summary>
        public bool TryBegin(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return false;
            if (_serialInFlight != null) return false;
            if (_answer != null && SameSerial(serial, _answeredSerial)) return false;
            _serialInFlight = serial;
            return true;
        }

        /// <summary>
        /// The check for <paramref name="serial"/> has finished. A finding
        /// that is an answer is held for that serial, replacing whatever was
        /// held before; an unanswered finding, or null for a check that threw,
        /// is recorded as finished without an answer.
        /// </summary>
        public void Complete(string serial, SmartLinkRegistrationEvidence.Finding? finding)
        {
            _serialInFlight = null;
            if (IsAnAnswer(finding))
            {
                _answeredSerial = serial;
                _answer = finding;
                if (SameSerial(serial, _unansweredSerial)) _unansweredSerial = null;
            }
            else
            {
                _unansweredSerial = serial;
                if (SameSerial(serial, _answeredSerial))
                {
                    _answeredSerial = null;
                    _answer = null;
                }
            }
        }

        /// <summary>
        /// The held answer, if it is <paramref name="serial"/>'s. Null for any
        /// other serial, however recently another radio's check completed.
        /// </summary>
        public SmartLinkRegistrationEvidence.Finding? AnswerFor(string? serial) =>
            _answer != null && SameSerial(serial, _answeredSerial) ? _answer : null;

        /// <summary>
        /// The most recent check for <paramref name="serial"/> finished
        /// without an answer, and nothing has answered for it since.
        /// </summary>
        public bool FinishedWithoutAnAnswerFor(string? serial) =>
            _unansweredSerial != null && SameSerial(serial, _unansweredSerial);

        /// <summary>
        /// Whether a completion's refresh should start a check for the radio
        /// on screen: nothing is in flight, and that radio has neither an
        /// answer nor a finished-without-an-answer record. True after A's
        /// check completes while B is on screen; false for the radio whose
        /// own check just completed, so a completion never re-asks.
        /// </summary>
        public bool ACheckIsOwedTo(string? serial) =>
            !string.IsNullOrEmpty(serial)
            && !InFlight
            && AnswerFor(serial) == null
            && !FinishedWithoutAnAnswerFor(serial);

        private static bool SameSerial(string? a, string? b) =>
            a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
