using System;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 36 Track E, task #284: a leg that connects is not a leg that
    /// worked.
    ///
    /// <para><b>The loop this file exists to keep broken.</b> The connect walk
    /// used to write <c>"connected"</c> into the per-radio ring the moment
    /// <c>ReconnectRemote</c> returned true — which is up to a minute before
    /// anyone knows whether the radio opened. On 2026-08-26 four consecutive
    /// SmartLink attempts to a radio at 192.168.50.100 died in the
    /// station-name wait, and all four were written into
    /// <c>4925-1213-8600-6245\connect-history.json</c> as successes, with
    /// durations of 341, 1334, 350 and 913 ms — each one matching a
    /// <c>ReconnectRemote: END connected=True</c> line in the trace.</para>
    ///
    /// <para>Three successes in a row is a trend, so
    /// <see cref="ConnectPathPolicy"/> then recommended SmartLink for the next
    /// attempt, which failed the same way and reinforced it again. Every
    /// failure made the next failure more likely, and the store showed an
    /// unbroken run of success while the operator was reaching for
    /// Alt+F4.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectOutcomeRecordingTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ConnectOutcomeRecordingTests));

        public void Dispose()
        {
            ConnectionHistory.DiscardPendingOutcome();
            _scope.Dispose();
        }

        private const string Serial = "4925-1213-8600-6245";

        [Fact]
        public void ArmingRecordsNothingYet()
        {
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 913);

            Assert.True(ConnectionHistory.HasPendingOutcome);
            Assert.Empty(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void AnOpenThatSucceededRecordsASuccess()
        {
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.Local.ToString(), 106);
            ConnectionHistory.CommitPendingOutcome(opened: true);

            var ring = ConnectionHistory.Load(Serial);
            var only = Assert.Single(ring);
            Assert.Equal(ConnectPathKind.Local.ToString(), only.Path);
            Assert.Equal(ConnectPathPolicy.ConnectedOutcome, only.Outcome);
            Assert.Equal(106, only.DurationMs);
            Assert.False(ConnectionHistory.HasPendingOutcome);
        }

        [Fact]
        public void AnOpenThatFailedIsNotRecordedAsASuccess()
        {
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 913);
            ConnectionHistory.CommitPendingOutcome(opened: false);

            var only = Assert.Single(ConnectionHistory.Load(Serial));
            Assert.Equal(ConnectPathPolicy.OpenFailedOutcome, only.Outcome);
            Assert.NotEqual(ConnectPathPolicy.ConnectedOutcome, only.Outcome);

            // And the attempt is still THERE, with its duration — the ring is a
            // support tool as well as a policy input, and "how long did that
            // take before it fell over" is exactly what someone asks next.
            Assert.Equal(913, only.DurationMs);
            Assert.Equal(ConnectPathKind.SmartLink.ToString(), only.Path);
        }

        [Fact]
        public void FourFailedOpensDoNotTeachTheAppToPreferThatPath()
        {
            // The 2026-08-26 evening, replayed. Under the old behaviour this
            // ring held four "connected" entries and the policy came back
            // SmartLink, which is how the next attempt was steered onto the
            // path that had just failed four times running.
            foreach (var ms in new long[] { 341, 1334, 350, 913 })
            {
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), ms);
                ConnectionHistory.CommitPendingOutcome(opened: false);
            }

            Assert.Equal(4, ConnectionHistory.Load(Serial).Count);
            Assert.Null(ConnectPathPolicy.LearnForRadio(Serial));
        }

        [Fact]
        public void AWalkThatFailsRemotelyAndThenOpensLocallyTeachesLocal()
        {
            // What the fixed walk actually writes: the SmartLink leg connected
            // and did not open, the Local leg connected and did open. The
            // second is the one that produced a working radio, and it is the
            // one the ring should be teaching from.
            for (int i = 0; i < 3; i++)
            {
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 900);
                ConnectionHistory.CommitPendingOutcome(opened: false);
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.Local.ToString(), 80);
                ConnectionHistory.CommitPendingOutcome(opened: true);
            }

            Assert.Equal(ConnectPathKind.Local, ConnectPathPolicy.LearnForRadio(Serial));
        }

        [Fact]
        public void AnOpenFailureDoesNotBreakARunOfGenuineSuccesses()
        {
            // open_failed is not "connected", so it cannot teach. It is also
            // not trend-breaking, which keeps it consistent with how a failed
            // leg has always been treated — the rule that lets a genuinely
            // remote radio learn anything at all.
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 900);
            ConnectionHistory.CommitPendingOutcome(opened: false);
            for (int i = 0; i < 3; i++)
            {
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 900);
                ConnectionHistory.CommitPendingOutcome(opened: true);
            }

            Assert.Equal(ConnectPathKind.SmartLink, ConnectPathPolicy.LearnForRadio(Serial));
        }

        [Fact]
        public void DiscardingLeavesNothingBehind()
        {
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 913);
            ConnectionHistory.DiscardPendingOutcome();

            Assert.False(ConnectionHistory.HasPendingOutcome);
            Assert.Empty(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void CommittingTwiceRecordsOnce()
        {
            // openTheRadio commits, and a resumed walk commits per leg. The
            // second call must be a no-op rather than a duplicate.
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.Local.ToString(), 60);
            ConnectionHistory.CommitPendingOutcome(opened: true);
            ConnectionHistory.CommitPendingOutcome(opened: false);

            Assert.Single(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void CommittingWithNothingArmedRecordsNothing()
        {
            // Auto-connect reaches the open without arming anything.
            ConnectionHistory.CommitPendingOutcome(opened: false);
            Assert.Empty(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void ALegLostBetweenArmAndCommitTeachesNothingRatherThanTheWrongThing()
        {
            // A crash, or a session that never resolved. Losing the record is
            // the right way to lose it: a missing attempt teaches the policy
            // nothing, where a false success teaches it the opposite of the
            // truth.
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 913);
            ConnectionHistory.DiscardPendingOutcome();

            Assert.Null(ConnectPathPolicy.LearnForRadio(Serial));
            Assert.Empty(ConnectionHistory.Load(Serial).Where(
                r => r.Outcome == ConnectPathPolicy.ConnectedOutcome));
        }
    }
}
