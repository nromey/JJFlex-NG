using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    public sealed class ConnectionLifetimeTests : IDisposable
    {
        public ConnectionLifetimeTests() => ConnectionLifetime.ResetForTests();
        public void Dispose() => ConnectionLifetime.ResetForTests();

        [Fact]
        public void A_connections_loss_is_claimed_exactly_once()
        {
            var identity = new object();
            var token = ConnectionLifetime.Bind(identity, "first", out var outcome);
            Assert.Equal(ConnectionBindOutcome.Bound, outcome);
            Assert.True(ConnectionLifetime.TryClaimLoss(token));
            Assert.False(ConnectionLifetime.TryClaimLoss(token));
            Assert.False(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.TokenFor(identity)));
        }

        [Fact]
        public void A_reachable_claim_never_lapses_or_gets_evicted_by_new_connections()
        {
            var identity = new object();
            var first = ConnectionLifetime.Bind(identity, "first", out _);
            Assert.True(ConnectionLifetime.TryClaimLoss(first));
            for (int i = 0; i < 300; ++i)
                ConnectionLifetime.Bind(new object(), "later", out _);
            Assert.Same(first, ConnectionLifetime.TokenFor(identity));
            Assert.False(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.TokenFor(identity)));
            Assert.True(ConnectionLifetime.IsRetired(identity));
        }

        [Fact]
        public void A_then_B_then_delayed_A_cannot_evade_the_claim()
        {
            var a = new object();
            var b = new object();
            Assert.True(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.Bind(a, "A", out _)));
            Assert.True(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.Bind(b, "B", out _)));
            Assert.False(ConnectionLifetime.TryClaimLoss(ConnectionLifetime.TokenFor(a)));
        }

        [Fact]
        public void Reconnecting_the_same_Radio_gets_a_new_lifetime_from_its_new_producer()
        {
            var radio = OfflineCommandProducer.Radio();
            OfflineCommandProducer.StartTls(radio);
            var firstIdentity = radio.CurrentCommandConnection;
            var first = ConnectionLifetime.Bind(firstIdentity, radio.Serial, out _);
            Assert.True(ConnectionLifetime.TryClaimLoss(first));
            OfflineCommandProducer.Transport(radio).Disconnect();
            OfflineCommandProducer.StartTls(radio);
            var next = ConnectionLifetime.Bind(radio.CurrentCommandConnection, radio.Serial, out var outcome);
            Assert.Equal(ConnectionBindOutcome.Bound, outcome);
            Assert.NotEqual(ConnectionBindOutcome.RetiredObjectRebound, outcome);
            Assert.NotSame(first, next);
            Assert.True(ConnectionLifetime.TryClaimLoss(next));
            Assert.False(ConnectionLifetime.TryClaimLoss(first));
            Assert.Same(first, ConnectionLifetime.TokenFor(firstIdentity));
            OfflineCommandProducer.Transport(radio).Disconnect();
        }

        [Fact]
        public void Rebinding_the_same_producer_preserves_its_claim_even_after_loss()
        {
            var identity = new object();
            var first = ConnectionLifetime.Bind(identity, "first", out _);
            Assert.True(ConnectionLifetime.TryClaimLoss(first));
            var repeated = ConnectionLifetime.Bind(identity, "again", out var outcome);
            Assert.Equal(ConnectionBindOutcome.SameLifetime, outcome);
            Assert.Same(first, repeated);
            Assert.False(ConnectionLifetime.TryClaimLoss(repeated));
        }

        [Fact]
        public void A_retry_using_an_existing_transport_keeps_its_token()
        {
            var identity = new object();
            var first = ConnectionLifetime.Bind(identity, "first", out _);
            var again = ConnectionLifetime.Bind(identity, "retry", out var outcome);
            Assert.Equal(ConnectionBindOutcome.SameLifetime, outcome);
            Assert.Same(first, again);
            Assert.True(ConnectionLifetime.TryClaimLoss(again));
        }

        [Fact]
        public void A_hang_up_then_new_transport_does_not_inherit_the_old_claim()
        {
            var first = ConnectionLifetime.Bind(new object(), "same radio", out _);
            var next = ConnectionLifetime.Bind(new object(), "same radio", out var outcome);
            Assert.Equal(ConnectionBindOutcome.Bound, outcome);
            Assert.NotSame(first, next);
            Assert.False(first.LossClaimed);
            Assert.True(ConnectionLifetime.TryClaimLoss(next));
            Assert.False(ConnectionLifetime.TryClaimLoss(next));
        }

        [Fact]
        public void An_unbound_identity_has_no_token()
        {
            Assert.Null(ConnectionLifetime.TokenFor(new object()));
            Assert.Null(ConnectionLifetime.TokenFor(null));
            Assert.False(ConnectionLifetime.IsRetired(new object()));
        }

        [Fact]
        public void Production_binds_the_report_identity_and_passes_its_token_to_the_archive()
        {
            string source = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));
            Assert.Contains("ConnectionLifetime.Bind(report.Connection,", source);
            Assert.DoesNotContain("ConnectionLifetime.Bind(\r\n                theRadio,", source);
            Assert.Contains("token, r.Nickname", CaptureArchiveTests.ArchiveMethodBody(source));
            Assert.Contains("ReferenceEquals(report.Connection, binding.Connection)", source);
        }
    }
}
