#nullable enable

using System;
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

        private static bool Send(RigOnVendorRadio rig, bool register, Action<string, bool> onState) => register
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

            bool sent = Send(rig, register, (_, __) => { });

            Assert.False(sent, "The registration command was sent after the change-nothing hold was armed.");
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

            Assert.False(underTheLock, "Progress was reported while the hold's lock was held.");
            Assert.NotEmpty(reports);   // the waiting state, raised inside the vendor call, still arrives
        }
    }
}
