#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The change-nothing hold is asked at the last moment before a SmartLink
    /// registration command is written, as one step with the write (#713,
    /// repair round 4, Codex B4). Asked only before the token, a hold armed
    /// from Settings while the token was fetched let the command through.
    /// </summary>
    /// <remarks>
    /// The send runs against <see cref="VendorRadioFixture"/>'s fake command
    /// transport, which records what is written and forwards nothing: no
    /// radio, no server, no registration. The token is a test string from
    /// <c>RegistrationTokenForTest</c>; no account is signed in.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RegistrationHoldTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(RegistrationHoldTests));
        private const string Serial = "1111-2222-6300-4444";
        private const string Owner = "owner@example.com";

        private static readonly FieldInfo IsConnectedField =
            typeof(FlexBase).GetField("_IsConnected", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo? GateField =
            typeof(FlexBase).GetField("_changeNothingGate", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly List<RigOnVendorRadio> _rigs = new();

        public void Dispose()
        {
            foreach (var r in _rigs) r.Dispose();
            _scope.Dispose();
        }

        private RigOnVendorRadio ConnectedRig()
        {
            Assert.True(IsConnectedField != null, "FlexBase._IsConnected is not where this test sets it.");
            var rig = new RigOnVendorRadio(Serial);
            _rigs.Add(rig);
            IsConnectedField.SetValue(rig.Rig, true);
            return rig;
        }

        private static FlexBase.RegistrationCheck Confirmed() => new()
        {
            CanProceed = true,
            AccountEmail = Owner,
            Account = new SmartLinkAccount { Email = Owner, FriendlyName = "Owner" },
            RadioSerial = Serial,
        };

        private static List<string> RegistrationCommands(RigOnVendorRadio rig) =>
            rig.Vendor.Transport.Commands.Where(c => c.StartsWith("wan register", StringComparison.Ordinal)
                || c.StartsWith("wan unregister", StringComparison.Ordinal)).ToList();

        private static bool Send(RigOnVendorRadio rig, bool register, Action<string, bool> onState)
            => Outcome(rig, register, onState) == GuardedOutcome.Done;

        private static GuardedOutcome Outcome(RigOnVendorRadio rig, bool register, Action<string, bool> onState) => register
            ? rig.Rig.BeginSmartLinkRegistration(Confirmed(), onState)
            : rig.Rig.BeginSmartLinkUnregistration(Confirmed(), onState);

        /// <summary>
        /// The schedule Codex named: the hold is off when the send begins, and
        /// is armed while the token is being fetched. Nothing may be written.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_hold_armed_while_the_token_is_fetched_stops_the_command(bool register)
        {
            var rig = ConnectedRig();
            rig.Rig.RegistrationTokenForTest = _ =>
            {
                rig.Rig.SetChangeNothingActive(true);    // the operator arms it from Settings meanwhile
                return "test-token";
            };

            var sent = Outcome(rig, register, (_, __) => { });

            // Refused, not Failed (#486): the hold has said so, so the caller
            // must not add "Could not send the command".
            Assert.Equal(GuardedOutcome.Refused, sent);
            Assert.Empty(RegistrationCommands(rig));
        }

        /// <summary>
        /// The control for the same guarantee: with the hold left off, the same
        /// send writes exactly one command to the fake transport, so the test
        /// above is empty for the reason it names.
        /// </summary>
        [Theory]
        [InlineData(true, "wan register owner_token=test-token")]
        [InlineData(false, "wan unregister owner_token=test-token")]
        public void With_the_hold_off_the_same_send_writes_one_command(bool register, string expected)
        {
            var rig = ConnectedRig();
            rig.Rig.RegistrationTokenForTest = _ => "test-token";

            bool sent = Send(rig, register, (_, __) => { });

            Assert.True(sent);
            Assert.Equal(new[] { expected }, RegistrationCommands(rig));
        }

        /// <summary>
        /// The check and the write are one step against the hold: while the
        /// hold's lock is held, arming the hold waits, and a send's last
        /// check and command wait too. Neither can slip between the other's
        /// check and write.
        /// </summary>
        [Fact]
        public void The_hold_and_the_last_check_take_the_same_lock()
        {
            var rig = ConnectedRig();
            Assert.True(GateField != null, "FlexBase._changeNothingGate is not where this test reaches it.");
            object gate = GateField!.GetValue(rig.Rig)!;
            rig.Rig.RegistrationTokenForTest = _ => "test-token";

            Task arming, sending;
            Monitor.Enter(gate);
            try
            {
                sending = Task.Run(() => Send(rig, register: true, (_, __) => { }));
                Thread.Sleep(300);
                Assert.Empty(RegistrationCommands(rig));
                Assert.False(sending.IsCompleted, "The send wrote its command while the hold's lock was held.");

                arming = Task.Run(() => rig.Rig.SetChangeNothingActive(true));
                Thread.Sleep(300);
                Assert.False(rig.Rig.ChangeNothingActive, "The hold was armed while its lock was held.");
            }
            finally
            {
                Monitor.Exit(gate);
            }
            Assert.True(sending.Wait(5000) && arming.Wait(5000));
            Assert.True(rig.Rig.ChangeNothingActive);
        }

        /// <summary>
        /// Progress is reported OUTSIDE the hold's lock, in the order the
        /// radio reported it. Radio Setup's progress callback dispatches to
        /// the window synchronously, and the window is where the hold is
        /// armed; reporting under the lock could wait on the window while the
        /// window waits on the lock.
        /// </summary>
        [Fact]
        public void Progress_is_reported_outside_the_holds_lock_and_in_order()
        {
            var rig = ConnectedRig();
            Assert.True(GateField != null, "FlexBase._changeNothingGate is not where this test reaches it.");
            object gate = GateField!.GetValue(rig.Rig)!;
            rig.Rig.RegistrationTokenForTest = _ => "test-token";
            var reports = new List<string>();
            bool underTheLock = false;

            Assert.True(Send(rig, register: true, (text, _) =>
            {
                if (Monitor.IsEntered(gate)) underTheLock = true;
                lock (reports) reports.Add(text);
            }));

            Assert.True(SpinWait.SpinUntil(() => { lock (reports) return reports.Count > 0; }, 5000),
                "the waiting state, raised inside the vendor call, never arrived");
            Assert.False(underTheLock, "Progress was reported while the hold's lock was held.");
        }

        // ------------------------------------------------------------------
        // Repair round 5: progress is delivered with no lock of ours held
        // ------------------------------------------------------------------

        /// <summary>
        /// A stand-in for a window's dispatcher, with no window: one thread
        /// runs posted work in order, and <see cref="Invoke"/> waits for it
        /// exactly as Dispatcher.Invoke does. Radio Setup's progress callback
        /// is a Dispatcher.Invoke.
        /// </summary>
        private sealed class UiThread : IDisposable
        {
            private readonly BlockingCollection<Action> _work = new();
            private readonly Thread _thread;
            public UiThread()
            {
                _thread = new Thread(() => { foreach (var a in _work.GetConsumingEnumerable()) a(); }) { IsBackground = true };
                _thread.Start();
            }
            public void Invoke(Action a)
            {
                if (Thread.CurrentThread == _thread) { a(); return; }
                var done = new ManualResetEventSlim();
                _work.Add(() => { try { a(); } finally { done.Set(); } });
                done.Wait();
            }
            public Task<T> Post<T>(Func<T> f)
            {
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _work.Add(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
                return tcs.Task;
            }
            public void Dispose() => _work.CompleteAdding();
        }

        private static readonly MethodInfo HandshakeSetter =
            typeof(Flex.Smoothlake.FlexLib.Radio).GetProperty(nameof(Flex.Smoothlake.FlexLib.Radio.WanOwnerHandshakeStatus))!
                .GetSetMethod(nonPublic: true)!;

        private static void RadioSays(RigOnVendorRadio rig, Flex.Smoothlake.FlexLib.Radio.WanRadioRegistrationState state) =>
            HandshakeSetter.Invoke(rig.Vendor.Radio, new object[] { state });

        /// <summary>
        /// Codex B5, the exact schedule. An earlier registration is still in
        /// progress. A second is sent from the window's thread and is
        /// fetching its token when the earlier one's next status arrives,
        /// and its progress callback waits on the window. The second send's
        /// own command then raises a status the earlier operation also hears.
        /// Until round 5 the earlier operation held its ordering lock across
        /// the callback that waits on the window, the window's thread wanted
        /// that lock, and neither ever moved.
        /// </summary>
        [Fact]
        public void A_second_send_from_the_window_never_waits_on_the_firsts_progress()
        {
            var rig = ConnectedRig();
            using var ui = new UiThread();
            var inToken = new ManualResetEventSlim();
            var releaseToken = new ManualResetEventSlim();
            int sends = 0;
            rig.Rig.RegistrationTokenForTest = _ =>
            {
                if (Interlocked.Increment(ref sends) == 2)
                {
                    inToken.Set();
                    releaseToken.Wait(10000);
                }
                return "test-token";
            };

            var firstReports = new List<string>();
            bool armed = false;
            var statusAtTheWindow = new ManualResetEventSlim();
            Action<string, bool> first = (text, _) =>
            {
                if (Volatile.Read(ref armed)) statusAtTheWindow.Set();
                ui.Invoke(() => { lock (firstReports) firstReports.Add(text); });
            };
            Assert.True(Send(rig, register: true, first));
            Assert.True(SpinWait.SpinUntil(() => { lock (firstReports) return firstReports.Count >= 1; }, 5000),
                "the first registration's progress never arrived");

            var second = ui.Post(() => Send(rig, register: true, (_, __) => ui.Invoke(() => { })));
            Assert.True(inToken.Wait(5000), "the second send never reached its token");

            Volatile.Write(ref armed, true);
            var status = Task.Run(() => RadioSays(rig, Flex.Smoothlake.FlexLib.Radio.WanRadioRegistrationState.WaitingForPTT));
            Assert.True(statusAtTheWindow.Wait(5000), "the earlier operation's status never reached its callback");
            releaseToken.Set();

            Assert.True(second.Wait(5000), "Deadlock: the second send never finished.");
            Assert.True(status.Wait(5000), "Deadlock: the earlier operation's status never finished.");
            Assert.True(second.Result);
        }

        /// <summary>
        /// Codex F1. Three states raised while the command is being written
        /// are delivered after it, in order, each with ITS OWN sentence. Until
        /// round 5 each queued state was spoken with the radio's latest state,
        /// so the microphone prompt arrived as the failure sentence.
        /// </summary>
        [Fact]
        public void Each_queued_state_is_reported_with_its_own_sentence()
        {
            var rig = ConnectedRig();
            rig.Rig.RegistrationTokenForTest = _ => "test-token";
            rig.Vendor.Transport.CommandWritten = text =>
            {
                if (!text.StartsWith("wan register", StringComparison.Ordinal)) return;
                RadioSays(rig, Flex.Smoothlake.FlexLib.Radio.WanRadioRegistrationState.WaitingForPTT);
                RadioSays(rig, Flex.Smoothlake.FlexLib.Radio.WanRadioRegistrationState.FailedPTT);
            };
            var reports = new List<(string text, bool terminal)>();

            Assert.True(Send(rig, register: true, (text, terminal) => { lock (reports) reports.Add((text, terminal)); }));
            Assert.True(SpinWait.SpinUntil(() => { lock (reports) return reports.Count >= 3; }, 5000), "the three states were not all reported");
            Thread.Sleep(100);   // and nothing after the terminal one

            lock (reports)
            {
                Assert.Equal(3, reports.Count);
                Assert.Contains("contacting SmartLink", reports[0].text, StringComparison.Ordinal);
                Assert.Contains("key the microphone", reports[1].text, StringComparison.Ordinal);
                Assert.Contains("did not see the microphone", reports[2].text, StringComparison.Ordinal);
                Assert.Equal(new[] { false, false, true }, reports.Select(r => r.terminal).ToArray());
            }
        }
    }
}
