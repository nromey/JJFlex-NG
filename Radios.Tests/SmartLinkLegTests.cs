#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The SmartLink method walk, driven with a fake radio layer: every way an
    /// attempt can end, the step that follows it, and that an attempt's pieces
    /// are torn down before the next one starts (the tiers plan, section 4).
    /// No radio, no session, no speech: the fake records what it was asked to
    /// do, in order.
    /// </summary>
    public sealed class SmartLinkLegTests
    {
        // ── The fake ────────────────────────────────────────────────────────

        internal sealed class FakeWorld : ISmartLinkLegWorld
        {
            public long Clock = 5_000_000;
            public bool Cancel;
            public int SocketGen = 10;
            public readonly List<string> Log = new();
            public readonly List<SmartLinkConnectRecord> Stored = new();
            public readonly List<(SmartLinkMethod From, SmartLinkMethod To)> Moves = new();
            public readonly Dictionary<SmartLinkAttempt, object> Connections = new();
            public readonly Dictionary<SmartLinkAttempt, int> Sockets = new();
            public readonly HashSet<SmartLinkMethod> NoLongerPermitted = new();
            public readonly List<SmartLinkAttempt> Attempts = new();
            public SmartLinkMethod? InFlight;
            public int Commits;
            public SmartLinkAttempt? ProvenAttempt;

            public Func<SmartLinkAttempt, SmartLinkHandleAnswer> OnRequest = _ => SmartLinkHandleAnswer.Arrived;
            public Func<SmartLinkAttempt, bool>? OnConnect;
            public Func<SmartLinkAttempt, SmartLinkOpenAnswer>? OnOpen;
            /// <summary>Called on every Sleep (the wait for proof), so a test can make something happen during it.</summary>
            public Action<SmartLinkAttempt?>? OnSleep;

            public long NowMs => Clock;
            public void Sleep(int ms) { Clock += ms; OnSleep?.Invoke(Attempts.LastOrDefault()); }
            public bool OperatorCancelled => Cancel;
            public int VitaSocketGeneration => SocketGen;
            public bool StillPermitted(SmartLinkMethod method) => !NoLongerPermitted.Contains(method);

            public SmartLinkAttemptRecord Prepare(SmartLinkAttempt attempt)
            {
                Attempts.Add(attempt);
                Log.Add("prepare:" + attempt.Method);
                return attempt.Method == SmartLinkMethod.HolePunch
                    ? SmartLinkAttemptRecord.ForPunch(40420, "radioProfile")
                    : SmartLinkAttemptRecord.ForPorts(attempt.Method,
                        new AdvertisedPortPair(attempt.Method == SmartLinkMethod.ManualPorts ? PortPairSource.Manual : PortPairSource.Upnp, 4994, 4993, true));
            }

            public SmartLinkHandleAnswer RequestHandle(SmartLinkAttempt attempt)
            {
                Log.Add("request:" + attempt.Method);
                return OnRequest(attempt);
            }

            /// <summary>The transport comes up on a new connection and a new socket.</summary>
            public bool TransportComesUp(SmartLinkAttempt attempt)
            {
                var connection = new object();
                Connections[attempt] = connection;
                Sockets[attempt] = ++SocketGen;
                attempt.NoteTransportUp(connection, SocketGen);
                return true;
            }

            public bool ConnectTransport(SmartLinkAttempt attempt)
            {
                Log.Add("connect:" + attempt.Method);
                return (OnConnect ?? TransportComesUp)(attempt);
            }

            /// <summary>The radio's data arrives on this attempt's own socket and connection.</summary>
            public SightingVerdict Sight(SmartLinkAttempt attempt, int ms = 118) =>
                attempt.NoteSighting(Sockets[attempt], Connections[attempt], TimeSpan.FromMilliseconds(ms), socketIsCurrent: true);

            public SmartLinkOpenAnswer Open(SmartLinkAttempt attempt)
            {
                Log.Add("open:" + attempt.Method);
                if (OnOpen != null) return OnOpen(attempt);
                Sight(attempt);
                return new SmartLinkOpenAnswer(SmartLinkOpenAnswerKind.Completed, "");
            }

            public int ProofWaits;
            public void ProofWaitBegan(SmartLinkAttempt attempt) => ProofWaits++;
            public void CommitOpen(SmartLinkAttempt attempt) { Commits++; Log.Add("commit:" + attempt.Method); }
            public void Teardown(SmartLinkAttempt attempt, string why) => Log.Add("teardown:" + attempt.Method);
            public void AnnounceMove(SmartLinkMethod from, SmartLinkMethod to)
            {
                Moves.Add((from, to));
                Log.Add($"say:{from}>{to}");
            }
            public void Store(SmartLinkConnectRecord record) => Stored.Add(record);
            public void MethodInFlight(SmartLinkMethod? method) => InFlight = method;
            public void Proven(SmartLinkAttempt attempt, SmartLinkConnectRecord record) { ProvenAttempt = attempt; Log.Add("proven:" + attempt.Method); }
            public void Trace(string line) { }
        }

        private static readonly AdvertisedPortPair Manual = new(PortPairSource.Manual, 4994, 4993, true);
        private static readonly AdvertisedPortPair Upnp = new(PortPairSource.Upnp, 21000, 21001, true);
        private static readonly AdvertisedPortPair NoManual = new(PortPairSource.Manual, -1, -1, true);
        private static readonly AdvertisedPortPair NoUpnp = new(PortPairSource.Upnp, -1, -1, false);

        private static (SmartLinkLeg leg, FakeWorld world) Leg(params SmartLinkMethod[] methods)
        {
            bool punch = methods.Contains(SmartLinkMethod.HolePunch);
            var report = new RadioPortReport(
                methods.Contains(SmartLinkMethod.ManualPorts) ? Manual : NoManual,
                methods.Contains(SmartLinkMethod.UpnpPorts) ? Upnp : NoUpnp,
                RadioSaysHolePunch: punch, FromRadioList: true);
            var input = new SmartLinkPolicyInput(SmartLinkConnectionMode.AutomaticHolePunch, true,
                RadioConnectionPreference.Auto, false, report);
            var decision = new SmartLinkPolicyDecision(methods, SmartLinkRefusal.None, null, false);
            var record = SmartLinkConnectRecord.Begin("4711-0001-8600-0001", "op@example.invalid", input, decision, DateTime.UtcNow);
            var world = new FakeWorld();
            return (new SmartLinkLeg(record, methods, world), world);
        }

        private static (SmartLinkLeg leg, FakeWorld world) TwoMethods() =>
            Leg(SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts);

        private static SmartLinkOpenAnswer Completed => new(SmartLinkOpenAnswerKind.Completed, "");

        // ── The rule table ──────────────────────────────────────────────────

        [Theory]
        [InlineData(SmartLinkAttemptEnd.Proven, true, SmartLinkLegNext.Done)]
        [InlineData(SmartLinkAttemptEnd.Proven, false, SmartLinkLegNext.Done)]
        [InlineData(SmartLinkAttemptEnd.Cancelled, true, SmartLinkLegNext.Stop)]
        [InlineData(SmartLinkAttemptEnd.Cancelled, false, SmartLinkLegNext.Stop)]
        [InlineData(SmartLinkAttemptEnd.ConnectFailed, true, SmartLinkLegNext.NextMethod)]
        [InlineData(SmartLinkAttemptEnd.ConnectFailed, false, SmartLinkLegNext.EndLeg)]
        [InlineData(SmartLinkAttemptEnd.CommandConnectionLost, true, SmartLinkLegNext.NextMethod)]
        [InlineData(SmartLinkAttemptEnd.CommandConnectionLost, false, SmartLinkLegNext.EndLeg)]
        [InlineData(SmartLinkAttemptEnd.NoQualifiedUdp, true, SmartLinkLegNext.NextMethod)]
        [InlineData(SmartLinkAttemptEnd.NoQualifiedUdp, false, SmartLinkLegNext.EndLeg)]
        [InlineData(SmartLinkAttemptEnd.NoHandle, true, SmartLinkLegNext.EndLeg)]
        [InlineData(SmartLinkAttemptEnd.NullHandle, true, SmartLinkLegNext.EndLeg)]
        [InlineData(SmartLinkAttemptEnd.OpenFailed, true, SmartLinkLegNext.EndLeg)]
        [InlineData(SmartLinkAttemptEnd.NotSent, true, SmartLinkLegNext.EndLeg)]
        [InlineData(SmartLinkAttemptEnd.Pending, true, SmartLinkLegNext.EndLeg)]
        [InlineData(SmartLinkAttemptEnd.Dialled, true, SmartLinkLegNext.EndLeg)]
        public void What_follows_each_end(SmartLinkAttemptEnd end, bool hasNext, SmartLinkLegNext expected)
        {
            Assert.Equal(expected, SmartLinkAttemptRules.After(end, hasNext));
        }

        [Fact]
        public void Every_end_the_record_can_hold_has_a_row_above()
        {
            var rows = typeof(SmartLinkLegTests).GetMethod(nameof(What_follows_each_end))!
                .GetCustomAttributesData()
                .Where(a => a.AttributeType == typeof(InlineDataAttribute))
                .Select(a => (SmartLinkAttemptEnd)(int)((IEnumerable<System.Reflection.CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!).First().Value!)
                .ToHashSet();
            foreach (SmartLinkAttemptEnd end in Enum.GetValues<SmartLinkAttemptEnd>())
                Assert.Contains(end, rows);
        }

        [Fact]
        public void Only_three_ends_are_failures_of_the_method()
        {
            var failures = Enum.GetValues<SmartLinkAttemptEnd>().Where(SmartLinkAttemptRules.IsMethodFailure).ToArray();
            Assert.Equal(new[]
            {
                SmartLinkAttemptEnd.ConnectFailed, SmartLinkAttemptEnd.CommandConnectionLost, SmartLinkAttemptEnd.NoQualifiedUdp,
            }, failures);
        }

        // ── The request stage: no answer is never a method failure ──────────

        [Theory]
        [InlineData(SmartLinkHandleAnswerKind.NoAnswer, SmartLinkAttemptEnd.NoHandle)]
        [InlineData(SmartLinkHandleAnswerKind.Empty, SmartLinkAttemptEnd.NullHandle)]
        [InlineData(SmartLinkHandleAnswerKind.NotSent, SmartLinkAttemptEnd.NotSent)]
        public void A_request_that_is_not_answered_ends_the_leg_and_no_other_method_is_sent(
            SmartLinkHandleAnswerKind answer, SmartLinkAttemptEnd expectedEnd)
        {
            var (leg, world) = TwoMethods();
            world.OnRequest = _ => new SmartLinkHandleAnswer(answer, "");

            Assert.False(leg.RunConnectStage());

            Assert.True(leg.Ended);
            Assert.False(leg.Cancelled);
            Assert.Equal(expectedEnd, leg.LastEnd);
            Assert.Equal(new[] { "prepare:ManualPorts", "request:ManualPorts" }, world.Log);
            Assert.Empty(world.Moves);                      // nothing is said: the walk above decides what is next
            Assert.Null(world.InFlight);
            // Its request is settled, and no longer listened for.
            Assert.True(world.Attempts[0].RequestSettled);
            Assert.True(world.Attempts[0].RequestToken.IsCancellationRequested);
            // Filed as it ended.
            var last = world.Stored.Last();
            Assert.Equal(expectedEnd, last.LastAttempt!.End);
            Assert.False(last.LastAttempt.HandleArrived);
            Assert.Null(last.MethodUsed);
            Assert.False(string.IsNullOrWhiteSpace(last.LastAttempt.EndDetail));
        }

        // ── The connect stage ───────────────────────────────────────────────

        [Fact]
        public void A_method_whose_connect_fails_is_torn_down_before_the_next_one_is_requested_and_the_move_is_said_once()
        {
            var (leg, world) = TwoMethods();
            world.OnConnect = a => a.Method != SmartLinkMethod.ManualPorts && world.TransportComesUp(a);

            Assert.True(leg.RunConnectStage());

            Assert.Equal(new[]
            {
                "prepare:ManualPorts", "request:ManualPorts", "connect:ManualPorts",
                "teardown:ManualPorts",
                "say:ManualPorts>UpnpPorts",
                "prepare:UpnpPorts", "request:UpnpPorts", "connect:UpnpPorts",
            }, world.Log);
            Assert.Single(world.Moves);
            Assert.Equal(SmartLinkMethod.UpnpPorts, world.InFlight);
            Assert.True(leg.OwesOpen);

            var record = leg.Record;
            Assert.Equal(2, record.Attempts.Count);
            Assert.Equal(SmartLinkAttemptEnd.ConnectFailed, record.Attempts[0].End);
            Assert.True(record.Attempts[0].HandleArrived);
            Assert.Null(record.Attempts[0].OpenSucceeded);          // its open was never attempted
            Assert.Equal(1, record.Attempts[0].Sequence);
            Assert.Equal(SmartLinkAttemptEnd.Dialled, record.Attempts[1].End);
            Assert.Equal(2, record.Attempts[1].Sequence);
            Assert.Equal(SmartLinkMethod.UpnpPorts, record.MethodUsed);
            // Each state was stored as it happened, the failure before the next attempt appeared.
            int failedAt = world.Stored.FindIndex(r => r.Attempts.Count == 1 && r.Attempts[0].End == SmartLinkAttemptEnd.ConnectFailed);
            int secondAt = world.Stored.FindIndex(r => r.Attempts.Count == 2);
            Assert.True(failedAt >= 0 && secondAt > failedAt);
            Assert.Null(world.Stored[failedAt].MethodUsed);
        }

        [Fact]
        public void The_last_method_failing_its_connect_ends_the_leg_without_a_word()
        {
            var (leg, world) = TwoMethods();
            world.OnConnect = _ => false;

            Assert.False(leg.RunConnectStage());

            Assert.True(leg.Ended);
            Assert.Single(world.Moves);                                  // Manual to UPnP, said once
            Assert.Equal(SmartLinkAttemptEnd.ConnectFailed, leg.LastEnd);
            Assert.Equal(2, world.Log.Count(l => l.StartsWith("teardown:", StringComparison.Ordinal)));
            Assert.Null(world.InFlight);
            Assert.Null(leg.Record.MethodUsed);
        }

        [Fact]
        public void An_owner_that_made_the_first_transport_itself_hands_over_the_result()
        {
            // The app's connect dials, makes the first transport, then hands
            // the result here.
            var (up, upWorld) = TwoMethods();
            Assert.True(up.DialCurrent());
            upWorld.TransportComesUp(upWorld.Attempts[0]);
            Assert.True(up.ContinueAfterTransport(true, out bool movedOn));
            Assert.False(movedOn);
            Assert.DoesNotContain(upWorld.Log, l => l.StartsWith("connect:", StringComparison.Ordinal));

            var (failed, world) = TwoMethods();
            Assert.True(failed.DialCurrent());
            Assert.True(failed.ContinueAfterTransport(false, out movedOn));
            Assert.True(movedOn);                                        // the transport now up is the UPnP method's
            Assert.Equal(new[]
            {
                "prepare:ManualPorts", "request:ManualPorts",
                "teardown:ManualPorts", "say:ManualPorts>UpnpPorts",
                "prepare:UpnpPorts", "request:UpnpPorts", "connect:UpnpPorts",
            }, world.Log);
        }

        // ── The open stage and the proof ────────────────────────────────────

        [Fact]
        public void A_proven_attempt_is_committed_only_after_its_proof_and_is_the_method_used()
        {
            var (leg, world) = TwoMethods();
            Assert.True(leg.RunConnectStage());
            Assert.True(leg.RunOpenStage());

            Assert.Equal(new[]
            {
                "prepare:ManualPorts", "request:ManualPorts", "connect:ManualPorts",
                "open:ManualPorts", "proven:ManualPorts", "commit:ManualPorts",
            }, world.Log);
            Assert.Equal(1, world.Commits);
            Assert.Empty(world.Moves);
            Assert.Equal(SmartLinkMethod.ManualPorts, world.InFlight);
            var attempt = leg.Record.LastAttempt!;
            Assert.Equal(SmartLinkAttemptEnd.Proven, attempt.End);
            Assert.True(attempt.OpenSucceeded);
            Assert.Equal(TimeSpan.FromMilliseconds(118), attempt.QualifiedUdpAfter);
            Assert.Equal(SmartLinkMethod.ManualPorts, leg.Record.MethodUsed);
            Assert.False(leg.OwesOpen);
        }

        [Fact]
        public void The_open_completing_first_waits_for_the_radios_data_and_says_nothing_meanwhile()
        {
            var (leg, world) = TwoMethods();
            world.OnOpen = _ => Completed;                                   // no data yet
            int sleeps = 0;
            world.OnSleep = a => { if (++sleeps == 40) world.Sight(a!, 1_100); };   // a second later

            Assert.True(leg.RunConnectStage());
            Assert.True(leg.RunOpenStage());

            Assert.Equal(40, sleeps);
            Assert.Equal(1, world.Commits);
            Assert.Equal(TimeSpan.FromMilliseconds(1_100), leg.Record.LastAttempt!.QualifiedUdpAfter);
            // While it waited, the record said opened and not yet proven, and nothing was committed.
            Assert.Contains(world.Stored, r => r.LastAttempt!.End == SmartLinkAttemptEnd.Dialled && r.LastAttempt.OpenSucceeded == true);
            Assert.True(world.Log.IndexOf("commit:ManualPorts") > world.Log.IndexOf("proven:ManualPorts"));
        }

        [Fact]
        public void No_data_after_the_open_is_a_method_failure_torn_down_and_followed_by_the_next_method()
        {
            var (leg, world) = TwoMethods();
            world.OnOpen = a =>
            {
                if (a.Method == SmartLinkMethod.UpnpPorts) world.Sight(a);
                return Completed;
            };

            Assert.True(leg.RunConnectStage());
            long began = world.Clock;
            Assert.True(leg.RunOpenStage());

            Assert.Equal(new[]
            {
                "prepare:ManualPorts", "request:ManualPorts", "connect:ManualPorts",
                "open:ManualPorts",
                "teardown:ManualPorts",                    // before anything of the next attempt
                "say:ManualPorts>UpnpPorts",
                "prepare:UpnpPorts", "request:UpnpPorts", "connect:UpnpPorts",
                "open:UpnpPorts", "proven:UpnpPorts", "commit:UpnpPorts",
            }, world.Log);
            Assert.InRange(world.Clock - began, SmartLinkProofRules.DefaultWindowMs, SmartLinkProofRules.DefaultWindowMs + 2 * SmartLinkLeg.ProofPollMs);
            Assert.Single(world.Moves);
            Assert.Equal(1, world.Commits);                // only the proven one is ever committed

            var record = leg.Record;
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, record.Attempts[0].End);
            Assert.True(record.Attempts[0].OpenSucceeded);  // it opened; its data never came
            Assert.Null(record.Attempts[0].QualifiedUdpAfter);
            Assert.Equal(SmartLinkAttemptEnd.Proven, record.Attempts[1].End);
            Assert.Equal(SmartLinkMethod.UpnpPorts, record.MethodUsed);
        }

        [Fact]
        public void A_proof_timeout_during_the_open_stops_the_open_is_recorded_as_the_proof_failure_and_walks_on()
        {
            // Settled item 4. The open can run 45 seconds; the proof window is
            // 15. The open is stopped by ITS ATTEMPT failing, not by a cancel.
            var (leg, world) = TwoMethods();
            world.OnOpen = a =>
            {
                if (a.Method == SmartLinkMethod.UpnpPorts) { world.Sight(a); return Completed; }
                while (true)                                 // the open's own wait, reading its attempt
                {
                    Assert.False(world.Cancel);
                    a.Poll();
                    if (a.HasFailed) return new SmartLinkOpenAnswer(SmartLinkOpenAnswerKind.StoppedByAttempt, "stopped");
                    world.Clock += 25;
                }
            };

            Assert.True(leg.RunConnectStage());
            Assert.True(leg.RunOpenStage());

            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, leg.Record.Attempts[0].End);
            Assert.False(leg.Record.Attempts[0].OpenSucceeded);
            Assert.False(leg.Cancelled);
            Assert.Single(world.Moves);
            Assert.Equal(SmartLinkAttemptEnd.Proven, leg.Record.Attempts[1].End);
            Assert.True(world.Log.IndexOf("teardown:ManualPorts") < world.Log.IndexOf("prepare:UpnpPorts"));
        }

        [Fact]
        public void An_operator_Escape_during_the_open_is_recorded_as_a_cancel_and_never_walks_on()
        {
            // The other half of settled item 4: the same moment, the other signal.
            var (leg, world) = TwoMethods();
            world.OnOpen = a =>
            {
                world.Cancel = true;
                return new SmartLinkOpenAnswer(SmartLinkOpenAnswerKind.Cancelled, "Cancelled by user");
            };

            Assert.True(leg.RunConnectStage());
            Assert.False(leg.RunOpenStage());

            Assert.True(leg.Cancelled);
            Assert.Equal(SmartLinkAttemptEnd.Cancelled, leg.Record.Attempts[0].End);
            Assert.Single(leg.Record.Attempts);
            Assert.Empty(world.Moves);
            Assert.DoesNotContain("request:UpnpPorts", world.Log);
            Assert.Equal(0, world.Commits);
        }

        [Fact]
        public void An_Escape_wins_even_when_the_attempt_failed_its_proof_at_the_same_moment()
        {
            var (leg, world) = TwoMethods();
            world.OnOpen = a =>
            {
                world.Clock += SmartLinkProofRules.DefaultWindowMs;
                a.Poll();                                   // NoQualifiedUdp, a method failure...
                world.Cancel = true;                        // ...and the operator pressed Escape
                return new SmartLinkOpenAnswer(SmartLinkOpenAnswerKind.StoppedByAttempt, "");
            };

            Assert.True(leg.RunConnectStage());
            Assert.False(leg.RunOpenStage());

            Assert.True(leg.Cancelled);
            Assert.Empty(world.Moves);
            Assert.DoesNotContain("request:UpnpPorts", world.Log);
        }

        [Fact]
        public void The_command_connection_lost_during_the_open_is_a_method_failure_and_the_next_method_is_tried()
        {
            var (leg, world) = TwoMethods();
            world.OnOpen = a =>
            {
                if (a.Method == SmartLinkMethod.UpnpPorts) { world.Sight(a); return Completed; }
                return new SmartLinkOpenAnswer(SmartLinkOpenAnswerKind.ConnectionLost, "Connection lost during setup");
            };

            Assert.True(leg.RunConnectStage());
            Assert.True(leg.RunOpenStage());

            Assert.Equal(SmartLinkAttemptEnd.CommandConnectionLost, leg.Record.Attempts[0].End);
            Assert.Single(world.Moves);
            Assert.Equal(SmartLinkMethod.UpnpPorts, leg.Record.MethodUsed);
        }

        [Fact]
        public void A_connection_that_fell_between_the_connect_and_the_open_moves_on_without_running_that_open()
        {
            var (leg, world) = TwoMethods();
            world.OnOpen = a => { world.Sight(a); return Completed; };

            Assert.True(leg.RunConnectStage());
            var first = world.Attempts[0];
            Assert.True(first.NoteCommandConnectionLost(world.Connections[first]));   // the radio dropped us before Start
            Assert.True(leg.OwesOpen);                                                // the open stage still has work: the next method

            Assert.True(leg.RunOpenStage());

            Assert.DoesNotContain("open:ManualPorts", world.Log);
            Assert.Equal(SmartLinkAttemptEnd.CommandConnectionLost, leg.Record.Attempts[0].End);
            Assert.Null(leg.Record.Attempts[0].OpenSucceeded);
            Assert.Equal(SmartLinkAttemptEnd.Proven, leg.Record.Attempts[1].End);
        }

        [Fact]
        public void An_open_that_fails_for_its_own_reasons_ends_the_leg_and_no_other_method_is_tried()
        {
            // No free slice, setup data that never came, a station name the
            // radio would not take: none is the path's fault.
            var (leg, world) = TwoMethods();
            world.OnOpen = a => new SmartLinkOpenAnswer(SmartLinkOpenAnswerKind.Failed, "No slices available");

            Assert.True(leg.RunConnectStage());
            Assert.False(leg.RunOpenStage());

            Assert.True(leg.Ended);
            Assert.False(leg.Cancelled);
            var attempt = leg.Record.LastAttempt!;
            Assert.Equal(SmartLinkAttemptEnd.OpenFailed, attempt.End);
            Assert.Equal("No slices available", attempt.EndDetail);
            Assert.False(attempt.OpenSucceeded);
            Assert.Empty(world.Moves);
            Assert.DoesNotContain("request:UpnpPorts", world.Log);
            // Left as the open left it: the walk above retires the session when it moves to another path.
            Assert.DoesNotContain("teardown:ManualPorts", world.Log);
            Assert.Null(leg.Record.MethodUsed);
            Assert.Null(world.InFlight);
        }

        [Fact]
        public void A_hole_punch_as_the_only_method_is_owned_and_proven_like_any_other_and_has_nothing_to_fall_back_to()
        {
            // Settled item 2: "Hole punch always", or Tier 3 with no usable pair.
            var (proven, provenWorld) = Leg(SmartLinkMethod.HolePunch);
            Assert.True(proven.RunConnectStage());
            Assert.True(proven.RunOpenStage());
            Assert.Equal(SmartLinkAttemptEnd.Proven, proven.Record.LastAttempt!.End);
            Assert.Equal(40420, proven.Record.LastAttempt.PunchPortSent);
            Assert.Equal(SmartLinkMethod.HolePunch, proven.Record.MethodUsed);

            // The 2026-08-06 shape: it opened, and the radio's data never reached us.
            var (silent, world) = Leg(SmartLinkMethod.HolePunch);
            world.OnOpen = _ => Completed;
            Assert.True(silent.RunConnectStage());
            Assert.False(silent.RunOpenStage());
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, silent.Record.LastAttempt!.End);
            Assert.Contains("teardown:HolePunch", world.Log);
            Assert.Empty(world.Moves);
            Assert.Equal(0, world.Commits);
            Assert.Null(silent.Record.MethodUsed);
        }

        // ── Escape, at every wait ───────────────────────────────────────────

        [Fact]
        public void Escape_before_anything_is_sent_sends_nothing()
        {
            var (leg, world) = TwoMethods();
            world.Cancel = true;
            Assert.False(leg.RunConnectStage());
            Assert.True(leg.Cancelled);
            Assert.Empty(world.Log);
        }

        [Fact]
        public void Escape_during_the_wait_for_the_handle_stops_listening_and_nothing_follows()
        {
            var (leg, world) = TwoMethods();
            world.OnRequest = _ => { world.Cancel = true; return new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.Cancelled, ""); };
            Assert.False(leg.RunConnectStage());
            Assert.True(leg.Cancelled);
            Assert.Equal(SmartLinkAttemptEnd.Cancelled, leg.LastEnd);
            Assert.True(world.Attempts[0].RequestToken.IsCancellationRequested);
            Assert.DoesNotContain(world.Log, l => l.StartsWith("connect:", StringComparison.Ordinal));
            Assert.Empty(world.Moves);
        }

        [Fact]
        public void Escape_after_the_handle_and_before_the_connect_makes_no_connection()
        {
            var (leg, world) = TwoMethods();
            world.OnRequest = _ => { world.Cancel = true; return SmartLinkHandleAnswer.Arrived; };
            Assert.False(leg.RunConnectStage());
            Assert.True(leg.Cancelled);
            Assert.DoesNotContain(world.Log, l => l.StartsWith("connect:", StringComparison.Ordinal));
        }

        [Fact]
        public void Escape_while_the_connect_is_failing_is_a_cancel_not_a_method_failure()
        {
            var (leg, world) = TwoMethods();
            world.OnConnect = _ => { world.Cancel = true; return false; };
            Assert.False(leg.RunConnectStage());
            Assert.True(leg.Cancelled);
            Assert.Equal(SmartLinkAttemptEnd.Cancelled, leg.LastEnd);
            Assert.Empty(world.Moves);
            Assert.DoesNotContain("request:UpnpPorts", world.Log);
        }

        [Fact]
        public void Escape_as_the_connection_comes_up_tears_it_down_and_stops()
        {
            var (leg, world) = TwoMethods();
            world.OnConnect = a => { world.TransportComesUp(a); world.Cancel = true; return true; };
            Assert.False(leg.RunConnectStage());
            Assert.True(leg.Cancelled);
            Assert.Contains("teardown:ManualPorts", world.Log);
            Assert.False(leg.OwesOpen);
        }

        [Fact]
        public void Escape_during_the_wait_for_proof_is_a_cancel_and_tears_the_attempt_down()
        {
            var (leg, world) = TwoMethods();
            world.OnOpen = _ => Completed;
            int sleeps = 0;
            world.OnSleep = _ => { if (++sleeps == 10) world.Cancel = true; };

            Assert.True(leg.RunConnectStage());
            Assert.False(leg.RunOpenStage());

            Assert.True(leg.Cancelled);
            Assert.Equal(SmartLinkAttemptEnd.Cancelled, leg.Record.LastAttempt!.End);
            Assert.Contains("teardown:ManualPorts", world.Log);
            Assert.Empty(world.Moves);
            Assert.Equal(0, world.Commits);
        }

        // ── Late events never decide another attempt ────────────────────────

        [Fact]
        public void Late_data_and_a_late_fall_from_the_first_method_decide_nothing_once_the_second_has_begun()
        {
            var (leg, world) = TwoMethods();
            SmartLinkAttempt? first = null;
            world.OnOpen = a =>
            {
                if (a.Method == SmartLinkMethod.ManualPorts) { first = a; return Completed; }   // no data: fails its proof

                // The second method's open is running. NOW the first method's
                // radio data turns up, and its connection's fall is reported.
                Assert.Equal(SightingVerdict.IgnoredAttemptOver, world.Sight(first!, 16_000));
                Assert.False(first!.NoteCommandConnectionLost(world.Connections[first]));
                // And the same late packet, offered to the attempt now in
                // flight, is refused: it is the old socket's, and it arrived
                // under the old connection.
                Assert.Equal(SightingVerdict.RefusedStaleSocket,
                    a.NoteSighting(world.Sockets[first], world.Connections[first], TimeSpan.FromMilliseconds(16_000), socketIsCurrent: false));
                Assert.Equal(SightingVerdict.RefusedOtherConnection,
                    a.NoteSighting(world.Sockets[a], world.Connections[first], TimeSpan.FromMilliseconds(5), socketIsCurrent: true));
                Assert.False(a.NoteCommandConnectionLost(world.Connections[first]));
                Assert.False(a.IsOver);
                return Completed;                                                                // still no data of its own
            };

            Assert.True(leg.RunConnectStage());
            Assert.False(leg.RunOpenStage());

            // Neither attempt was proven by the first one's late data, and the
            // second failed on its own clock, not on the first one's fall.
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, leg.Record.Attempts[0].End);
            Assert.Null(leg.Record.Attempts[0].QualifiedUdpAfter);
            Assert.Equal(SmartLinkAttemptEnd.NoQualifiedUdp, leg.Record.Attempts[1].End);
            Assert.Null(leg.Record.Attempts[1].QualifiedUdpAfter);
            Assert.Equal(2, first!.LateEventsIgnored);
            Assert.Equal(0, world.Commits);
        }

        // ── The ceiling, read again for each later method ───────────────────

        [Fact]
        public void A_method_the_policy_stopped_allowing_mid_walk_is_never_sent()
        {
            var (leg, world) = TwoMethods();
            world.OnConnect = a =>
            {
                world.NoLongerPermitted.Add(SmartLinkMethod.UpnpPorts);   // the tier was lowered while the first method was failing
                return false;
            };

            Assert.False(leg.RunConnectStage());

            Assert.DoesNotContain("request:UpnpPorts", world.Log);
            Assert.DoesNotContain("prepare:UpnpPorts", world.Log);
            Assert.Equal(SmartLinkAttemptEnd.NotSent, leg.Record.Attempts[1].End);
            Assert.Equal(SmartLinkMethod.UpnpPorts, leg.Record.Attempts[1].Method);
            Assert.Equal("The current policy check did not permit this method", leg.Record.Attempts[1].EndDetail);
            Assert.True(leg.Ended);
            Assert.Empty(world.Moves);        // a method that is never tried is never announced as being tried
        }

        [Fact]
        public void The_first_method_is_not_asked_again_because_the_plan_that_allowed_it_was_just_made()
        {
            var (leg, world) = TwoMethods();
            world.NoLongerPermitted.Add(SmartLinkMethod.ManualPorts);
            Assert.True(leg.RunConnectStage());
            Assert.Contains("request:ManualPorts", world.Log);
        }

        [Fact]
        public void A_note_on_the_answer_stays_on_the_attempt_through_its_final_end()
        {
            // "Ready" names a radio, not a request. When an answer may have
            // been meant for an earlier request the record keeps saying so,
            // proven or failed, and not only while the attempt is in flight.
            const string note = "An earlier request to this radio went unanswered 7 seconds before this reply; the reply may belong to that earlier request";
            var (proven, world) = TwoMethods();
            world.OnRequest = _ => new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.Arrived, note);
            Assert.True(proven.RunConnectStage());
            Assert.EndsWith(note, proven.Record.LastAttempt!.EndDetail);
            Assert.True(proven.RunOpenStage());
            Assert.Equal(SmartLinkAttemptEnd.Proven, proven.Record.LastAttempt!.End);
            Assert.EndsWith(note, proven.Record.LastAttempt.EndDetail);
            Assert.StartsWith("Radio setup completed and audio or data began arriving on this connection", proven.Record.LastAttempt.EndDetail);

            var (failed, failedWorld) = Leg(SmartLinkMethod.ManualPorts);
            failedWorld.OnRequest = _ => new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.Arrived, note);
            failedWorld.OnConnect = _ => false;
            Assert.False(failed.RunConnectStage());
            Assert.Equal(SmartLinkAttemptEnd.ConnectFailed, failed.Record.LastAttempt!.End);
            Assert.EndsWith(note, failed.Record.LastAttempt.EndDetail);

            // And it is one attempt's note, not the next one's.
            var (two, twoWorld) = TwoMethods();
            twoWorld.OnRequest = a => new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.Arrived,
                a.Method == SmartLinkMethod.ManualPorts ? note : "");
            twoWorld.OnConnect = a => a.Method != SmartLinkMethod.ManualPorts && twoWorld.TransportComesUp(a);
            Assert.True(two.RunConnectStage());
            Assert.EndsWith(note, two.Record.Attempts[0].EndDetail);
            Assert.DoesNotContain("earlier request", two.Record.Attempts[1].EndDetail);
        }

        [Fact]
        public void The_wait_for_proof_is_announced_to_the_world_only_when_there_is_one()
        {
            var (early, earlyWorld) = TwoMethods();          // the data arrives during the open
            Assert.True(early.RunConnectStage());
            Assert.True(early.RunOpenStage());
            Assert.Equal(0, earlyWorld.ProofWaits);

            var (late, lateWorld) = TwoMethods();            // the open finishes first
            lateWorld.OnOpen = _ => Completed;
            int sleeps = 0;
            lateWorld.OnSleep = a => { if (++sleeps == 4) lateWorld.Sight(a!); };
            Assert.True(late.RunConnectStage());
            Assert.True(late.RunOpenStage());
            Assert.Equal(1, lateWorld.ProofWaits);
        }

        [Fact]
        public void Whether_the_leg_will_move_on_is_asked_of_the_policy_too()
        {
            var (leg, world) = TwoMethods();
            Assert.True(leg.RunConnectStage());
            Assert.True(leg.WillMoveOnAfter(SmartLinkAttemptEnd.CommandConnectionLost));
            Assert.False(leg.WillMoveOnAfter(SmartLinkAttemptEnd.OpenFailed));

            world.NoLongerPermitted.Add(SmartLinkMethod.UpnpPorts);
            Assert.False(leg.WillMoveOnAfter(SmartLinkAttemptEnd.CommandConnectionLost));   // no move will be said, so nothing is withheld for it
        }

        [Fact]
        public void An_attempt_ended_unproven_signals_whoever_is_making_its_transport()
        {
            var (leg, world) = TwoMethods();
            Assert.True(leg.DialCurrent());
            var attempt = world.Attempts[0];
            Assert.False(attempt.EndedToken.IsCancellationRequested);
            leg.Abandon(operatorCancelled: false, "a new dial began on this rig");
            Assert.True(attempt.EndedToken.IsCancellationRequested);

            var (proven, provenWorld) = TwoMethods();
            Assert.True(proven.RunConnectStage());
            Assert.True(proven.RunOpenStage());
            Assert.False(provenWorld.Attempts[0].EndedToken.IsCancellationRequested);     // a proven attempt's transport is kept
        }

        // ── Abandoned from outside ──────────────────────────────────────────

        [Fact]
        public void An_undecided_attempt_abandoned_from_outside_gets_an_end_and_its_request_is_let_go()
        {
            var (leg, world) = TwoMethods();
            Assert.True(leg.DialCurrent());
            leg.Abandon(operatorCancelled: false, "the rig was disconnected");
            Assert.True(leg.Ended);
            Assert.Equal(SmartLinkAttemptEnd.OpenFailed, leg.Record.LastAttempt!.End);
            Assert.Null(leg.Record.MethodUsed);
            Assert.Null(world.InFlight);

            var (cancelled, _) = TwoMethods();
            Assert.True(cancelled.DialCurrent());
            cancelled.Abandon(operatorCancelled: true, "cancelled");
            Assert.True(cancelled.Cancelled);
            Assert.Equal(SmartLinkAttemptEnd.Cancelled, cancelled.Record.LastAttempt!.End);
        }

        [Fact]
        public void A_proven_leg_is_not_changed_by_being_abandoned_later()
        {
            var (leg, _) = TwoMethods();
            Assert.True(leg.RunConnectStage());
            Assert.True(leg.RunOpenStage());
            leg.Abandon(operatorCancelled: false, "the rig was disconnected");
            Assert.Equal(SmartLinkAttemptEnd.Proven, leg.Record.LastAttempt!.End);
            Assert.Equal(SmartLinkMethod.ManualPorts, leg.Record.MethodUsed);
        }

        // ── The order of a reconnect ────────────────────────────────────────

        [Fact]
        public void A_reconnect_tries_the_method_that_last_proved_itself_first_then_the_policys_order()
        {
            var permitted = new[] { SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts };
            Assert.Equal(new[] { SmartLinkMethod.UpnpPorts, SmartLinkMethod.ManualPorts },
                SmartLinkAttemptRules.ReconnectOrder(permitted, SmartLinkMethod.UpnpPorts));
            Assert.Equal(permitted, SmartLinkAttemptRules.ReconnectOrder(permitted, SmartLinkMethod.ManualPorts));
            Assert.Equal(permitted, SmartLinkAttemptRules.ReconnectOrder(permitted, null));
        }

        [Fact]
        public void A_reconnect_never_adds_a_method_the_policy_does_not_permit_now()
        {
            // The radio last proved itself by a punch; the tier is now Tier 2.
            var permitted = new[] { SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts };
            var order = SmartLinkAttemptRules.ReconnectOrder(permitted, SmartLinkMethod.HolePunch);
            Assert.Equal(permitted, order);
            Assert.DoesNotContain(SmartLinkMethod.HolePunch, order);
            // And the last proven UPnP method under a tier that now allows only the manual pair.
            Assert.Equal(new[] { SmartLinkMethod.ManualPorts },
                SmartLinkAttemptRules.ReconnectOrder(new[] { SmartLinkMethod.ManualPorts }, SmartLinkMethod.UpnpPorts));
            Assert.Empty(SmartLinkAttemptRules.ReconnectOrder(Array.Empty<SmartLinkMethod>(), SmartLinkMethod.UpnpPorts));
        }

        [Fact]
        public void A_reconnect_keeps_every_permitted_method_exactly_once()
        {
            var permitted = new[] { SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts, SmartLinkMethod.HolePunch };
            foreach (SmartLinkMethod? last in new SmartLinkMethod?[] { null, SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts, SmartLinkMethod.HolePunch })
            {
                var order = SmartLinkAttemptRules.ReconnectOrder(permitted, last);
                Assert.Equal(permitted.OrderBy(m => m), order.OrderBy(m => m));
            }
        }
    }

    /// <summary>
    /// The request ledger: one outstanding connect request per account and
    /// radio, and the memory of one that went unanswered.
    /// </summary>
    [Collection(SmartLinkRequestLedgerCollection.Name)]
    public sealed class SmartLinkRequestLedgerTests
    {
        private static string Radio() => "4711-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        private const string Account = "ledger@example.invalid";

        [Fact]
        public void A_second_request_for_the_same_radio_is_refused_while_one_is_unsettled()
        {
            string radio = Radio();
            Assert.True(SmartLinkRequestLedger.TryBegin(Account, radio));
            Assert.True(SmartLinkRequestLedger.IsOutstanding(Account, radio));
            Assert.False(SmartLinkRequestLedger.TryBegin(Account, radio));

            SmartLinkRequestLedger.Settle(Account, radio, answered: true, nowMs: 1_000);
            Assert.False(SmartLinkRequestLedger.IsOutstanding(Account, radio));
            Assert.True(SmartLinkRequestLedger.TryBegin(Account, radio));
            SmartLinkRequestLedger.Settle(Account, radio, answered: true, nowMs: 2_000);
        }

        [Fact]
        public void An_unanswered_request_is_remembered_for_a_minute_so_a_later_answer_can_be_marked()
        {
            string radio = Radio();
            Assert.Null(SmartLinkRequestLedger.RecentUnansweredAgeMs(Account, radio, 500));
            Assert.True(SmartLinkRequestLedger.TryBegin(Account, radio));
            SmartLinkRequestLedger.Settle(Account, radio, answered: false, nowMs: 10_000);

            Assert.Equal(7_000, SmartLinkRequestLedger.RecentUnansweredAgeMs(Account, radio, 17_000));
            Assert.Null(SmartLinkRequestLedger.RecentUnansweredAgeMs(Account, radio, 10_000 + SmartLinkRequestLedger.LateAnswerWatchMs));

            // An answered request clears the memory.
            Assert.True(SmartLinkRequestLedger.TryBegin(Account, radio));
            SmartLinkRequestLedger.Settle(Account, radio, answered: true, nowMs: 20_000);
            Assert.Null(SmartLinkRequestLedger.RecentUnansweredAgeMs(Account, radio, 21_000));
        }

        [Fact]
        public void Another_radio_and_another_account_are_not_held_up_or_marked()
        {
            string radio = Radio(), other = Radio();
            Assert.True(SmartLinkRequestLedger.TryBegin(Account, radio));
            Assert.True(SmartLinkRequestLedger.TryBegin(Account, other));                    // a second radio on this account
            Assert.True(SmartLinkRequestLedger.TryBegin("second@example.invalid", radio));   // the same radio through another account
            SmartLinkRequestLedger.Settle(Account, radio, answered: false, nowMs: 1_000);
            Assert.Null(SmartLinkRequestLedger.RecentUnansweredAgeMs(Account, other, 2_000));
            Assert.Null(SmartLinkRequestLedger.RecentUnansweredAgeMs("second@example.invalid", radio, 2_000));
            SmartLinkRequestLedger.Settle(Account, other, true, 3_000);
            SmartLinkRequestLedger.Settle("second@example.invalid", radio, true, 3_000);
        }
    }

    /// <summary>The ledger is process-wide state; tests that read it do not run beside each other.</summary>
    [CollectionDefinition(Name)]
    public sealed class SmartLinkRequestLedgerCollection
    {
        public const string Name = "SmartLink request ledger";
    }
}
