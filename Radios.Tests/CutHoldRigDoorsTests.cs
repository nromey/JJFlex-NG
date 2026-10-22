#nullable enable
using System;
using System.Collections.Generic;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #571, the rig's side of the hold, on a real <see cref="FlexBase"/> with
    /// no radio behind it: who-wrote-it for the snapshot setters (Astra B2),
    /// the CW keying doors (B5) and the VOX levers' honesty about what they
    /// could issue (B3).
    /// </summary>
    /// <remarks>
    /// The bare rig's command queue is not running, so a write made through it
    /// is dropped with a trace; to see whether a CW message was QUEUED at all,
    /// the tests switch the queue's loop flag on (it then collects rather than
    /// runs, because nothing dequeues) and read its count. Nothing here reaches
    /// a radio.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class CutHoldRigDoorsTests : IDisposable
    {
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        public void Dispose()
        {
            FlexBase.RefuseIfTransmitHeld = null;
            FlexBase.IsTransmitHeld = null;
            try { _rig.Dispose(); } catch { }
        }

        // ── Astra B2: the app's own writes are told apart from the operator's at the setter ──

        [Theory]
        [InlineData("XmitPower")]
        [InlineData("TunePower")]
        [InlineData("TXAntennaName")]
        public void An_app_scoped_write_of_a_snapshot_setter_raises_the_app_write_event_with_its_value(string what)
        {
            var seen = new List<(string, string)>();
            _rig.AppInitiatedRadioSettingWritten += (w, v) => seen.Add((w, v));

            using (_rig.AppInitiatedSettingChanges())
            {
                switch (what)
                {
                    case "XmitPower": _rig.XmitPower = 25; break;
                    case "TunePower": _rig.TunePower = 40; break;
                    case "TXAntennaName": _rig.TXAntennaName = "ANT1"; break;
                }
            }

            // TXAntennaName's setter drops the request on a rig with no slice
            // before it notes anything, and says so in the trace; the two
            // power setters note first. That asymmetry is the setters' own and
            // is recorded here rather than papered over.
            if (what == "TXAntennaName")
                Assert.Empty(seen);
            else
                Assert.Equal(new[] { (what, what == "XmitPower" ? "25" : "40") }, seen);
        }

        // ── Opus N1: Dummy Load Mode is the operator's own change, carried out by the app ──

        [Fact]
        public void Dummy_Load_Mode_raises_the_operator_changed_signal_for_both_powers_and_not_the_app_write_event()
        {
            var operatorChanged = new List<string>();
            var appWrote = new List<(string, string)>();
            _rig.OperatorChangedRadioSetting += w => operatorChanged.Add(w);
            _rig.AppInitiatedRadioSettingWritten += (w, v) => appWrote.Add((w, v));

            _rig.DummyLoadMode = true;

            Assert.Equal(new[] { "XmitPower", "TunePower" }, operatorChanged);
            Assert.Empty(appWrote);

            operatorChanged.Clear();
            _rig.DummyLoadMode = false;   // leaving it puts the saved powers back: also the operator's choice
            Assert.Equal(new[] { "XmitPower", "TunePower" }, operatorChanged);
            Assert.Empty(appWrote);
        }

        [Fact]
        public void Wired_to_a_hold_Dummy_Load_Mode_lifts_it_and_an_app_restore_does_not()
        {
            // Opus's probe 1 and its control, through the same two events the
            // controller subscribes: the operator's command lifts; the Audio
            // Check's restore (an app-scoped write of the same setter) moves
            // the baseline and holds.
            long now = 1_000_000;
            var hold = new ReflectedCutHold(() => now);
            _rig.OperatorChangedRadioSetting += w => hold.OperatorChanged(w, now);
            _rig.AppInitiatedRadioSettingWritten += (w, v) => hold.AppWrote(w, v);
            var reading = new TransmitPowerReading(17.8f, 10.3f, 4f, 20f, 25);
            var at = new ReflectedCutHold.TransmitSetup("ANT2", 14_250_000, 25, 10, false, "", "USB");

            hold.Begin(TransmitSafety.ReflectedCut.Share, reading, "ANT2", at, now);
            using (_rig.AppInitiatedSettingChanges()) _rig.XmitPower = 25;     // the restore
            Assert.False(hold.Tick(new ReflectedCutHold.TransmitSetup("ANT2", 14_250_000, 25, 10, false, "", "USB"), now += 300),
                "an app restore's echo lifted the hold");
            Assert.True(hold.Holding);

            _rig.DummyLoadMode = true;                                           // the operator's command
            Assert.False(hold.Holding, "Dummy Load Mode did not lift the hold");
            Assert.Equal(ReflectedCutHold.EndReason.Lifted, hold.LastEnd);
            Assert.StartsWith("you changed the RF power", hold.LastEndDetail);
        }

        [Fact]
        public void The_operators_own_write_does_not_raise_the_app_write_event()
        {
            var seen = new List<(string, string)>();
            _rig.AppInitiatedRadioSettingWritten += (w, v) => seen.Add((w, v));
            _rig.XmitPower = 25;   // no app scope: the operator's hand
            Assert.Empty(seen);
        }

        // ── Astra B5: CW keying is a held door ──

        [Fact]
        public void A_CW_message_is_refused_at_the_door_and_nothing_is_queued()
        {
            string? asked = null;
            FlexBase.RefuseIfTransmitHeld = door => { asked = door; return true; };
            _rig.q.MainLoop = true;   // collect rather than drop, so the queue count is the evidence

            Assert.False(_rig.SendCW("CQ CQ DE K5NER"));
            Assert.Equal("a CW message", asked);
            Assert.Equal(0, _rig.q.Count);

            Assert.False(_rig.SendCW('K'));
            Assert.Equal("CW typing", asked);
            Assert.Equal(0, _rig.q.Count);
        }

        [Fact]
        public void With_no_hold_a_CW_message_is_queued_as_before()
        {
            // The positive control: the door open, the same calls queue their work.
            FlexBase.RefuseIfTransmitHeld = door => false;
            _rig.q.MainLoop = true;
            Assert.True(_rig.SendCW("CQ"));
            Assert.True(_rig.SendCW('K'));
            Assert.Equal(2, _rig.q.Count);

            FlexBase.RefuseIfTransmitHeld = null;   // no controller at all reads as not held
            Assert.True(_rig.SendCW("CQ"));
            Assert.Equal(3, _rig.q.Count);
        }

        [Fact]
        public void A_door_hook_that_throws_reads_as_not_held()
        {
            // Noel's worry runs this way: "I can't transmit because an alarm
            // hasn't cleared" must not be reachable through a broken hook.
            FlexBase.RefuseIfTransmitHeld = door => throw new InvalidOperationException("gone");
            _rig.q.MainLoop = true;
            Assert.True(_rig.SendCW("CQ"));
            Assert.Equal(1, _rig.q.Count);
        }

        // ── Astra B3: the levers say what they could issue ──

        [Fact]
        public void On_a_rig_with_no_radio_the_VOX_levers_read_none_and_issue_nothing()
        {
            Assert.Equal(FlexBase.VoxSetting.None, _rig.CurrentVoxSettingIfOn());
            Assert.False(_rig.WriteVoxSetting(FlexBase.VoxSetting.SimpleVox, true, immediate: true), "no radio: nothing was issued, and the lever must say so");
            Assert.False(_rig.WriteVoxSetting(FlexBase.VoxSetting.CwBreakIn, true, immediate: false));
            Assert.False(_rig.WriteVoxSetting(FlexBase.VoxSetting.None, true, immediate: true));
        }

        [Fact]
        public void The_levers_a_controller_wires_are_the_rigs_own()
        {
            int tones = 0;
            var levers = ReflectedCutHold.LeversFor(_rig, () => tones++);
            Assert.Equal(FlexBase.VoxSetting.None, levers.CurrentIfOn());
            Assert.False(levers.Write(FlexBase.VoxSetting.SimpleVox, false, false));
            levers.PausedTone!();
            Assert.Equal(1, tones);
        }
    }
}
