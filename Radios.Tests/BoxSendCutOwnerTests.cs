using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A box send is owned by the transmit kill switch (#835 fix round 1,
    /// Codex's blocker 1): the reflected-power CUT, with its existing
    /// thresholds and the hold after it, applies to the carrier the box keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug this pins: the box keyed the radio and asked the PTT
    /// controller to WATCH, which leaves the controller Idle, and the
    /// controller's cut is judged only for a transmission it owns. The
    /// warning ran; the cut never could. These tests drive the real
    /// composition the host builds (<see cref="BoxSendOwnedRig.Own"/> under
    /// <see cref="VoiceMessageSend.BeginKeyed"/>) over a real
    /// <see cref="FlexBase"/> with no radio behind it, feed it a reflected
    /// reading through the same fields the meter callbacks write, and let the
    /// kill switch's own watcher thread judge it.
    /// </para>
    /// <para>
    /// What is not proved: that the radio's meters report as they do here,
    /// or what the air hears. The bench's.
    /// </para>
    /// </remarks>

    public sealed class BoxSendCutOwnerTests : IDisposable
    {
        public BoxSendCutOwnerTests() => TransmitKillSwitch.ResetForTests();
        public void Dispose() => TransmitKillSwitch.ResetForTests();

        private static FlexBase Rig() => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };

        private static void FeedPower(FlexBase rig, float forwardDbm, float reflectedDbm)
        {
            var t = typeof(FlexBase);
            object gate = t.GetField("_powerPairLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig)!;
            lock (gate)
            {
                long now = Stopwatch.GetTimestamp();
                t.GetField("_PowerDBM", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rig, forwardDbm);
                t.GetField("_ReflectedPower", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rig, reflectedDbm);
                t.GetField("_forwardStamp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rig, now);
                t.GetField("_reflectedStamp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rig, now);
            }
        }

        [Fact]
        public void A_box_send_is_armed_on_the_kill_switch_for_its_whole_life()
        {
            var rig = Rig();
            try
            {
                using (var owned = BoxSendOwnedRig.Own(rig, new FlexBaseVoiceMessageRig(rig), "the box send of CQ"))
                {
                    Assert.True(TransmitKillSwitch.Armed, "the box send keyed with no cut owner armed");
                    Assert.Contains("CQ", TransmitKillSwitch.WhatIsArmed);
                    var send = VoiceMessageSend.BeginKeyed(owned, "CQ", 0, new KeyedSendOptions());
                    send.Stop();
                    Assert.True(TransmitKillSwitch.Armed, "the arm must outlive the unkey request; the owner disposes it");
                }
                Assert.False(TransmitKillSwitch.Armed, "the arm outlived the send");
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        /// <summary>
        /// Arming is not the protection; the cut firing is. Twenty watts back
        /// at a hundred forward, on the box send's own carrier, with the
        /// operator's cutoff on: the watts rung fires, the hold is told, and
        /// the source that could key the radio again is disarmed.
        /// </summary>
        [Fact]
        public void The_reflected_power_cut_fires_on_a_carrier_the_box_keyed()
        {
            var rig = Rig();
            try
            {
                rig.TxFilePlayer.Load(new float[48000 * 20], 48000, "cq");
                ReflectedCutNotice? notice = null;
                TransmitKillSwitch.CutOnReflectedAlarm = () => true;
                TransmitKillSwitch.ReflectedCutHappened = n => notice = n;

                using var owned = BoxSendOwnedRig.Own(rig, new FlexBaseVoiceMessageRig(rig), "the box send of CQ");
                var send = VoiceMessageSend.BeginKeyed(owned, "CQ", 0, new KeyedSendOptions());
                Assert.True(rig.TxFilePlaying, "positive control: the box engaged the file");

                var until = DateTime.UtcNow.AddSeconds(5);
                while (notice == null && DateTime.UtcNow < until)
                {
                    FeedPower(rig, forwardDbm: 50f, reflectedDbm: 43f);   // 100 W out, 20 W back
                    Thread.Sleep(20);
                }

                Assert.True(notice.HasValue, "no reflected-power cut fired on the box send's carrier");
                Assert.Equal(TransmitSafety.ReflectedCut.Watts, notice!.Value.Rung);
                Assert.False(rig.TxFilePlaying, "the cut left the recording engaged");
                Assert.True(TransmitKillSwitch.KillRequested);
                GC.KeepAlive(send);
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        /// <summary>
        /// The send keys through the switch, so the switch's own refusal holds:
        /// once a stop has been asked for, the box cannot key again.
        /// </summary>
        [Fact]
        public void After_a_kill_the_box_cannot_raise_the_carrier_again()
        {
            var rig = Rig();
            try
            {
                using var owned = BoxSendOwnedRig.Own(rig, new FlexBaseVoiceMessageRig(rig), "the box send of CQ");
                TransmitKillSwitch.Request(TransmitKillSwitch.Source.HostRequest);
                owned.Transmit = true;
                Assert.True(owned.KeyRefused, "a box send keyed after the kill switch had been told to stop");
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        /// <summary>
        /// Escape is the main window's, for a box send: a send must not be
        /// killed by Escape pressed in the operator's logger. The transmit
        /// checks keep watching Escape from anywhere.
        /// </summary>
        [Fact]
        public void A_box_send_does_not_make_the_switch_watch_Escape_from_other_programs()
        {
            var rig = Rig();
            try
            {
                using (BoxSendOwnedRig.Own(rig, new FlexBaseVoiceMessageRig(rig), "the box send of CQ"))
                    Assert.False(TransmitKillSwitch.WatchesEscape);
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                    Assert.True(TransmitKillSwitch.WatchesEscape, "positive control: a check still watches Escape");
            }
            finally { try { rig.Dispose(); } catch { } }
        }
    }
}
