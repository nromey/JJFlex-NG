using System;
using System.Collections.Generic;
using Radios.ChainChecks;
using Xunit;
using static Radios.ChainChecks.TxTuneProbe;

namespace Radios.Tests
{
    /// <summary>
    /// May the probe key the transmitter?
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the checks that stand between a diagnostic and an
    /// unannounced transmission, so they are exercised exhaustively — every
    /// combination of the four inputs, sixteen cases, no sampling.
    /// </para>
    /// <para>
    /// <b>They exist as a pure function because the runner could only ever be
    /// tested down its first branch.</b> With no radio you can never reach the
    /// load-declaration gate, which meant the gate that matters MOST was the
    /// one nothing could exercise. Writing this file is what surfaced that;
    /// the design changed rather than the test being written around it.
    /// </para>
    /// </remarks>
    public class TxTuneProbePreconditionTests
    {
        // ---- the happy case, and it is the only one ----

        [Fact]
        public void Everything_in_order_permits_the_run()
        {
            Assert.Equal(SkipReason.None,
                CheckPreconditions(haveRadio: true, loadDeclared: true,
                                   alreadyTransmitting: false, cancelled: false));
        }

        [Fact]
        public void Exactly_one_of_the_sixteen_combinations_permits_transmitting()
        {
            // A guard that permits more cases than intended is the failure
            // mode here, and it is invisible unless counted. Enumerate the
            // whole space rather than trusting the four tests above to cover
            // it — they check the cases someone thought of.
            int permitted = 0;
            foreach (bool radio in new[] { true, false })
            foreach (bool load in new[] { true, false })
            foreach (bool txing in new[] { true, false })
            foreach (bool cancelled in new[] { true, false })
                if (CheckPreconditions(radio, load, txing, cancelled) == SkipReason.None)
                    permitted++;

            Assert.Equal(1, permitted);
        }

        // ---- each refusal, in isolation ----

        [Fact]
        public void No_radio_refuses()
        {
            Assert.Equal(SkipReason.RadioNotReachable,
                CheckPreconditions(haveRadio: false, loadDeclared: true,
                                   alreadyTransmitting: false, cancelled: false));
        }

        [Fact]
        public void An_undeclared_load_refuses_even_with_everything_else_ready()
        {
            // #180. This is the gate the runner could not previously reach in
            // a test, and the one whose absence would matter most: it is what
            // stops the app transmitting into something nobody has identified.
            Assert.Equal(SkipReason.LoadNotDeclared,
                CheckPreconditions(haveRadio: true, loadDeclared: false,
                                   alreadyTransmitting: false, cancelled: false));
        }

        [Fact]
        public void Already_transmitting_refuses()
        {
            // Keying on top of an existing transmission would measure that,
            // not this.
            Assert.Equal(SkipReason.AlreadyTransmitting,
                CheckPreconditions(haveRadio: true, loadDeclared: true,
                                   alreadyTransmitting: true, cancelled: false));
        }

        [Fact]
        public void Cancellation_refuses()
        {
            Assert.Equal(SkipReason.Cancelled,
                CheckPreconditions(haveRadio: true, loadDeclared: true,
                                   alreadyTransmitting: false, cancelled: true));
        }

        // ---- precedence, where more than one thing is wrong ----

        [Fact]
        public void Cancellation_wins_over_everything()
        {
            // If the operator has already backed out, telling them about the
            // radio is answering a question nobody is asking any more.
            Assert.Equal(SkipReason.Cancelled,
                CheckPreconditions(haveRadio: false, loadDeclared: false,
                                   alreadyTransmitting: true, cancelled: true));
        }

        [Fact]
        public void With_no_radio_the_missing_radio_is_reported_not_the_load()
        {
            // Without a radio nothing else is even knowable — we cannot ask
            // whether it is transmitting — so leading with the load would be
            // reporting a policy detail while the fundamental problem goes
            // unmentioned.
            Assert.Equal(SkipReason.RadioNotReachable,
                CheckPreconditions(haveRadio: false, loadDeclared: false,
                                   alreadyTransmitting: false, cancelled: false));
        }

        [Fact]
        public void The_load_gate_outranks_already_transmitting()
        {
            // Both refuse, so the outcome is the same either way — but the
            // operator is told the one that is theirs to fix and that will
            // still be true next time.
            Assert.Equal(SkipReason.LoadNotDeclared,
                CheckPreconditions(haveRadio: true, loadDeclared: false,
                                   alreadyTransmitting: true, cancelled: false));
        }

        // ---- the property that actually matters ----

        [Fact]
        public void An_undeclared_load_never_permits_a_run_whatever_else_is_true()
        {
            // The invariant, stated as an invariant rather than as three
            // examples. If this ever fails, the app can transmit into
            // something nobody identified, and no other test in the suite
            // would necessarily notice.
            foreach (bool radio in new[] { true, false })
            foreach (bool txing in new[] { true, false })
            foreach (bool cancelled in new[] { true, false })
            {
                SkipReason r = CheckPreconditions(radio, loadDeclared: false,
                                                  alreadyTransmitting: txing,
                                                  cancelled: cancelled);
                Assert.NotEqual(SkipReason.None, r);
            }
        }

        [Fact]
        public void No_radio_never_permits_a_run_whatever_else_is_true()
        {
            foreach (bool load in new[] { true, false })
            foreach (bool txing in new[] { true, false })
            foreach (bool cancelled in new[] { true, false })
            {
                SkipReason r = CheckPreconditions(haveRadio: false, loadDeclared: load,
                                                  alreadyTransmitting: txing,
                                                  cancelled: cancelled);
                Assert.NotEqual(SkipReason.None, r);
            }
        }

        [Fact]
        public void Every_refusal_it_can_return_has_operator_facing_wording()
        {
            // A reason with no explanation is a dead end for whoever hit it.
            var produced = new HashSet<SkipReason>();
            foreach (bool radio in new[] { true, false })
            foreach (bool load in new[] { true, false })
            foreach (bool txing in new[] { true, false })
            foreach (bool cancelled in new[] { true, false })
                produced.Add(CheckPreconditions(radio, load, txing, cancelled));

            produced.Remove(SkipReason.None);
            Assert.NotEmpty(produced);
            foreach (SkipReason r in produced)
                Assert.False(string.IsNullOrWhiteSpace(ExplainSkip(r)),
                             "no wording for " + r);
        }
    }
}
