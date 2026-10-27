using System;
using System.Collections.Generic;
using System.Reflection;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Push to talk takes over a box send, whichever push to talk it is
    /// (#835 fix round 1, Codex's blocker 2; Noel 2026-10-27 15:26, "ptt takes
    /// over"): the keyboard's and the lock through the controller, and a
    /// hardware line — the microphone's, ACC or the rear RCA.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The discriminator.</b> A hardware press is read from the radio's own
    /// interlock source, and the send's own key is software, so the radio
    /// reports SW for it. A takeover is a HARDWARE source seen AFTER a software
    /// one in the same keyed send. So the send's own MOX can never read as the
    /// operator, and neither can a hardware source left over from an earlier
    /// transmission that the radio has not yet replaced with SW.
    /// </para>
    /// <para>
    /// The host predicate is <see cref="BoxSendTakeover.For"/> over a real
    /// <see cref="FlexBase"/> on a real FlexLib <c>Radio</c>, fed the interlock
    /// status lines the radio sends. What is NOT proved: that the radio
    /// reports the source this way when a hardware line is pressed under a
    /// software key. Nobody has read that off a radio; it is a bench step.
    /// </para>
    /// </remarks>
    public sealed class BoxSendTakeoverTests
    {
        private static readonly MethodInfo NoteRadioMox =
            typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /// <summary>A rig on a vendor radio whose Mox reaches FlexBase.Transmit the way radioPropertyChangedHandler's "Mox" case does.</summary>
        private static RigOnVendorRadio Fixture()
        {
            Assert.NotNull(NoteRadioMox);
            var f = new RigOnVendorRadio("box-takeover-test");
            f.Vendor.Radio.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Mox") NoteRadioMox.Invoke(f.Rig, new object[] { f.Vendor.Radio.Mox });
            };
            return f;
        }

        private static void Interlock(RigOnVendorRadio f, string state, string source) =>
            f.Vendor.Status($"interlock state={state} source={source} tx_client_handle=0x{RigOnVendorRadio.OurHandle:X8}");

        [Fact]
        public void The_sends_own_software_key_is_never_read_as_the_operator()
        {
            using var f = Fixture();
            var takeover = BoxSendTakeover.For(f.Rig, () => false);
            Interlock(f, "TRANSMITTING", "SW");
            Assert.True(f.Rig.Transmit, "positive control: the fixture's interlock reached FlexBase.Transmit");
            for (int i = 0; i < 20; i++) Assert.Equal(BoxTakeover.None, takeover.Check());
        }

        [Theory]
        [InlineData("MIC")]
        [InlineData("ACC")]
        [InlineData("RCA")]
        public void A_hardware_line_pressed_under_the_sends_key_takes_over(string line)
        {
            using var f = Fixture();
            var takeover = BoxSendTakeover.For(f.Rig, () => false);
            Interlock(f, "TRANSMITTING", "SW");
            Assert.Equal(BoxTakeover.None, takeover.Check());

            Interlock(f, "TRANSMITTING", line);

            Assert.Equal(BoxTakeover.Hardware, takeover.Check());
        }

        /// <summary>
        /// A source left over from the operator's last foot-switch transmission
        /// is not a press: nothing is read as hardware until the send's own
        /// software key has been seen first.
        /// </summary>
        [Fact]
        public void A_stale_hardware_source_before_the_sends_key_is_seen_is_not_a_takeover()
        {
            using var f = Fixture();
            var takeover = BoxSendTakeover.For(f.Rig, () => false);
            Interlock(f, "TRANSMITTING", "MIC");
            Assert.Equal(BoxTakeover.None, takeover.Check());
            Interlock(f, "TRANSMITTING", "SW");
            Assert.Equal(BoxTakeover.None, takeover.Check());
            Interlock(f, "TRANSMITTING", "MIC");
            Assert.Equal(BoxTakeover.Hardware, takeover.Check());
        }

        [Fact]
        public void The_controllers_push_to_talk_or_lock_takes_over()
        {
            using var f = Fixture();
            bool held = false;
            var takeover = BoxSendTakeover.For(f.Rig, () => held);
            Interlock(f, "TRANSMITTING", "SW");
            Assert.Equal(BoxTakeover.None, takeover.Check());
            held = true;
            Assert.Equal(BoxTakeover.Controller, takeover.Check());
        }

        [Fact]
        public void Receiving_is_never_a_takeover()
        {
            using var f = Fixture();
            var takeover = BoxSendTakeover.For(f.Rig, () => false);
            Interlock(f, "TRANSMITTING", "SW");
            takeover.Check();
            Interlock(f, "RECEIVE", "MIC");
            Assert.False(f.Rig.Transmit);
            Assert.Equal(BoxTakeover.None, takeover.Check());
        }

        // ── What the send does with each takeover ─────────────────────────

        private sealed class FakeRig : IVoiceMessageRig
        {
            public readonly List<string> Calls = new();
            public bool RadioSaysTransmitting;
            public bool Transmit { get => RadioSaysTransmitting; set => Calls.Add(value ? "Transmit=true" : "Transmit=false"); }
            public bool FilePlaying { get; set; }
            public bool FileReachedEnd { get; set; }
            public double FileSeconds { get; set; } = 8.0;
            public void FileStart() { Calls.Add("FileStart"); FilePlaying = true; }
            public void FileStartForVox() { Calls.Add("FileStartForVox"); FilePlaying = true; }
            public void FileStop() { Calls.Add("FileStop"); FilePlaying = false; }
            public bool VoxOn { get; set; } = true;
        }

        /// <summary>
        /// A hardware takeover stops the recording AND withdraws the send's own
        /// software key: the line holds the carrier, and a software key left
        /// standing would keep the radio keyed after the operator let go.
        /// </summary>
        [Fact]
        public void After_a_hardware_takeover_the_send_withdraws_its_own_software_key()
        {
            var by = BoxTakeover.None;
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "CQ", 0, new KeyedSendOptions { TakenBy = () => by });
            rig.RadioSaysTransmitting = true;
            send.Tick(100);

            by = BoxTakeover.Hardware;
            send.Tick(2000);

            Assert.Equal(VoiceMessageOutcome.TakenOver, send.Outcome);
            Assert.True(send.ReleasedToHardware);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        [Fact]
        public void After_the_controllers_takeover_the_send_leaves_the_key_alone()
        {
            var by = BoxTakeover.None;
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "CQ", 0, new KeyedSendOptions { TakenBy = () => by });
            rig.RadioSaysTransmitting = true;
            send.Tick(100);

            by = BoxTakeover.Controller;
            send.Tick(2000);

            Assert.Equal(VoiceMessageOutcome.TakenOver, send.Outcome);
            Assert.False(send.ReleasedToHardware);
            Assert.DoesNotContain("Transmit=false", rig.Calls);
        }
    }
}
