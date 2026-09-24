using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The application's own notion of "one connection", which is what replaced
    /// Track H2's sixty-second same-object window.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the window had to go.</b> H2 deduplicated repeat removal
    /// notices by comparing the <c>Radio</c> object and letting the comparison
    /// lapse after a minute, because nothing had verified whether FlexLib hands
    /// back a new object after a reconnect. Astra read the vendor source and
    /// settled it: <b>a fresh object per reconnect is NOT a guaranteed
    /// contract.</b> <c>Radio.Disconnect</c> clears transport and session state
    /// and calls <c>API.RemoveRadio</c> without destroying the object;
    /// <c>Radio.Connect</c> reconnects that same instance; and
    /// <c>FlexBase.RetryConnect</c> deliberately reuses <c>theRadio</c>. So the
    /// bound was covering a real ambiguity rather than a knowable fact, and a
    /// timer is not an identity either way.</para>
    ///
    /// <para><b>Astra's warning, which these tests are written against:</b>
    /// <i>"a mock that always supplies a new object is not a test of that
    /// lifetime contract."</i> So the reconnect cases below are exercised BOTH
    /// ways — a reconnect that acquires a fresh object, and a reconnect handed
    /// back the same one.</para>
    /// </remarks>
    // The suite runs sequentially by assembly policy (TestParallelism.cs),
    // which is what makes the process-global roster safe to drive here.
    public sealed class ConnectionLifetimeTests : IDisposable
    {
        public ConnectionLifetimeTests() => ConnectionLifetime.ResetForTests();
        public void Dispose() => ConnectionLifetime.ResetForTests();

        /// <summary>A stand-in for the FlexLib <c>Radio</c> object. Only its
        /// identity matters, which is the point.</summary>
        private sealed class Rig
        {
            public Rig(string serial) { Serial = serial; }
            public string Serial { get; }
        }

        // ── One claim per connection ───────────────────────────────────────

        [Fact]
        public void A_connection_s_loss_is_claimed_exactly_once()
        {
            var rig = new Rig("1234");
            ConnectionLifetime.Token token = ConnectionLifetime.Bind(rig, "1234", out var outcome);
            Assert.Equal(ConnectionBindOutcome.Bound, outcome);

            Assert.True(ConnectionLifetime.TryClaimLoss(token));
            Assert.False(ConnectionLifetime.TryClaimLoss(token));
            Assert.False(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.TokenFor(rig)));
        }

        /// <summary>
        /// <b>No elapsed time appears anywhere.</b> H2's guard lapsed after a
        /// minute, so a delayed duplicate sealed a fresh log and announced a
        /// second drop — and its <c>Environment.TickCount</c> subtraction could
        /// not enforce a true minute across a signed half-wrap anyway. A
        /// terminal claim needs no clock, which is why this test does not sleep.
        /// </summary>
        [Fact]
        public void The_claim_never_lapses_with_time()
        {
            var rig = new Rig("1234");
            ConnectionLifetime.Token token = ConnectionLifetime.Bind(rig, "1234", out _);
            Assert.True(ConnectionLifetime.TryClaimLoss(token));

            // However long the evening is, this object's loss is spent. There is
            // nothing to wait for.
            Assert.False(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.TokenFor(rig)));
            Assert.True(ConnectionLifetime.IsRetired(rig));
        }

        /// <summary>
        /// A, B, delayed-A. Claims are kept PER TOKEN, not as one process-global
        /// "most recent object" — which B's arrival would otherwise have reset,
        /// letting A's late duplicate look new again.
        /// </summary>
        [Fact]
        public void A_then_B_then_a_delayed_A_cannot_evade_the_claim()
        {
            var a = new Rig("aaaa");
            var b = new Rig("bbbb");

            ConnectionLifetime.Token ta = ConnectionLifetime.Bind(a, "aaaa", out _);
            Assert.True(ConnectionLifetime.TryClaimLoss(ta));

            ConnectionLifetime.Token tb = ConnectionLifetime.Bind(b, "bbbb", out _);
            Assert.True(ConnectionLifetime.TryClaimLoss(tb));

            // A's second notice, arriving after B's whole drop.
            Assert.False(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.TokenFor(a)));
        }

        // ── Reconnects, both ways ──────────────────────────────────────────

        /// <summary>
        /// The positive control. A reconnect that really does acquire a fresh
        /// object is a NEW lifetime, and its loss can be claimed — otherwise the
        /// rule would silently suppress every second drop of an evening.
        /// </summary>
        [Fact]
        public void A_reconnect_that_acquires_a_fresh_object_gets_a_new_lifetime()
        {
            var first = new Rig("1234");
            ConnectionLifetime.Token t1 = ConnectionLifetime.Bind(first, "1234", out var o1);
            Assert.Equal(ConnectionBindOutcome.Bound, o1);
            Assert.True(ConnectionLifetime.TryClaimLoss(t1));

            // LAN rediscovery constructs a new Radio per discovery packet, so
            // this is the ordinary case.
            var second = new Rig("1234");
            ConnectionLifetime.Token t2 = ConnectionLifetime.Bind(second, "1234", out var o2);

            Assert.Equal(ConnectionBindOutcome.Bound, o2);
            Assert.NotSame(t1, t2);
            Assert.False(t2.Retired);
            Assert.True(ConnectionLifetime.TryClaimLoss(t2));

            // Same serial, different connection. Identity is never inferred
            // from a matching serial.
            Assert.NotEqual(t1.Ordinal, t2.Ordinal);
        }

        /// <summary>
        /// <b>The rule, stated as a test.</b> A terminally retired object is
        /// never rebound as a new connection: it keeps resolving to the retired
        /// token, so old-object callbacks cannot be mistaken for a new loss.
        /// </summary>
        [Fact]
        public void A_terminally_retired_object_is_never_rebound()
        {
            var rig = new Rig("1234");
            ConnectionLifetime.Token first = ConnectionLifetime.Bind(rig, "1234", out _);
            Assert.True(ConnectionLifetime.TryClaimLoss(first));

            // The SmartLink path can hand back the very object that just died:
            // its handle bank is not cleared by a drop.
            ConnectionLifetime.Token rebound = ConnectionLifetime.Bind(rig, "1234", out var outcome);

            Assert.Equal(ConnectionBindOutcome.RetiredObjectRebound, outcome);
            Assert.Same(first, rebound);
            Assert.True(rebound.Retired);

            // AND THE CONSEQUENCE THAT NEEDS A RULING: a genuine second loss of
            // this reused object produces no claim, so no seal and no notice.
            // Pinned deliberately, so the behaviour is visible and a ruling can
            // change one line rather than discovering this at a bench.
            Assert.False(ConnectionLifetime.TryClaimLoss(rebound));
        }

        /// <summary>
        /// A retry that has NOT crossed a terminal retirement stays inside its
        /// original lifetime. <c>RetryConnect</c> only runs while a connect
        /// attempt is still in progress, so it is exactly this case — and its
        /// loss can still be claimed once.
        /// </summary>
        [Fact]
        public void A_retry_inside_the_same_lifetime_keeps_its_token_and_its_claim()
        {
            var rig = new Rig("1234");
            ConnectionLifetime.Token first = ConnectionLifetime.Bind(rig, "1234", out _);

            ConnectionLifetime.Token retried = ConnectionLifetime.Bind(rig, "1234", out var outcome);
            Assert.Equal(ConnectionBindOutcome.SameLifetime, outcome);
            Assert.Same(first, retried);
            Assert.False(retried.Retired);

            Assert.True(ConnectionLifetime.TryClaimLoss(retried));
        }

        /// <summary>
        /// <b>A deliberate disconnect leaves the lifetime alone, and that is
        /// the fix rather than an omission.</b>
        ///
        /// <para>The first shape of this retired the lifetime on a hang-up,
        /// reasoning that a hang-up ends a connection. It does — but "retired"
        /// here means terminally retired by a claimed LOSS, and it carries the
        /// rule that the object is never rebound. Applying it to a hang-up made
        /// the unresolved SmartLink case reachable by an ordinary sequence:
        /// disconnect, reconnect over SmartLink, radio dies — and the drop
        /// would not have sealed, silently losing the evidence this bridge
        /// exists to produce.</para>
        ///
        /// <para>Nothing is needed there anyway: a self-initiated removal never
        /// reaches the seal, because <c>RemovalSealsTheCapture</c> answers only
        /// for <c>ConnectionLostOurRadio</c>. So the lifetime spans the hang-up
        /// and the reconnect, unclaimed, and a genuine later drop still claims
        /// it exactly once.</para>
        /// </summary>
        [Fact]
        public void A_hang_up_and_reconnect_on_the_same_object_can_still_report_a_real_drop()
        {
            var rig = new Rig("1234");
            ConnectionLifetime.Token first = ConnectionLifetime.Bind(rig, "1234", out _);

            // The operator disconnects. Nothing touches the lifetime — the
            // classification, not the lifetime, is what keeps a hang-up from
            // being announced as a drop.
            Assert.False(first.Retired);
            Assert.False(first.LossClaimed);

            // They reconnect, and SmartLink hands back the very same object.
            ConnectionLifetime.Token again = ConnectionLifetime.Bind(rig, "1234", out var outcome);
            Assert.Equal(ConnectionBindOutcome.SameLifetime, outcome);
            Assert.Same(first, again);

            // Now the radio really dies. This must seal.
            Assert.True(ConnectionLifetime.TryClaimLoss(again));

            // And exactly once.
            Assert.False(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.TokenFor(rig)));
        }

        [Fact]
        public void An_object_this_process_never_bound_has_no_token()
        {
            Assert.Null(ConnectionLifetime.TokenFor(new Rig("never")));
            Assert.Null(ConnectionLifetime.TokenFor(null));
            Assert.False(ConnectionLifetime.IsRetired(new Rig("never")));
        }

        // ── The acquisition paths, pinned at their call sites ──────────────

        /// <summary>
        /// A lifetime nothing binds is a lifetime that is wrong for free. This
        /// pins the two acquisition paths, because the rule is only as good as
        /// the place that enforces it — and a later author moving the bind out
        /// of <c>Connect</c> would leave every removal callback resolving to a
        /// freshly minted token, which is exactly the ambiguity this replaced.
        /// </summary>
        [Fact]
        public void The_acquisition_paths_bind_a_lifetime()
        {
            string source = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));

            // Connect, immediately after theRadio is resolved.
            int acquire = source.IndexOf("theRadio = findRadioForConnect(", StringComparison.Ordinal);
            Assert.True(acquire > 0, "the acquisition point has moved");
            int bind = source.IndexOf("ConnectionLifetime.Bind(", acquire, StringComparison.Ordinal);
            Assert.True(bind > acquire, "Connect does not bind a connection lifetime");

            // RetryConnect, which reuses theRadio and therefore has to say so.
            int retry = source.IndexOf("public bool RetryConnect()", StringComparison.Ordinal);
            Assert.True(retry > 0);
            int retryBind = source.IndexOf("ConnectionLifetime.Bind(", retry, StringComparison.Ordinal);
            Assert.True(retryBind > retry && retryBind < retry + 2000,
                        "RetryConnect does not declare which lifetime it is retrying inside");

            // And the operator's own disconnect touches the lifetime not at
            // all. Retiring there would make a hang-up look like a terminal
            // loss to the one acquisition path that can hand back the same
            // object, and the next real drop would go unsealed.
            int selfArm = source.IndexOf("case RadioRemovalKind.SelfInitiated:", StringComparison.Ordinal);
            int dropArm = source.IndexOf("case RadioRemovalKind.ConnectionLostOurRadio:", StringComparison.Ordinal);
            string selfBody = source.Substring(selfArm, dropArm - selfArm);
            Assert.DoesNotContain("ConnectionLifetime.Retire", selfBody, StringComparison.Ordinal);
            Assert.DoesNotContain("TryClaimLoss", selfBody, StringComparison.Ordinal);
        }
    }
}
