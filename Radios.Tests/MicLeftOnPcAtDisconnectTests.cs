using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #861, night question J3, RULED by Noel 2026-11-06 10:57 (wording 11:02,
    /// "while transmitting" 11:03): a mic input put-back WITHHELD at a deliberate
    /// disconnect, because the radio was still transmitting, is SPOKEN, replacing
    /// the 2026-11-04 "logged only". The real FlexBase teardown methods on the
    /// vendored Radio and its fake transport; what went out is read from the
    /// transport, and what would be said from the rig's own record of it. No
    /// socket, no radio, nothing keys, nothing is spoken aloud (no backend).
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class MicLeftOnPcAtDisconnectTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Key = "connect.disconnect.mic_left_on";
        private static readonly MethodInfo NoteRadioMox = typeof(FlexBase).GetMethod("noteRadioMox", Private)!;
        private static readonly MethodInfo PutBackNow = typeof(FlexBase).GetMethod("PutBackNow", Private)!;
        private static readonly Type SwapType = typeof(FlexBase).GetNestedType("MicInputSwap", BindingFlags.NonPublic)!;
        private static readonly FieldInfo SwapField = typeof(FlexBase).GetField("_micInputSwap", Private)!;

        private readonly RadioConfigStaticsScope _scope = new(nameof(MicLeftOnPcAtDisconnectTests));
        private readonly RigOnVendorRadio _f;
        private readonly Thread _loop;
        private volatile bool _stop;

        public MicLeftOnPcAtDisconnectTests()
        {
            _f = new RigOnVendorRadio("0861-0003-0003-0001");
            _f.Vendor.Radio.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Mox") NoteRadioMox.Invoke(_f.Rig, new object[] { _f.Vendor.Radio.Mox });
            };
            _f.Rig.q.MainLoop = true;
            _loop = new Thread(() =>
            {
                while (!_stop)
                {
                    if (_f.Rig.q.Count > 0) { if (_f.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a(); }
                    else Thread.Sleep(2);
                }
            }) { IsBackground = true };
            _loop.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _loop.Join(1000);
            _f.Rig.q.MainLoop = false;
            _f.Dispose();
            _scope.Dispose();
        }

        private void Interlock(string state, uint client = RigOnVendorRadio.OurHandle) =>
            _f.Vendor.Status($"interlock state={state} source=SW tx_client_handle=0x{client:X8}");

        private string[] MicWrites() =>
            _f.Vendor.Transport.Commands.Where(c => c.StartsWith("mic input", StringComparison.OrdinalIgnoreCase)).ToArray();

        private object Swap(string saved) =>
            Activator.CreateInstance(SwapType, BindingFlags.Public | BindingFlags.Instance, null,
                new object?[] { saved, _f.Vendor.Radio, 0L, false }, null)!;

        /// <summary>The stop's own put-back of MIC, the radio on the PC this app set.</summary>
        private void PutBackMic()
        {
            _f.Vendor.Status("transmit mic_selection=PC");
            PutBackNow.Invoke(_f.Rig, new object[] { _f.Vendor.Radio, Swap("MIC"), "test", 0L });
        }

        private static string Sentence(string input) =>
            Lexicon.Get(Key, ScreenReaderOutput.CurrentVerbosity, ("input", input));

        private void AnotherClientIsTransmitting()
        {
            Interlock("TRANSMITTING", client: 0x0BADF00D);
            Assert.True(_f.Vendor.Radio.InterlockState == InterlockState.Transmitting, "positive control: the interlock arrived");
        }

        [Fact]
        public void A_put_back_withheld_at_a_disconnect_because_the_radio_is_transmitting_is_said_after_the_hang_up()
        {
            AnotherClientIsTransmitting();
            Assert.False(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(500), 100));
            PutBackMic();
            Assert.Empty(MicWrites());                                    // withheld, as before
            (Radios.Speech.SpeechIntent Intent, VerbosityLevel Level, string Subject)? route = null;
            _f.Rig.MicLeftOnPcRouteProbe = (i, l, s) => route = (i, l, s);

            _f.Rig.SayMicLeftOnPcAfterTeardown("test");

            Assert.Equal(Sentence("PC"), _f.Rig.LastMicLeftOnPcReport);
            Assert.Equal(1, _f.Rig.MicLeftOnPcReports);
            // The route: queued (survives our own later interrupts), Critical
            // (no verbosity drops it), under its own subject.
            Assert.Equal((Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Critical, Radios.Speech.SpeechSubject.MicInputLeftOnPc), route);
            _f.Rig.SayMicLeftOnPcAfterTeardown("test again");
            Assert.Equal(1, _f.Rig.MicLeftOnPcReports);                  // said once
        }

        [Fact]
        public void A_put_back_made_at_a_disconnect_says_nothing()
        {
            Assert.True(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(50), 50));
            PutBackMic();
            Assert.Contains("mic input MIC", MicWrites());
            _f.Rig.SayMicLeftOnPcAfterTeardown("test");
            Assert.Null(_f.Rig.LastMicLeftOnPcReport);
        }

        [Fact]
        public void A_put_back_withheld_outside_a_disconnect_is_not_said_by_the_disconnect_sentence()
        {
            // A stop of PC audio while transmitting, mid-session: kept owed on
            // its connection, and not the disconnect's sentence.
            AnotherClientIsTransmitting();
            PutBackMic();
            Assert.Empty(MicWrites());
            _f.Rig.SayMicLeftOnPcAfterTeardown("test");
            Assert.Null(_f.Rig.LastMicLeftOnPcReport);
        }

        [Fact]
        public void A_newer_mic_choice_reported_after_the_withhold_is_not_contradicted()
        {
            AnotherClientIsTransmitting();
            Assert.False(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(500), 100));
            PutBackMic();
            _f.Vendor.Status("transmit mic_selection=BAL");               // someone chose since
            _f.Rig.SayMicLeftOnPcAfterTeardown("test");
            Assert.Null(_f.Rig.LastMicLeftOnPcReport);
        }

        [Fact]
        public void A_put_back_owed_from_earlier_is_made_at_the_disconnect_and_said_when_still_withheld()
        {
            // Withheld earlier under a carrier and kept owed (PC audio already
            // stopped); the radio is still transmitting at the disconnect.
            AnotherClientIsTransmitting();
            _f.Vendor.Status("transmit mic_selection=PC");
            SwapField.SetValue(_f.Rig, Swap("MIC"));
            Assert.False(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(500), 100));

            _f.Rig.PayOwedMicPutBackForTeardown("test");

            Assert.Empty(MicWrites());
            _f.Rig.SayMicLeftOnPcAfterTeardown("test");
            Assert.Equal(Sentence("PC"), _f.Rig.LastMicLeftOnPcReport);
        }

        [Fact]
        public void A_put_back_owed_from_earlier_is_paid_at_the_disconnect_when_the_radio_receives()
        {
            _f.Vendor.Status("transmit mic_selection=PC");
            SwapField.SetValue(_f.Rig, Swap("MIC"));
            Assert.True(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(50), 50));

            _f.Rig.PayOwedMicPutBackForTeardown("test");

            Assert.Contains("mic input MIC", MicWrites());
            Assert.Null(SwapField.GetValue(_f.Rig));
            _f.Rig.SayMicLeftOnPcAfterTeardown("test");
            Assert.Null(_f.Rig.LastMicLeftOnPcReport);
        }

        [Fact]
        public void The_sentence_is_Noels_wording_at_each_verbosity()
        {
            Assert.Equal("Mic input still set to PC. If desired, change it when connecting to this radio.",
                Lexicon.Get(Key, VerbosityLevel.Terse, ("input", "PC")));
            Assert.Equal("Mic input still set to PC. If desired, change it when connecting to this radio.",
                Lexicon.Get(Key, VerbosityLevel.Critical, ("input", "PC")));
            Assert.Equal("You disconnected while transmitting. Your microphone input is still set to PC. "
                + "If you need to make a mic input change, set a new input once you connect to this radio.",
                Lexicon.Get(Key, VerbosityLevel.Chatty, ("input", "PC")));
            Assert.Equal(Lexicon.Get(Key, VerbosityLevel.Chatty, ("input", "PC")),
                Lexicon.Get(Key, VerbosityLevel.Diagnostic, ("input", "PC")));
        }

        [Fact]
        public void The_disconnect_pays_what_is_owed_before_the_hang_up_and_speaks_after_it()
        {
            // Disconnect() speaks, plays CW and needs a whole radio, so its order
            // is read from source, as DisconnectPutBackOrderTests does.
            string body = DisconnectBody();
            int stop = body.IndexOf("PCAudio = false;", StringComparison.Ordinal);
            int pay = body.IndexOf("PayOwedMicPutBackForTeardown(\"Disconnect\");", StringComparison.Ordinal);
            int drain = body.IndexOf("DrainQueuedMicPutBack(\"Disconnect\"", StringComparison.Ordinal);
            int hangUp = body.IndexOf("theRadio.Disconnect();", StringComparison.Ordinal);
            int say = body.IndexOf("SayMicLeftOnPcAfterTeardown(\"Disconnect\");", StringComparison.Ordinal);
            int end = body.IndexOf("EndTeardownUnkey();", StringComparison.Ordinal);
            Assert.True(stop > 0 && pay > stop && drain > pay, "the owed put-back is made after PC audio stops and before the queued one is drained");
            Assert.True(hangUp > drain && say > hangUp && end > say, "said after the hang-up, before the teardown's record is cleared");
        }

        private static string DisconnectBody()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "Radios", "FlexBase.cs"));
            int start = source.IndexOf("public void Disconnect()", StringComparison.Ordinal);
            int next = source.IndexOf("public static int SkFarewellWaitMs()", start, StringComparison.Ordinal);
            Assert.True(start > 0 && next > start);
            return source.Substring(start, next - start);
        }
    }
}
