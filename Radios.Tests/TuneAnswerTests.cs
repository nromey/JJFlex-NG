using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #447: tune announces what the radio did. A radio that refused says so
    /// with its reason, an unknown reading is never a refusal, and the carrier
    /// command stays unconditional.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TuneAnswerTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        public TuneAnswerTests() => Lexicon.Load(Lexicon.Partitions);

        public void Dispose() => _rig.Dispose();

        private static TuneReading NotKeyed(bool? allowed = null, bool notReady = false,
                                            InterlockReason reason = InterlockReason.None)
            => new(false, allowed, notReady, reason);

        [Fact]
        public void A_keyed_radio_says_tune_on_at_once()
        {
            var keyed = new TuneReading(true, null, false, InterlockReason.None);

            Assert.True(TuneAnswer.Settled(keyed, 0));
            Assert.Equal(Lexicon.Get("audio.tune.tune_on"), TuneAnswer.Sentence(keyed));
        }

        [Fact]
        public void Keyed_beats_every_refusal_signal_because_it_is_what_the_radio_did()
        {
            var keyedAnyway = new TuneReading(true, false, true, InterlockReason.OUT_OF_BAND);

            Assert.False(keyedAnyway.RadioRefuses);
            Assert.Equal(Lexicon.Get("audio.tune.tune_on"), TuneAnswer.Sentence(keyedAnyway));
        }

        [Fact]
        public void Nothing_is_said_until_the_window_has_passed()
        {
            var refusing = NotKeyed(allowed: false, notReady: true, reason: InterlockReason.OUT_OF_BAND);

            Assert.False(TuneAnswer.Settled(refusing, TuneAnswer.WaitMs - 1));
            Assert.True(TuneAnswer.Settled(refusing, TuneAnswer.WaitMs));
        }

        [Theory]
        [InlineData(InterlockReason.OUT_OF_BAND, "audio.tune.refused_out_of_band")]
        [InlineData(InterlockReason.BAD_MODE, "audio.tune.refused_bad_mode")]
        [InlineData(InterlockReason.PA_RANGE, "audio.tune.refused_pa_range")]
        [InlineData(InterlockReason.CLIENT_TX_INHIBIT, "audio.tune.refused_inhibited")]
        [InlineData(InterlockReason.XVTR_RX_ONLY, "audio.tune.refused_rx_only")]
        [InlineData(InterlockReason.NO_TX_ASSIGNED, "audio.tune.refused_no_tx_slice")]
        [InlineData(InterlockReason.TGXL, "audio.tune.refused_amplifier")]
        [InlineData(InterlockReason.TUNED_TOO_FAR, "audio.tune.refused")]
        [InlineData(InterlockReason.None, "audio.tune.refused")]
        public void A_refusal_carries_the_radios_reason(InterlockReason reason, string key)
        {
            Assert.Equal(Lexicon.Get(key), TuneAnswer.Sentence(NotKeyed(allowed: false, reason: reason)));
        }

        [Fact]
        public void An_interlock_that_says_not_ready_is_a_refusal_with_TXAllowed_unread()
        {
            Assert.Equal(Lexicon.Get("audio.tune.refused_out_of_band"),
                TuneAnswer.Sentence(NotKeyed(notReady: true, reason: InterlockReason.OUT_OF_BAND)));
        }

        [Theory]
        [InlineData(null)]   // never reported
        [InlineData(true)]   // reported, and the radio still did not key
        public void An_unknown_reading_is_never_a_refusal(bool? allowed)
        {
            string said = TuneAnswer.Sentence(NotKeyed(allowed: allowed));

            Assert.Equal(Lexicon.Get("audio.tune.unconfirmed"), said);
            Assert.DoesNotContain("refused", said, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_reason_with_no_refusal_is_not_a_refusal()
        {
            // The interlock keeps its last reason; alone it says nothing.
            Assert.Equal(Lexicon.Get("audio.tune.unconfirmed"),
                TuneAnswer.Sentence(NotKeyed(reason: InterlockReason.OUT_OF_BAND)));
        }

        // ---- the real rig ----

        private Radio ARadio()
        {
            var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { false }, null)!;
            _rig.theRadio = radio;
            return radio;
        }

        [Fact]
        public void With_no_radio_the_reading_is_unknown()
        {
            Assert.Equal(TuneReading.Unknown, _rig.ReadTuneStanding());
            Assert.False(TuneReading.Unknown.RadioRefuses);
        }

        [Fact]
        public void FlexLibs_constructed_false_is_not_read_as_the_radio_saying_no()
        {
            ARadio();

            TuneReading reading = _rig.ReadTuneStanding();

            Assert.Null(reading.TxAllowed);
            Assert.False(reading.RadioRefuses);
        }

        [Fact]
        public void Once_the_radio_has_reported_TXAllowed_its_answer_counts()
        {
            Radio radio = ARadio();

            typeof(FlexBase).GetMethod("radioPropertyChangedHandler", Private)!.Invoke(_rig, new object?[]
            {
                radio, new PropertyChangedEventArgs("TXAllowed"),
                new ObservationBinding(radio, new ConnectionAttempt("test-serial")),
            });

            TuneReading reading = _rig.ReadTuneStanding();
            Assert.False(reading.TxAllowed);
            Assert.True(reading.RadioRefuses);
        }

        // ---- the prohibition: speech only ----

        [Fact]
        public void The_carrier_command_stays_unconditional()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            string main = File.ReadAllText(Path.Combine(d!.FullName, "JJFlexWpf", "MainWindow.xaml.cs"));

            int start = main.IndexOf("public void ToggleTuneCarrier()", StringComparison.Ordinal);
            int key = main.IndexOf("RigControl.TxTune = newState;", start, StringComparison.Ordinal);
            string before = main.Substring(start, key - start);

            // Nothing between the debounce and the write reads the radio's answer.
            Assert.DoesNotContain("ReadTuneStanding", before, StringComparison.Ordinal);
            Assert.DoesNotContain("TuneAnswer", before, StringComparison.Ordinal);
            Assert.DoesNotContain("TXAllowed", before, StringComparison.Ordinal);
        }
    }
}
