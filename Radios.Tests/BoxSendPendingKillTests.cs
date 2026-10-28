using System;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A kill cancels every key after it, including a box send's that is not
    /// made yet (#835 fix round 1, Codex's follow-up 4). The box waits for the
    /// radio to confirm VOX off, and for VOX's own tail to drop, before it keys;
    /// a hard kill in that wait used to leave the start to key afterwards,
    /// because only Escape invalidated it.
    /// </summary>
    /// <remarks>
    /// The host reads <see cref="TransmitKillSwitch.DisarmGeneration"/> at the
    /// press and arms through <see cref="BoxSendOwnedRig.Own(FlexBase, IVoiceMessageRig, string, long)"/>
    /// just before it keys. Every kill route disarms the sources through
    /// <see cref="TransmitKillSwitch.DisarmSources"/>: the controller's
    /// <c>KillTransmitNow</c>, the system-wide push to talk watchdogs that call
    /// it, and the switch's own <see cref="TransmitKillSwitch.Request"/>.
    /// </remarks>
    public sealed class BoxSendPendingKillTests : IDisposable
    {
        public BoxSendPendingKillTests() => TransmitKillSwitch.ResetForTests();
        public void Dispose() => TransmitKillSwitch.ResetForTests();

        private static FlexBase Rig() => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };

        [Fact]
        public void A_kill_while_the_box_send_gets_ready_cancels_its_key()
        {
            var rig = Rig();
            try
            {
                long atPress = TransmitKillSwitch.DisarmGeneration;

                TransmitKillSwitch.DisarmSources(rig);   // what every kill does, during the VOX wait

                var owned = BoxSendOwnedRig.Own(rig, new FlexBaseVoiceMessageRig(rig), "the box send of CQ", atPress);
                Assert.Null(owned);
                Assert.False(TransmitKillSwitch.Armed, "a cancelled start left the kill switch armed");
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        [Fact]
        public void Without_a_kill_the_box_send_keys_as_before()
        {
            var rig = Rig();
            try
            {
                long atPress = TransmitKillSwitch.DisarmGeneration;
                using var owned = BoxSendOwnedRig.Own(rig, new FlexBaseVoiceMessageRig(rig), "the box send of CQ", atPress);
                Assert.NotNull(owned);
                Assert.True(TransmitKillSwitch.Armed);
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        /// <summary>The kill switch's own route (a check's Escape or a cut) counts as a kill too.</summary>
        [Fact]
        public void The_kill_switchs_own_request_counts_as_a_kill()
        {
            var rig = Rig();
            try
            {
                long atPress = TransmitKillSwitch.DisarmGeneration;
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                    TransmitKillSwitch.Request(TransmitKillSwitch.Source.HostRequest);
                Assert.Null(BoxSendOwnedRig.Own(rig, new FlexBaseVoiceMessageRig(rig), "the box send of CQ", atPress));
            }
            finally { try { rig.Dispose(); } catch { } }
        }
    }
}
