#nullable enable

using System;
using System.Linq;
using System.Reflection;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// One SmartLink method attempt and its proof, on a clock the test holds
    /// (the tiers plan, section 4 and decision 10). No radio, no socket, no
    /// session: the attempt is handed its events as the radio layer hands them.
    /// </summary>
    public sealed class SmartLinkAttemptTests
    {
        private long _now = 1_000_000;
        private readonly object _connection = new();
        private readonly object _otherConnection = new();

        private SmartLinkAttempt Attempt(int floor = 4, SmartLinkProofRules? rules = null) =>
            new(connectId: 7, sequence: 1, SmartLinkMethod.ManualPorts, floor, () => _now, rules);

        /// <summary>An attempt whose handle arrived and whose transport is up on socket 5.</summary>
        private SmartLinkAttempt Up(int floor = 4, SmartLinkProofRules? rules = null)
        {
            var a = Attempt(floor, rules);
            a.SettleRequest(handleArrived: true);
            a.NoteTransportUp(_connection, 5);
            return a;
        }

        private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

        // ── Proof: both halves, in either order ─────────────────────────────

        [Fact]
        public void The_open_then_the_sighting_proves_it()
        {
            var a = Up();
            a.NoteOpenCompleted();
            Assert.Equal(SmartLinkAttemptEnd.Dialled, a.End);
            Assert.False(a.IsOver);

            Assert.Equal(SightingVerdict.Counted, a.NoteSighting(5, _connection, Ms(118), socketIsCurrent: true));

            Assert.True(a.IsProven);
            Assert.Equal(SmartLinkAttemptEnd.Proven, a.End);
            Assert.Equal(Ms(118), a.QualifiedUdpAfter);
        }

        [Fact]
        public void The_sighting_then_the_open_proves_it()
        {
            // The October trace: registration arrived 118 ms after the
            // connect, before the open completed.
            var a = Up();
            Assert.Equal(SightingVerdict.Counted, a.NoteSighting(5, _connection, Ms(118), true));
            Assert.False(a.IsOver);
            Assert.Equal(SmartLinkAttemptEnd.Dialled, a.Poll());

            a.NoteOpenCompleted();

            Assert.True(a.IsProven);
            Assert.Equal(Ms(118), a.QualifiedUdpAfter);
        }

        [Fact]
        public void The_open_alone_is_not_proof_and_the_sighting_alone_is_not_proof()
        {
            var opened = Up();
            opened.NoteOpenCompleted();
            _now += 14_999;
            Assert.Equal(SmartLinkAttemptEnd.Dialled, opened.Poll());

            var sighted = Up();
            sighted.NoteSighting(5, _connection, Ms(10), true);
            _now += 60_000;   // well past the window: the sighting is in, only the open is owed
            Assert.Equal(SmartLinkAttemptEnd.Dialled, sighted.Poll());
            Assert.False(sighted.IsOver);
        }

        // ── What never counts ───────────────────────────────────────────────

        [Fact]
        public void A_sighting_on_a_socket_made_before_this_attempt_never_counts()
        {
            var a = Up(floor: 4);
            a.NoteOpenCompleted();
            Assert.Equal(SightingVerdict.RefusedStaleSocket, a.NoteSighting(4, _connection, Ms(5), true));
            Assert.Equal(SightingVerdict.RefusedStaleSocket, a.NoteSighting(3, _connection, Ms(5), true));
            Assert.False(a.IsOver);
            Assert.Null(a.QualifiedUdpAfter);
        }

        [Fact]
        public void A_sighting_on_a_socket_that_is_no_longer_live_never_counts()
        {
            var a = Up();
            a.NoteOpenCompleted();
            Assert.Equal(SightingVerdict.RefusedStaleSocket, a.NoteSighting(5, _connection, Ms(5), socketIsCurrent: false));
            Assert.False(a.IsOver);
        }

        [Fact]
        public void A_sighting_on_a_newer_socket_than_this_attempts_never_counts()
        {
            var a = Up();
            a.NoteOpenCompleted();
            Assert.Equal(SightingVerdict.RefusedStaleSocket, a.NoteSighting(6, _connection, Ms(5), true));
            Assert.False(a.IsOver);
        }

        [Fact]
        public void A_sighting_under_another_command_connection_never_counts_and_a_later_right_one_still_does()
        {
            var a = Up();
            a.NoteOpenCompleted();
            Assert.Equal(SightingVerdict.RefusedOtherConnection, a.NoteSighting(5, _otherConnection, Ms(5), true));
            Assert.Equal(SightingVerdict.RefusedOtherConnection, a.NoteSighting(5, null, Ms(6), true));
            Assert.False(a.IsOver);

            // MIGRATION.md item 31 is what lets this one arrive at all.
            Assert.Equal(SightingVerdict.Counted, a.NoteSighting(5, _connection, Ms(900), true));
            Assert.True(a.IsProven);
            Assert.Equal(Ms(900), a.QualifiedUdpAfter);
        }

        [Fact]
        public void An_attempt_that_made_no_socket_of_its_own_can_never_be_proven()
        {
            // FlexLib's connect returned true but the radio's newest socket is
            // not newer than the floor: an earlier attempt's socket was reused.
            var a = Attempt(floor: 5);
            a.SettleRequest(true);
            a.NoteTransportUp(_connection, 5);
            a.NoteOpenCompleted();
            Assert.Equal(0, a.SocketGeneration);
            Assert.Equal(SightingVerdict.RefusedStaleSocket, a.NoteSighting(5, _connection, Ms(5), true));
            _now += 15_000;
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, a.Poll());
        }

        // ── The watch starts at socket creation ─────────────────────────────

        [Fact]
        public void A_sighting_before_the_transport_is_known_is_held_and_judged_when_it_is()
        {
            // A hole punch makes its socket before the command connection.
            var a = Attempt(floor: 4);
            a.SettleRequest(true);
            Assert.Equal(SightingVerdict.HeldUntilTransportIsKnown, a.NoteSighting(5, _otherConnection, Ms(3), true));
            Assert.Equal(SightingVerdict.HeldUntilTransportIsKnown, a.NoteSighting(5, _connection, Ms(40), true));

            a.NoteTransportUp(_connection, 5);
            a.NoteOpenCompleted();

            Assert.True(a.IsProven);
            Assert.Equal(Ms(40), a.QualifiedUdpAfter);   // the one under its own connection, not the earlier one
        }

        [Fact]
        public void A_held_sighting_under_the_wrong_connection_proves_nothing()
        {
            var a = Attempt(floor: 4);
            a.SettleRequest(true);
            a.NoteSighting(5, _otherConnection, Ms(3), true);
            a.NoteTransportUp(_connection, 5);
            a.NoteOpenCompleted();
            Assert.False(a.IsOver);
            _now += 15_000;
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, a.Poll());
        }

        // ── Failure ─────────────────────────────────────────────────────────

        [Fact]
        public void No_sighting_within_the_window_fails_it_and_not_a_moment_earlier()
        {
            var a = Up();
            a.NoteOpenCompleted();
            _now += SmartLinkProofRules.DefaultWindowMs - 1;
            Assert.Equal(SmartLinkAttemptEnd.Dialled, a.Poll());
            _now += 1;
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, a.Poll());
            Assert.True(a.HasFailed);
            Assert.Equal("No audio or data arrived from the radio within " + (SmartLinkProofRules.DefaultWindowMs / 1000) + " seconds after the command connection was established", a.EndDetail);
        }

        [Fact]
        public void The_window_counts_from_the_transport_coming_up_not_from_the_attempts_start()
        {
            var a = Attempt();
            a.SettleRequest(true);
            _now += 40_000;   // a slow TCP and TLS stage
            a.NoteTransportUp(_connection, 5);
            _now += 14_000;
            Assert.Equal(SmartLinkAttemptEnd.Dialled, a.Poll());
            _now += 1_000;
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, a.Poll());
        }

        [Fact]
        public void The_recorded_first_data_time_counts_from_the_socket_and_the_window_from_the_transport()
        {
            // Two clocks, on purpose. A hole punch makes its socket before the
            // TCP and TLS stage: here that stage took twenty seconds, and the
            // radio's data came one second after the transport was up. The
            // window (15 s from the transport) is not exceeded; the record's
            // time (from the socket, as its doc says) is twenty-one seconds.
            var a = Attempt();
            a.SettleRequest(true);
            _now += 20_000;
            a.NoteTransportUp(_connection, 5);
            _now += 1_000;
            Assert.Equal(SightingVerdict.Counted, a.NoteSighting(5, _connection, Ms(21_000), true));
            a.NoteOpenCompleted();
            Assert.True(a.IsProven);
            Assert.Equal(Ms(21_000), a.QualifiedUdpAfter);
            Assert.True(a.QualifiedUdpAfter!.Value.TotalMilliseconds > SmartLinkProofRules.DefaultWindowMs);
        }

        [Fact]
        public void Losing_its_own_command_connection_before_proof_fails_it()
        {
            var a = Up();
            Assert.True(a.NoteCommandConnectionLost(_connection));
            Assert.Equal(SmartLinkAttemptEnd.CommandConnectionLost, a.End);
            Assert.True(a.HasFailed);
        }

        [Fact]
        public void Another_connections_fall_is_not_this_attempts()
        {
            var a = Up();
            Assert.False(a.NoteCommandConnectionLost(_otherConnection));
            Assert.False(a.NoteCommandConnectionLost(null));
            Assert.False(a.IsOver);
        }

        [Fact]
        public void A_fall_reported_before_the_transport_is_known_is_judged_when_it_is()
        {
            var own = Attempt();
            own.SettleRequest(true);
            own.NoteCommandConnectionLost(_connection);
            own.NoteTransportUp(_connection, 5);
            Assert.Equal(SmartLinkAttemptEnd.CommandConnectionLost, own.End);

            var notOwn = Attempt();
            notOwn.SettleRequest(true);
            notOwn.NoteCommandConnectionLost(_otherConnection);   // an older connection's late fall
            notOwn.NoteTransportUp(_connection, 5);
            Assert.False(notOwn.IsOver);
        }

        [Fact]
        public void The_method_budget_ends_an_open_that_never_finishes_as_an_open_failure_not_a_method_failure()
        {
            var a = Up();
            a.NoteSighting(5, _connection, Ms(100), true);   // the path carries data; the open is what is stuck
            _now += SmartLinkProofRules.DefaultMethodBudgetMs - 1;
            Assert.Equal(SmartLinkAttemptEnd.Dialled, a.Poll());
            _now += 1;
            Assert.Equal(SmartLinkAttemptEnd.OpenFailed, a.Poll());
            Assert.False(SmartLinkAttemptRules.IsMethodFailure(a.End));
        }

        // ── Nothing decides an attempt after its end ────────────────────────

        [Fact]
        public void After_its_end_every_late_event_is_ignored_and_counted()
        {
            var a = Up();
            a.NoteOpenCompleted();
            _now += 15_000;
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, a.Poll());

            // The radio's data arrives after all, the connection falls, the
            // open "completes", someone cancels: none of it is about a
            // decided attempt.
            Assert.Equal(SightingVerdict.IgnoredAttemptOver, a.NoteSighting(5, _connection, Ms(15_200), true));
            Assert.False(a.NoteCommandConnectionLost(_connection));
            a.NoteOpenCompleted();
            a.NoteTransportUp(_connection, 5);
            Assert.False(a.TryEnd(SmartLinkAttemptEnd.Cancelled, "late"));
            Assert.False(a.TryEnd(SmartLinkAttemptEnd.Proven, "late"));

            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, a.End);
            Assert.Null(a.QualifiedUdpAfter);
            Assert.Equal(6, a.LateEventsIgnored);
            _now += 100_000;
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, a.Poll());
        }

        [Fact]
        public void A_fall_after_proof_does_not_unprove_the_attempt()
        {
            var a = Up();
            a.NoteOpenCompleted();
            a.NoteSighting(5, _connection, Ms(100), true);
            Assert.True(a.IsProven);
            Assert.False(a.NoteCommandConnectionLost(_connection));   // a session drop later is not the attempt's
            Assert.True(a.IsProven);
        }

        // ── The request ─────────────────────────────────────────────────────

        [Fact]
        public void Giving_the_request_up_cancels_its_token_and_an_arrived_handle_does_not()
        {
            var given = Attempt();
            Assert.False(given.RequestToken.IsCancellationRequested);
            given.SettleRequest(handleArrived: false);
            Assert.True(given.RequestToken.IsCancellationRequested);
            Assert.True(given.RequestSettled);
            Assert.False(given.HandleArrived);

            var answered = Attempt();
            answered.SettleRequest(handleArrived: true);
            Assert.False(answered.RequestToken.IsCancellationRequested);
            Assert.True(answered.HandleArrived);
            Assert.Equal(SmartLinkAttemptEnd.Dialled, answered.End);
        }

        [Fact]
        public void An_attempt_ended_with_its_request_unanswered_stops_listening_for_it()
        {
            var a = Attempt();
            Assert.True(a.TryEnd(SmartLinkAttemptEnd.Cancelled, "the operator pressed Escape"));
            Assert.True(a.RequestToken.IsCancellationRequested);
            Assert.True(a.RequestSettled);
        }

        // ── The survival rule the hardware gate may switch on ───────────────

        [Fact]
        public void With_the_survival_rule_on_success_waits_for_the_connection_to_outlive_the_evidence()
        {
            var rules = new SmartLinkProofRules { SurvivalMs = 10_000 };
            var a = Up(rules: rules);
            a.NoteOpenCompleted();
            a.NoteSighting(5, _connection, Ms(100), true);
            Assert.False(a.IsOver);          // evidence in, not yet proven
            _now += 9_999;
            Assert.Equal(SmartLinkAttemptEnd.Dialled, a.Poll());
            _now += 1;
            Assert.Equal(SmartLinkAttemptEnd.Proven, a.Poll());
        }

        [Fact]
        public void With_the_survival_rule_on_a_fall_inside_it_is_a_method_failure()
        {
            // The 2026-08-06 shape: opened, data seen, dropped ten seconds on.
            var rules = new SmartLinkProofRules { SurvivalMs = 10_000 };
            var a = Up(rules: rules);
            a.NoteOpenCompleted();
            a.NoteSighting(5, _connection, Ms(100), true);
            _now += 8_000;
            Assert.True(a.NoteCommandConnectionLost(_connection));
            Assert.Equal(SmartLinkAttemptEnd.CommandConnectionLost, a.End);
        }

        [Fact]
        public void The_ruled_defaults_are_fifteen_seconds_and_no_survival_wait()
        {
            Assert.Equal(15_000, SmartLinkProofRules.Default.WindowMs);
            Assert.Equal(0, SmartLinkProofRules.Default.SurvivalMs);
            Assert.Equal(130_000, SmartLinkProofRules.Default.MethodBudgetMs);
        }

        // ── No input for anything else ──────────────────────────────────────

        [Fact]
        public void The_attempt_has_no_input_for_client_arrivals_or_any_byte_count()
        {
            // A second operator arriving or leaving, our own client's removal
            // and re-add, the receive sampler and any counter that includes
            // TCP: none can be handed to an attempt, because there is nowhere
            // to hand them. Its evidence is exactly these four.
            var inputs = typeof(SmartLinkAttempt)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.StartsWith("Note", StringComparison.Ordinal))
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "NoteCommandConnectionLost", "NoteOpenCompleted", "NoteSighting", "NoteTransportUp" }, inputs);

            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Radios", "SmartLink", "SmartLinkAttempt.cs"));
            foreach (string never in new[] { "AvgRXTotalkbps", "RxTraffic", "GUIClient", "GuiClient", "_udpSuccessfulRegistration" })
                Assert.DoesNotContain(never, source);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }
}
