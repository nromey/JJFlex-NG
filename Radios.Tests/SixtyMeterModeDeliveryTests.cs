using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 60 m mode correction is said only when it is true (#312, repair
    /// round one). Drives a REAL <see cref="FlexBase"/> — its own Mode guard,
    /// its own slice targeting, its own command queue — and reads what got
    /// PAST the speech gate from the silent recording channel (#171), not what
    /// a caller intended to say.
    /// </summary>
    /// <remarks>
    /// <para><b>The false claim, shown red on HEAD 1415e47a before this repair.</b>
    /// A scratch test replayed SixtyMeterChannelNavigate's digital-segment
    /// sequence there (Rotate, then <c>Mode = ModeToSet</c>, then speak the
    /// announcement) against this same offline rig: the transmitting case and
    /// the no-slice case both said "mode set to CW" while the slice stayed on
    /// USB or did not exist (2 of 3 failed; the accepted case passed).</para>
    /// <para><b>What this cannot see.</b> The radio itself: the slice is
    /// FlexLib's object with no connection, so "applied" here means the queued
    /// write ran and the slice now reports the mode — the same standard
    /// TXModeAsReported gives the Fixer (#164), no stronger. Nor NVDA: the
    /// recorder runs with rendering off. That three call sites hand their
    /// arrival to this helper is read in the source, not exercised.</para>
    /// </remarks>
    [Collection(SpeechOutputStaticsCollection.Name)]
    public sealed class SixtyMeterModeDeliveryTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Arrival = "60 meter digital and CW segment, 5.3515 megahertz";

        private readonly string _path;
        private FlexBase? _rig;

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
            if (_rig != null) { try { _rig.Dispose(); } catch { } }
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
            _rig = rig;
            return (rig, slice);
        }

        /// <summary>The command loop's one job, done by the test between polls.</summary>
        private static Func<int, Task> DrainingDelay(FlexBase rig) => _ =>
        {
            while (rig.q.Count > 0) ((FlexBase.FunctionDel)rig.q.Dequeue().Item)();
            return Task.CompletedTask;
        };

        private static Func<int, Task> StalledDelay => _ => Task.CompletedTask;

        private static SixtyMeterChannels.ModeCorrection DigitalSegmentFromUsb()
            => SixtyMeterChannels.GetModeCorrection("US", 5_351_500, "USB")!;

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

        [Fact]
        public async Task Accepted_says_mode_set_and_the_slice_has_the_mode()
        {
            var (rig, slice) = Rig(withSlice: true, transmitting: false);

            string? said = await SixtyMeterModeDelivery.DeliverAsync(rig, Arrival, DigitalSegmentFromUsb(),
                SpeechIntent.Queue, DrainingDelay(rig));

            Assert.Equal("CW", slice!.DemodMode);
            Assert.Equal(Arrival + ", mode set to CW for digital segment", said);
            Assert.Equal(said, SpokenPastTheGate());
        }

        [Fact]
        public async Task Transmitting_never_claims_the_mode_and_says_nothing_was_changed()
        {
            var (rig, slice) = Rig(withSlice: true, transmitting: true);

            string? said = await SixtyMeterModeDelivery.DeliverAsync(rig, Arrival, DigitalSegmentFromUsb(),
                SpeechIntent.Queue, DrainingDelay(rig));

            Assert.Equal("USB", slice!.DemodMode);
            Assert.Equal(0, rig.q.Count);
            Assert.Equal(Arrival + ", digital segment needs CW, and nothing was changed because you are transmitting",
                SpokenPastTheGate());
            Assert.DoesNotContain("mode set to", said);
        }

        [Fact]
        public async Task No_slice_of_ours_never_claims_the_mode()
        {
            var (rig, _) = Rig(withSlice: false, transmitting: false);

            await SixtyMeterModeDelivery.DeliverAsync(rig, Arrival, DigitalSegmentFromUsb(),
                SpeechIntent.Queue, DrainingDelay(rig));

            Assert.Equal(0, rig.q.Count);
            Assert.Equal(Arrival + ", digital segment needs CW, and the mode was not changed", SpokenPastTheGate());
        }

        [Fact]
        public async Task A_command_loop_that_is_not_running_never_claims_the_mode()
        {
            var (rig, slice) = Rig(withSlice: true, transmitting: false, running: false);

            await SixtyMeterModeDelivery.DeliverAsync(rig, Arrival, DigitalSegmentFromUsb(),
                SpeechIntent.Queue, DrainingDelay(rig));

            Assert.Equal("USB", slice!.DemodMode);
            Assert.Equal(Arrival + ", digital segment needs CW, and the mode was not changed", SpokenPastTheGate());
        }

        [Fact]
        public async Task Sent_but_never_applied_says_the_radio_has_not_confirmed()
        {
            // The write went out to the queue and nothing ran it: the
            // enqueue is not proof, so the sentence must not say "set".
            var (rig, slice) = Rig(withSlice: true, transmitting: false);

            string? said = await SixtyMeterModeDelivery.DeliverAsync(rig, Arrival, DigitalSegmentFromUsb(),
                SpeechIntent.Queue, StalledDelay, confirmWindowMs: 200);

            Assert.Equal(1, rig.q.Count); // positive control: it WAS requested
            Assert.Equal("USB", slice!.DemodMode);
            Assert.Equal(Arrival + ", digital segment needs CW, and the radio has not confirmed the mode change", said);
            Assert.Equal(said, SpokenPastTheGate());
        }

        [Fact]
        public async Task A_newer_arrival_retires_one_still_waiting_and_it_says_nothing()
        {
            var (rig, _) = Rig(withSlice: true, transmitting: false);
            Func<int, Task> movedOn = _ => { SixtyMeterModeDelivery.CancelPending(); return Task.CompletedTask; };

            string? said = await SixtyMeterModeDelivery.DeliverAsync(rig, Arrival, DigitalSegmentFromUsb(),
                SpeechIntent.Queue, movedOn);

            Assert.Null(said);
            Assert.Empty(Spoken());
        }

        [Fact]
        public async Task The_recorder_sees_the_gate_negative_control()
        {
            // Without this, "gated is false" above could be a recorder that
            // never reports gating at all.
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;
            var (rig, _) = Rig(withSlice: true, transmitting: false);

            await SixtyMeterModeDelivery.DeliverAsync(rig, Arrival, DigitalSegmentFromUsb(),
                SpeechIntent.Queue, DrainingDelay(rig));

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
    }
}
