using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Flex.Smoothlake.FlexLib;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 60 m mode correction says only what is known (#312, repair rounds
    /// one and two). Drives a REAL <see cref="FlexBase"/> — its own Mode
    /// guard, its own slice targeting, its own command queue — and reads what
    /// got PAST the speech gate from the silent recording channel (#171), not
    /// what a caller intended to say.
    /// </summary>
    /// <remarks>
    /// <para><b>Ruled 2026-10-20 (round two).</b> A request that went out is
    /// "switching" and claims nothing more; a refusal is certain and says the
    /// mode was not changed, about the MODE only. No "mode set": round one's
    /// read-back of Slice.DemodMode was FlexLib's own local echo.</para>
    /// <para><b>Shown red before each repair.</b> On HEAD 1415e47a a scratch
    /// replay of the rotation's sequence said "mode set to CW" while
    /// transmitting and with no slice (2 of 3 failed). On round one
    /// (d1159ff4, reverted only for the meters half) this class's wording
    /// assertions failed 4 of 8: the sent case said "mode set to CW", and the
    /// refusals said "and nothing was changed ..." / "and the mode was not
    /// changed".</para>
    /// <para><b>What this cannot see.</b> The radio: the slice is FlexLib's
    /// object with no connection, which is exactly why "switching" is all a
    /// sent request may claim. NVDA: the recorder runs with rendering off.
    /// The band jump's and typed entry's arrivals are assembled in
    /// JJFlexWpf.Tests' QuickTypeCommitTests through the same Compose.</para>
    /// </remarks>
    [Collection(SpeechOutputStaticsCollection.Name)]
    public sealed class SixtyMeterModeDeliveryTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly string _path;
        private readonly System.Collections.Generic.List<FlexBase> _rigs = new();

        public SixtyMeterModeDeliveryTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-recorder-tests");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
            ScreenReaderOutput.ResetTransientSpeechStateForTest();
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            // Terse: the level these sentences are spoken at, so a pass here is
            // a pass at the operator's ordinary setting.
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        }

        public void Dispose()
        {
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            try { File.Delete(_path); } catch { }
            foreach (var r in _rigs) { try { r.Dispose(); } catch { } }
        }

        private (FlexBase rig, Slice? slice) Rig(bool withSlice, bool transmitting, bool running = true)
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            Slice? slice = null;
            if (withSlice)
            {
                var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { false }, null)!;
                slice = (Slice)Activator.CreateInstance(typeof(Slice), Private, null, new object[] { radio }, null)!;
                Assert.Equal("USB", slice.DemodMode); // positive control: starts on voice
                rig.mySlices.Add(slice);
                rig._RXVFO = 0;
            }
            rig.q.MainLoop = running;
            typeof(FlexBase).GetField("_Transmit", Private)!.SetValue(rig, transmitting);
            Assert.Equal(transmitting, rig.Transmit);
            _rigs.Add(rig);
            return (rig, slice);
        }

        /// <summary>The rotation's own stop: Channel 5 on USB, one press up.</summary>
        private static SixtyMeterChannels.RotationStop IntoTheSegmentFromUsb()
        {
            var stop = SixtyMeterChannels.Rotate("US", 4, +1, "USB")!;
            Assert.NotNull(stop.Correction);
            return stop;
        }

        private JsonElement[] Spoken()
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("event").GetString() == "speech")
                .ToArray();
        }

        private string SpokenPastTheGate()
        {
            var e = Assert.Single(Spoken());
            Assert.False(e.GetProperty("gated").GetBoolean(), "the sentence was dropped by the verbosity filter");
            return e.GetProperty("text").GetString()!;
        }

        private static string Segment => $"60 meter digital and CW segment, {5.3515:F4} megahertz";

        [Fact]
        public void Sent_says_switching_and_claims_nothing_more()
        {
            var (rig, slice) = Rig(withSlice: true, transmitting: false);
            var stop = IntoTheSegmentFromUsb();

            string said = SixtyMeterModeDelivery.Deliver(rig, stop.Arrival, stop.Correction!, SpeechIntent.Queue);

            Assert.Equal(1, rig.q.Count); // positive control: the request really went out
            Assert.Equal(Segment + ", digital segment needs CW, switching", said);
            Assert.Equal(said, SpokenPastTheGate());
            Assert.DoesNotContain("set to", said);
            // Even after the write runs, nothing on this path heard the radio:
            // the slice changing is FlexLib's local echo, so the sentence above
            // is all that may be said.
            ((FlexBase.FunctionDel)rig.q.Dequeue().Item)();
            Assert.Equal("CW", slice!.DemodMode);
        }

        [Fact]
        public void Transmitting_says_the_mode_was_not_changed_and_nothing_about_the_frequency()
        {
            var (rig, slice) = Rig(withSlice: true, transmitting: true);
            var stop = IntoTheSegmentFromUsb();

            string said = SixtyMeterModeDelivery.Deliver(rig, stop.Arrival, stop.Correction!, SpeechIntent.Queue);

            Assert.Equal("USB", slice!.DemodMode);
            Assert.Equal(0, rig.q.Count);
            Assert.Equal(Segment + ", digital segment needs CW, mode not changed while transmitting", SpokenPastTheGate());
            Assert.DoesNotContain("nothing was changed", said);
        }

        [Fact]
        public void No_slice_of_ours_says_the_mode_was_not_changed()
        {
            var (rig, _) = Rig(withSlice: false, transmitting: false);
            var stop = IntoTheSegmentFromUsb();

            SixtyMeterModeDelivery.Deliver(rig, stop.Arrival, stop.Correction!, SpeechIntent.Queue);

            Assert.Equal(0, rig.q.Count);
            Assert.Equal(Segment + ", digital segment needs CW, mode not changed", SpokenPastTheGate());
        }

        [Fact]
        public void A_command_loop_that_is_not_running_says_the_mode_was_not_changed()
        {
            var (rig, slice) = Rig(withSlice: true, transmitting: false, running: false);
            var stop = IntoTheSegmentFromUsb();

            SixtyMeterModeDelivery.Deliver(rig, stop.Arrival, stop.Correction!, SpeechIntent.Queue);

            Assert.Equal("USB", slice!.DemodMode);
            Assert.Equal(Segment + ", digital segment needs CW, mode not changed", SpokenPastTheGate());
        }

        [Fact]
        public void The_recorder_sees_the_gate_negative_control()
        {
            // Without this, "gated is false" above could be a recorder that
            // never reports gating at all.
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;
            var (rig, _) = Rig(withSlice: true, transmitting: false);
            var stop = IntoTheSegmentFromUsb();

            SixtyMeterModeDelivery.Deliver(rig, stop.Arrival, stop.Correction!, SpeechIntent.Queue);

            Assert.True(Assert.Single(Spoken()).GetProperty("gated").GetBoolean());
        }

        [Fact]
        public void Mode_and_RXMode_still_refuse_silently_for_their_other_callers()
        {
            // The repair added RequestMode beside them and changed neither.
            var (rig, slice) = Rig(withSlice: true, transmitting: true);
            rig.Mode = "CW";
            Assert.Equal(0, rig.q.Count);

            typeof(FlexBase).GetField("_Transmit", Private)!.SetValue(rig, false);
            rig.RXMode = "CW";
            Assert.Equal(1, rig.q.Count);
            ((FlexBase.FunctionDel)rig.q.Dequeue().Item)();
            Assert.Equal("CW", slice!.DemodMode);
        }

        [Fact]
        public void RequestMode_reports_each_refusal_and_a_send()
        {
            Assert.Equal(ModeRequestOutcome.RefusedTransmitting, Rig(true, transmitting: true).rig.RequestMode("CW"));
            Assert.Equal(ModeRequestOutcome.RefusedNoSlice, Rig(false, transmitting: false).rig.RequestMode("CW"));
            Assert.Equal(ModeRequestOutcome.RefusedNotRunning, Rig(true, transmitting: false, running: false).rig.RequestMode("CW"));
            Assert.Equal(ModeRequestOutcome.Sent, Rig(true, transmitting: false).rig.RequestMode("CW"));
        }
    }
}
