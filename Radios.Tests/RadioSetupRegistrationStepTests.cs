#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Radios;
using Xunit;

using Verdict = Radios.FlexBase.SmartLinkRegistrationQuery;
using Evidence = Radios.SmartLinkRegistrationEvidence;

namespace Radios.Tests
{
    /// <summary>
    /// Radio Setup's step 2 says what the application already knows (the
    /// junk box batch 6 bench run, 2026-11-11, on #691's step-2 lines).
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> On the 8600 over the local network, step 2
    /// read "Ready to register to the account. Checking with SmartLink whether
    /// it already is... Not started. JJ Flexible Radio Access cannot tell from
    /// here whether this radio is already registered", at once and again nine
    /// seconds later, while the trace said twice that the radio was in a list
    /// already held for that account, registered, no round trip needed. A
    /// held list's answer comes back as a task that has already completed;
    /// the dialog drew "Checking" first and started the check after, and the
    /// redraw the answer asks for is skipped while Settings is not yet shown,
    /// which is when it assigns its radio.</para>
    ///
    /// <para><b>These drive the order, not the source text.</b> The held list
    /// is a real <see cref="Radios.SmartLink.WanSessionOwner"/> behind a mock
    /// server, judged by the same <see cref="Evidence.HeldLists"/> and
    /// <see cref="Evidence.Judge"/> the query uses, and handed to the step the
    /// way <see cref="FlexBase.QuerySmartLinkRegistrationAsync"/> hands it: as
    /// a completed task. What is NOT driven here is FlexBase's own account
    /// resolution in front of that branch: it reads the per-radio settings
    /// and the SmartLink coordinator singleton, which belong to two different
    /// test collections, so one class cannot hold both. The bench trace is
    /// the evidence that production takes this branch; the source pin below
    /// holds the shape of what it returns.</para>
    ///
    /// <para>In the settings collection because every line is read through
    /// <see cref="Lexicon"/>, whose overlay folder that collection owns.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioSetupRegistrationStepTests : IDisposable
    {
        private const string Serial = "4321-8765-6600-1234";
        private const string Account = "owner@example.com";        // the account whose held list carries the radio
        private const string Preflight = "operator@example.com";   // the account registration would go to
        private const string RadioState = "Not started.";

        private readonly RadioConfigStaticsScope _scope = new(nameof(RadioSetupRegistrationStepTests));

        public void Dispose() => _scope.Dispose();

        private static void WaitUntil(Func<bool> condition, string because) =>
            Assert.True(System.Threading.SpinWait.SpinUntil(condition, 5000), because);

        /// <summary>
        /// A held session for <see cref="Account"/>, connected, whose list on
        /// its live connection carries <see cref="Serial"/>, judged exactly as
        /// the query's held-list branch judges it.
        /// </summary>
        private static Evidence.Finding HeldListAnswer()
        {
            var wan = new MockWanServer();
            var owner = new Radios.SmartLink.WanSessionOwner(
                Guid.NewGuid().ToString("N").Substring(0, 12), Account, wan,
                new Radios.SmartLink.DirectPassthroughSink(), new[] { 50, 50, 50 });
            try
            {
                owner.Connect();
                WaitUntil(() => owner.IsConnected, "the mock session never reported connected");
                wan.RaiseWanRadioRadioListReceived(new[] { SmartLinkRegistrationReplayTests.WanRadio(Serial) });
                WaitUntil(() => owner.RadioListSnapshot.ArrivedOnTheLiveConnection,
                    "the list never registered as arrived on the live connection");

                var lists = Evidence.HeldLists(new[] { owner });
                Assert.Equal(Evidence.ListSource.HeldByAConnectedSession, lists.Single().Source);
                return Evidence.Judge(Serial, arrivedOverSmartLink: false, anySavedAccount: true,
                    anAccountIsInHand: true, listsInHand: lists);
            }
            finally
            {
                owner.Dispose();
            }
        }

        private static string Registered(string account) =>
            Lexicon.Get("settings.radio.register.already_registered", ("accountEmail", account));

        private static string Checking() =>
            Lexicon.Get("settings.radio.register.checking",
                ("accountEmail", Preflight), ("state", RadioState));

        private static string Unanswered() =>
            Lexicon.Get("settings.radio.register.check_unanswered", ("accountEmail", Preflight));

        /// <summary>
        /// The finding: a radio in a list a session already holds reads as
        /// registered, to the account that listed it, the moment the step is
        /// drawn. Red with the line chosen before the check starts, which is
        /// the order the dialog had.
        /// </summary>
        [Fact]
        public void A_radio_already_in_a_held_list_reads_as_registered_when_the_step_is_drawn()
        {
            var answer = HeldListAnswer();

            // Positive controls: the held list really answers, under the
            // account that listed it, and the answer really arrives already
            // completed. Without these, the line below could be right for a
            // reason that has nothing to do with the bench run.
            Assert.Equal(Verdict.Registered, answer.Verdict);
            Assert.Equal(Account, answer.ListedUnderAccount);
            Func<Task<Evidence.Finding>> query = () => Task.FromResult(answer);
            Assert.True(query().IsCompleted);

            var step = new RadioSetupRegistrationStep();
            int redraws = 0;
            string line = step.Refresh(Serial, Preflight, RadioState, startACheck: true, query, () => redraws++);

            Assert.Equal(Registered(Account), line);
            Assert.NotEqual(Checking(), line);
            // Nothing is left for a later redraw to repair: the answer is
            // already in the line, and no check is running.
            Assert.Equal(0, redraws);
            Assert.False(step.Ledger.InFlight);
            // And the next ordinary refresh starts nothing and says the same.
            Assert.Equal(Registered(Account),
                step.Refresh(Serial, Preflight, RadioState, startACheck: true,
                    () => throw new InvalidOperationException("an answered radio was asked again"), () => redraws++));
        }

        /// <summary>
        /// Why the held list's answer arrives already completed: the query's
        /// held-list branch returns it through <c>Task.FromResult</c>, with no
        /// round trip. If that ever changes, the test above stops describing
        /// production, and this says so.
        /// </summary>
        [Fact]
        public void The_held_list_branch_answers_with_a_completed_task()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "Radios", "FlexBase.SmartLink.cs"));
            int query = src.IndexOf("public Task<SmartLinkRegistrationEvidence.Finding> QuerySmartLinkRegistrationAsync()", StringComparison.Ordinal);
            Assert.True(query >= 0, "QuerySmartLinkRegistrationAsync is not where this test looks for it.");
            int held = src.IndexOf("is in a list already held", query, StringComparison.Ordinal);
            int network = src.IndexOf("return Task.Run(", query, StringComparison.Ordinal);
            Assert.True(held > query && network > held,
                "The held-list branch is not before the round trip, where this test looks for it.");
            Assert.Contains("return Task.FromResult(fromCache);", src.Substring(held, network - held), StringComparison.Ordinal);
        }

        /// <summary>
        /// The control for the test above: a check that is still running
        /// reads as checking, and when it lands the caller is asked to redraw
        /// once, and the redraw reads the answer. So the checking line is
        /// reachable, and the registered line above is not the only thing the
        /// step can say.
        /// </summary>
        [Fact]
        public void A_check_still_running_reads_as_checking_and_the_answer_shows_on_the_redraw()
        {
            var answer = HeldListAnswer();
            var pending = new TaskCompletionSource<Evidence.Finding>();

            var step = new RadioSetupRegistrationStep();
            int redraws = 0;
            string line = step.Refresh(Serial, Preflight, RadioState, startACheck: true,
                () => pending.Task, () => redraws++);

            Assert.Equal(Checking(), line);
            Assert.True(step.Ledger.InFlight);
            Assert.Equal(0, redraws);

            pending.SetResult(answer);

            Assert.Equal(1, redraws);
            Assert.False(step.Ledger.InFlight);
            Assert.Equal(Registered(Account),
                step.Refresh(Serial, Preflight, RadioState, startACheck: false, () => pending.Task, () => redraws++));
        }

        /// <summary>
        /// The same order holds for a check that ends at once with nothing
        /// established: it reads as Noel's ruled line (2026-10-02, kept as
        /// built 2026-11-11), not as checking with nothing running.
        /// </summary>
        [Fact]
        public void A_check_that_ends_at_once_without_an_answer_reads_as_ready_to_register()
        {
            var step = new RadioSetupRegistrationStep();
            int redraws = 0;
            string line = step.Refresh(Serial, Preflight, RadioState, startACheck: true,
                () => Task.FromResult(Evidence.Finding.Unknown()), () => redraws++);

            Assert.Equal(Unanswered(), line);
            Assert.Equal("Ready to register radio to " + Preflight + ".", line);
            Assert.Equal(0, redraws);
        }

        /// <summary>
        /// A check that throws before it returns has finished too, without an
        /// answer, and the step says so rather than checking.
        /// </summary>
        [Fact]
        public void A_check_that_throws_reads_as_ready_to_register()
        {
            var step = new RadioSetupRegistrationStep();
            string line = step.Refresh(Serial, Preflight, RadioState, startACheck: true,
                () => throw new InvalidOperationException("the check failed before it started"), () => { });

            Assert.Equal(Unanswered(), line);
            Assert.False(step.Ledger.InFlight);
        }

        /// <summary>
        /// An unanswered check is not pinned: a later refresh asks again, and
        /// while that new check runs the step reads as checking. This is the
        /// case the dialog's old "not starting a check" condition guarded;
        /// now the check has started before the line is chosen, so "is one
        /// running" answers it.
        /// </summary>
        [Fact]
        public void Asking_again_after_an_unanswered_check_reads_as_checking_until_it_lands()
        {
            var step = new RadioSetupRegistrationStep();
            int redraws = 0;
            var first = new TaskCompletionSource<Evidence.Finding>();
            Assert.Equal(Checking(), step.Refresh(Serial, Preflight, RadioState, startACheck: true,
                () => first.Task, () => redraws++));
            first.SetResult(Evidence.Finding.Unknown());
            Assert.Equal(1, redraws);

            // The completion's redraw: nothing owed, nothing started.
            Assert.False(step.Ledger.ACheckIsOwedTo(Serial));
            Assert.Equal(Unanswered(), step.Refresh(Serial, Preflight, RadioState, startACheck: false,
                () => throw new InvalidOperationException("the completion's redraw started a check"), () => redraws++));

            // Refresh all steps: asks again, and says so while it runs.
            var second = new TaskCompletionSource<Evidence.Finding>();
            Assert.Equal(Checking(), step.Refresh(Serial, Preflight, RadioState, startACheck: true,
                () => second.Task, () => redraws++));
            second.SetResult(HeldListAnswer());
            Assert.Equal(2, redraws);
            Assert.Equal(Registered(Account), step.LineFor(Serial, Preflight, RadioState));
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }
    }
}
