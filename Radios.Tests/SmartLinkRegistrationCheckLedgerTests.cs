#nullable enable

using Radios;
using Xunit;

using Finding = Radios.SmartLinkRegistrationEvidence.Finding;
using Verdict = Radios.FlexBase.SmartLinkRegistrationQuery;

namespace Radios.Tests
{
    /// <summary>
    /// Radio Setup's step-2 bookkeeping, driven through the transition Sol's
    /// review of Track L3 traced: radio A's check is in flight, the operator
    /// switches to radio B, and A's check completes (#352).
    /// </summary>
    /// <remarks>
    /// These drive the <see cref="SmartLinkRegistrationCheckLedger"/> the
    /// dialog now defers to, rather than reading the dialog's source, because
    /// the dialog is a window this project cannot construct and the defect
    /// was in the ORDER of events, which source text cannot show. The
    /// dialog's own use of the ledger — which value each line interpolates,
    /// and that a completion asks the ledger whether a check is owed — is
    /// pinned in <see cref="SmartLinkRegistrationCallerSourceTests"/>.
    /// </remarks>
    public sealed class SmartLinkRegistrationCheckLedgerTests
    {
        private const string A = "4925-1213-8600-6245";
        private const string B = "1111-2222-6400-3333";
        private const string AsAccount = "a-owner@example.com";

        private static Finding RegisteredTo(string account) =>
            new(Verdict.Registered, false, 1, account);

        private static Finding NotListed() =>
            new(Verdict.NotInAccountList, true, 1, string.Empty);

        /// <summary>
        /// The first half of the counterexample: A's check was in flight when
        /// the radio changed to B, so B could not start its own; A's then
        /// completed Registered, and B's line read A's account.
        /// </summary>
        [Fact]
        public void B_never_wears_As_answer()
        {
            var ledger = new SmartLinkRegistrationCheckLedger();

            Assert.True(ledger.TryBegin(A));
            // The operator switches to B while A's check runs. B cannot start.
            Assert.False(ledger.TryBegin(B));
            Assert.Equal(A, ledger.SerialInFlight);

            ledger.Complete(A, RegisteredTo(AsAccount));

            // B's line: nothing, not A's account.
            Assert.Null(ledger.AnswerFor(B));
            // And A's answer is A's, so the positive control holds.
            Assert.Equal(AsAccount, ledger.AnswerFor(A)!.Value.ListedUnderAccount);

            // The completion's refresh finds a check owed to B, and B's check
            // then runs and answers for B.
            Assert.True(ledger.ACheckIsOwedTo(B));
            Assert.True(ledger.TryBegin(B));
            ledger.Complete(B, NotListed());
            Assert.Equal(Verdict.NotInAccountList, ledger.AnswerFor(B)!.Value.Verdict);
        }

        /// <summary>
        /// The second half: A's check completed WITHOUT an answer while B was
        /// on screen. Track L3 forbade a completion's refresh from starting
        /// any check, so B sat on "Checking" with nothing running. Now the
        /// refresh asks whether a check is owed, and one is owed to B.
        /// </summary>
        [Fact]
        public void B_is_not_stranded_when_As_check_ends_without_an_answer()
        {
            var ledger = new SmartLinkRegistrationCheckLedger();

            Assert.True(ledger.TryBegin(A));
            Assert.False(ledger.TryBegin(B));
            ledger.Complete(A, Finding.Unknown());

            // The unanswered record is A's, not B's — B's line must not say
            // B's check finished without an answer, because B was never asked.
            Assert.True(ledger.FinishedWithoutAnAnswerFor(A));
            Assert.False(ledger.FinishedWithoutAnAnswerFor(B));
            Assert.Null(ledger.AnswerFor(B));

            // And B is owed a check, so "Checking" on B's line has a check
            // behind it.
            Assert.False(ledger.InFlight);
            Assert.True(ledger.ACheckIsOwedTo(B));
            Assert.True(ledger.TryBegin(B));
        }

        /// <summary>
        /// The other side of the same rule, which is what makes it safe: a
        /// completion never owes a check to the radio whose check just
        /// completed, answered or not, so an unanswered check cannot re-ask
        /// itself for as long as the dialog is open (Track L3's loop guard,
        /// kept).
        /// </summary>
        [Fact]
        public void A_completion_never_owes_a_check_to_its_own_radio()
        {
            var unanswered = new SmartLinkRegistrationCheckLedger();
            Assert.True(unanswered.TryBegin(A));
            unanswered.Complete(A, Finding.Unknown());
            Assert.False(unanswered.ACheckIsOwedTo(A));

            var answered = new SmartLinkRegistrationCheckLedger();
            Assert.True(answered.TryBegin(A));
            answered.Complete(A, RegisteredTo(AsAccount));
            Assert.False(answered.ACheckIsOwedTo(A));

            var threw = new SmartLinkRegistrationCheckLedger();
            Assert.True(threw.TryBegin(A));
            threw.Complete(A, null);
            Assert.False(threw.InFlight);
            Assert.True(threw.FinishedWithoutAnAnswerFor(A));
            Assert.False(threw.ACheckIsOwedTo(A));
        }

        /// <summary>
        /// An ordinary refresh — the tab opening, Refresh all steps — may ask
        /// again after an unanswered check, which is what "Refresh all steps
        /// checks again" promises; it does not re-ask a radio whose answer is
        /// held.
        /// </summary>
        [Fact]
        public void An_ordinary_refresh_asks_again_only_when_there_is_no_answer()
        {
            var ledger = new SmartLinkRegistrationCheckLedger();

            Assert.True(ledger.TryBegin(A));
            ledger.Complete(A, Finding.Unknown());
            Assert.True(ledger.TryBegin(A));
            ledger.Complete(A, RegisteredTo(AsAccount));
            Assert.False(ledger.TryBegin(A));

            // A held answer survives an unrelated unanswered check only for
            // its own serial: answering A again after an unanswered A clears
            // the unanswered record.
            Assert.False(ledger.FinishedWithoutAnAnswerFor(A));
        }

        /// <summary>
        /// One answer is held, for one serial, as the dialog always did
        /// ("keyed by serial so a different radio re-asks"). Switching back to
        /// A after B answered asks A again rather than showing B's answer or
        /// a stale A.
        /// </summary>
        [Fact]
        public void Switching_back_asks_again_rather_than_reusing_another_radios_answer()
        {
            var ledger = new SmartLinkRegistrationCheckLedger();

            Assert.True(ledger.TryBegin(A));
            ledger.Complete(A, RegisteredTo(AsAccount));
            Assert.True(ledger.TryBegin(B));
            ledger.Complete(B, NotListed());

            Assert.Null(ledger.AnswerFor(A));
            Assert.NotNull(ledger.AnswerFor(B));
            Assert.True(ledger.ACheckIsOwedTo(A));
            Assert.True(ledger.TryBegin(A));
        }

        /// <summary>
        /// A radio with no serial is never asked and never owed — the
        /// Rig-connected, serial-empty case Track L3 recorded as unlikely.
        /// </summary>
        [Fact]
        public void An_empty_serial_is_neither_asked_nor_owed()
        {
            var ledger = new SmartLinkRegistrationCheckLedger();
            Assert.False(ledger.TryBegin(string.Empty));
            Assert.False(ledger.ACheckIsOwedTo(string.Empty));
            Assert.False(ledger.ACheckIsOwedTo(null));
            Assert.False(ledger.InFlight);
        }
    }
}
