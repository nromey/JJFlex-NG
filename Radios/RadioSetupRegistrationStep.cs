#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Radio Setup's step 2 for a radio on the local network whose
    /// registration can be attempted: the one SmartLink check that may run,
    /// and the line the step shows for the radio on screen.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect this exists to end (the batch 6 bench run,
    /// 2026-11-11, on #691's step-2 lines).</b> A radio already in a list a
    /// SmartLink session is holding is answered without a round trip:
    /// <see cref="FlexBase.QuerySmartLinkRegistrationAsync"/> returns a task
    /// that has ALREADY completed. The dialog drew the line first and started
    /// the check after, so the line was "Checking with SmartLink" and the
    /// answer landed underneath it in the same call. The completion's own
    /// redraw is skipped while the window is not yet loaded, and Settings
    /// assigns its radio before it is shown, so on every opening of Settings
    /// with a registered radio the step said it was checking and could not
    /// tell, while the trace said the radio was registered. Nothing redrew it
    /// until an unrelated event did.</para>
    ///
    /// <para><b>So the check starts BEFORE the line is chosen</b>, and the
    /// line is read from the ledger as it stands afterwards: an answer already
    /// in hand is the answer shown, a check still running reads as checking,
    /// and a check that finished here without an answer reads as finished.
    /// That ordering is the whole fix, and it lives here rather than in the
    /// dialog so the suite can drive it.</para>
    ///
    /// <para><see cref="SmartLinkRegistrationCheckLedger"/> still owns whose
    /// answer is whose; this owns when the check runs relative to the line,
    /// and which line each ledger state reads as.</para>
    /// </remarks>
    public sealed class RadioSetupRegistrationStep
    {
        /// <summary>One check at a time, and each answer held for its own radio.</summary>
        public SmartLinkRegistrationCheckLedger Ledger { get; } = new();

        /// <summary>
        /// The step's line for the radio <paramref name="serial"/>, after
        /// starting that radio's check when <paramref name="startACheck"/> is
        /// true and the ledger allows one.
        /// </summary>
        /// <param name="serial">The radio on screen.</param>
        /// <param name="accountEmail">The account registration would go to (the preflight's).</param>
        /// <param name="radioStateText">The radio's registration state, for the checking line.</param>
        /// <param name="startACheck">Whether this refresh may start a check.</param>
        /// <param name="query">The check itself. It may return a task that has already completed.</param>
        /// <param name="finishedLater">Called once when a check started here finishes AFTER this
        /// method has returned, so the caller can redraw. Not called for a check that finished
        /// before the line was chosen: that answer is already in the line returned.</param>
        public string Refresh(
            string? serial,
            string accountEmail,
            string radioStateText,
            bool startACheck,
            Func<Task<SmartLinkRegistrationEvidence.Finding>> query,
            Action finishedLater)
        {
            if (startACheck)
                RunCheck(serial ?? string.Empty, query, finishedLater);
            return LineFor(serial, accountEmail, radioStateText);
        }

        /// <summary>
        /// The line for the radio <paramref name="serial"/> as the ledger
        /// stands now. Starts nothing.
        /// </summary>
        public string LineFor(string? serial, string accountEmail, string radioStateText) =>
            Ledger.AnswerFor(serial) switch
            {
                // The account that LISTED the radio, never the signed-in
                // one: the query consults every held account, and a radio
                // found under another of them is registered THERE.
                // A Registered finding with no listing account cannot say
                // whose it is, so it falls through to the neutral line
                // rather than guessing.
                { Verdict: FlexBase.SmartLinkRegistrationQuery.Registered, ListedUnderAccount: { Length: > 0 } listedUnder } =>
                    Lexicon.Get("settings.radio.register.already_registered",
                        ("accountEmail", listedUnder)),
                // Deliberately not "not registered". The server said only
                // that it cannot route to this radio for this account right
                // now, and a step-2 status line that turns that into a
                // verdict is the same overreach the connect advisory made
                // (#352).
                { Verdict: FlexBase.SmartLinkRegistrationQuery.NotInAccountList } =>
                    Lexicon.Get("settings.radio.register.not_in_account_list",
                        ("accountEmail", accountEmail)),
                // The check has finished and established nothing, and none
                // is running: any check this refresh could start has already
                // been started above, so "in flight" is the whole question.
                // The status line says the state and stops: Noel's words,
                // 2026-10-02, "'Ready to register radio to' the account,
                // period, done", with nothing about how the check went and
                // no radio state after it. Explanation belongs in the step's
                // own instructions, not in a status line (#691 item 7).
                _ when !Ledger.InFlight
                       && Ledger.FinishedWithoutAnAnswerFor(serial) =>
                    Lexicon.Get("settings.radio.register.check_unanswered",
                        ("accountEmail", accountEmail)),
                _ =>
                    Lexicon.Get("settings.radio.register.checking",
                        ("accountEmail", accountEmail),
                        ("state", radioStateText)),
            };

        // async void on purpose, as the dialog's own check was: it is fire
        // and forget from a status refresh, and a redraw that throws should
        // reach the dispatcher's handler rather than vanish into a task
        // nobody observes. The check itself cannot throw out of here.
        private async void RunCheck(
            string serial,
            Func<Task<SmartLinkRegistrationEvidence.Finding>> query,
            Action finishedLater)
        {
            if (!Ledger.TryBegin(serial)) return;

            SmartLinkRegistrationEvidence.Finding? finding = null;
            // True until the check is seen to be still running when it is
            // awaited: a query that throws before returning has finished here
            // too, without an answer.
            bool finishedHere = true;
            try
            {
                var pending = query();
                finishedHere = pending.IsCompleted;
                finding = await pending;
            }
            catch (Exception ex)
            {
                // A check that threw has finished too, and without an answer.
                Tracing.TraceLine($"RadioSetupRegistrationStep: {ex.Message}", TraceLevel.Error);
            }
            finally
            {
                // Completed BEFORE anyone redraws, so the redraw sees the
                // check as finished rather than still in flight. Unknown and
                // NoAccount are recorded as finished without an answer, not
                // held as answers: a later ordinary refresh may ask again
                // rather than pinning a shrug, and NoAccount changes the
                // moment the operator signs in.
                Ledger.Complete(serial, finding);
            }

            // A check that finished before Refresh chose its line is already
            // in that line; redrawing for it would only repeat it.
            if (!finishedHere)
                finishedLater();
        }
    }
}
