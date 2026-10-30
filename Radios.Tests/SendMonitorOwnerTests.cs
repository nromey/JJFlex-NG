#nullable enable
using System;
using System.Collections.Generic;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Every send is heard through the radio's transmit monitor, switched on
    /// silently and put back (#835; Noel, 2026-10-28 16:36, 16:47, 16:48).
    /// </summary>
    /// <remarks>
    /// The port is a fake: these prove the owner's decisions. What they cannot
    /// see is the radio: that <c>Radio.TXMonitor</c> written through the command
    /// queue reaches it, and how late the monitor arrives over SmartLink. Those
    /// are bench steps.
    /// </remarks>
    public sealed class SendMonitorOwnerTests
    {
        private sealed class FakePort : SendMonitorOwner.IPort
        {
            public bool? Monitor = false;
            public long Operator;
            public object? Conn = new object();
            public readonly List<string> Writes = new();
            public int Watching;
            public SendMonitorOwner? Owner;

            public bool? MonitorOn => Monitor;
            public long OperatorWrites => Operator;
            public object? Connection => Conn;
            public void WriteMonitor(bool on) => Writes.Add(on ? "on" : "off");
            public void Watch(bool watching) => Watching += watching ? 1 : -1;
            public int Lost;
            public void PutBackLost() => Lost++;

            /// <summary>The radio reports the monitor; the port tells the owner, as FlexBase's does.</summary>
            public void Report(bool on) { Monitor = on; Owner?.Observe(); }

            /// <summary>The operator's setter: counted, then the radio echoes it.</summary>
            public void OperatorSets(bool on) { Operator++; Report(on); }
        }

        private static (SendMonitorOwner owner, FakePort port) Make(bool monitorOn = false)
        {
            var port = new FakePort { Monitor = monitorOn };
            var owner = new SendMonitorOwner(port);
            port.Owner = owner;
            return (owner, port);
        }

        [Fact]
        public void A_monitor_that_was_off_goes_on_for_the_send_and_back_off_after()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            Assert.Equal(new[] { "on" }, port.Writes);
            port.Report(true);
            Assert.True(owner.OnForASend);

            hold.Dispose();

            Assert.Equal(new[] { "on", "off" }, port.Writes);
            Assert.False(owner.Holding);
            Assert.Equal(0, port.Watching);
        }

        [Fact]
        public void A_monitor_already_on_is_the_operators_and_is_never_written()
        {
            var (owner, port) = Make(monitorOn: true);
            owner.Hold("cq").Dispose();
            Assert.Empty(port.Writes);
        }

        [Fact]
        public void A_send_that_ends_before_the_radio_echoes_the_turn_on_still_puts_it_back()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            // No echo yet: the radio still reads off. That is our write in
            // flight, not the operator turning it off.
            hold.Dispose();
            Assert.Equal(new[] { "on", "off" }, port.Writes);
        }

        [Fact]
        public void The_operator_turning_the_monitor_on_mid_send_keeps_it_on()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            port.Report(true);
            port.OperatorSets(false);
            port.OperatorSets(true);
            hold.Dispose();
            Assert.Equal(new[] { "on" }, port.Writes);
            Assert.True(port.Monitor);
        }

        [Fact]
        public void Any_operator_write_during_the_send_hands_the_monitor_over_even_with_no_change()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            port.Report(true);
            port.OperatorSets(true);   // Home's checkbox set on while it already was
            hold.Dispose();
            Assert.Equal(new[] { "on" }, port.Writes);
        }

        [Fact]
        public void Turned_off_by_another_client_mid_send_is_left_off_and_not_written()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            port.Report(true);
            port.Report(false);   // not through our setter
            hold.Dispose();
            Assert.Equal(new[] { "on" }, port.Writes);
        }

        [Fact]
        public void Nested_holds_turn_it_on_once_and_off_once_when_the_last_lets_go()
        {
            var (owner, port) = Make();
            var host = owner.Hold("slice A's recording");
            port.Report(true);
            var slice = owner.Hold("slice A playback on the air");
            host.Dispose();
            Assert.Equal(new[] { "on" }, port.Writes);
            slice.Dispose();
            slice.Dispose();   // twice is harmless
            Assert.Equal(new[] { "on", "off" }, port.Writes);
        }

        [Fact]
        public void A_radio_that_disconnected_during_the_send_is_not_written()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            port.Report(true);
            port.Conn = null;
            hold.Dispose();
            Assert.Equal(new[] { "on" }, port.Writes);
        }

        /// <summary>
        /// Codex round 1, finding 3: a disconnect used to forget that the send
        /// had turned the monitor on. Now a put-back that cannot be written is
        /// reported, so the next connect can make it.
        /// </summary>
        [Fact]
        public void A_put_back_lost_to_a_disconnect_is_reported_not_forgotten()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            port.Report(true);
            port.Conn = null;
            hold.Dispose();
            Assert.Equal(1, port.Lost);
        }

        [Fact]
        public void A_put_back_made_on_the_disconnect_path_is_not_made_again_or_reported_lost()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            port.Report(true);

            Assert.True(owner.TakeBackForDisconnect(), "the send turned it on, so the disconnect path puts it back");
            Assert.False(owner.TakeBackForDisconnect(), "and only once");
            port.Conn = null;
            hold.Dispose();

            Assert.Equal(new[] { "on" }, port.Writes);   // the disconnect path wrote "off" itself
            Assert.Equal(0, port.Lost);
        }

        [Fact]
        public void The_disconnect_path_leaves_an_operators_monitor_alone()
        {
            var (owner, port) = Make(monitorOn: true);
            var hold = owner.Hold("cq");
            Assert.False(owner.TakeBackForDisconnect());
            hold.Dispose();

            var (owner2, port2) = Make();
            var hold2 = owner2.Hold("cq");
            port2.Report(true);
            port2.OperatorSets(true);
            Assert.False(owner2.TakeBackForDisconnect());
            hold2.Dispose();
            Assert.Equal(0, port2.Lost);
        }

        [Fact]
        public void A_new_connection_is_not_written_for_a_send_on_the_old_one()
        {
            var (owner, port) = Make();
            var hold = owner.Hold("cq");
            port.Report(true);
            port.Conn = new object();
            hold.Dispose();
            Assert.Equal(new[] { "on" }, port.Writes);
        }

        [Fact]
        public void With_no_radio_nothing_is_written_at_either_end()
        {
            var (owner, port) = Make();
            port.Monitor = null;
            port.Conn = null;
            owner.Hold("cq").Dispose();
            Assert.Empty(port.Writes);
        }

        [Fact]
        public void A_second_send_after_the_first_starts_fresh()
        {
            var (owner, port) = Make();
            var first = owner.Hold("cq");
            port.Report(true);
            port.OperatorSets(true);
            first.Dispose();
            Assert.Equal(new[] { "on" }, port.Writes);

            port.OperatorSets(false);   // the operator turns it off between sends
            owner.Hold("cq").Dispose();
            Assert.Equal(new[] { "on", "on", "off" }, port.Writes);
        }
    }
}
