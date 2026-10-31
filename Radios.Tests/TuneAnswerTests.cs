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
            => TuneReading.Unknown with { TxAllowed = allowed, NotReady = notReady, Reason = reason };

        private static readonly TuneReading Idle = TuneReading.Unknown;

        private static TuneReading OnTheAir(PTTSource source, bool anotherClient = false)
            => TuneReading.Unknown with { Transmitting = true, Busy = true, Source = source, AnotherClient = anotherClient };

        // ---- whose RF is it ----

        [Fact]
        public void The_radio_naming_the_tune_as_the_source_is_this_tune()
        {
            Assert.True(TuneAnswer.ThisTuneKeyed(OnTheAir(PTTSource.TUNE), Idle));
        }

        [Fact]
        public void Keying_up_from_idle_on_a_software_source_is_this_tune()
        {
            Assert.True(TuneAnswer.ThisTuneKeyed(OnTheAir(PTTSource.SW), Idle));
            Assert.True(TuneAnswer.ThisTuneKeyed(OnTheAir(PTTSource.None), Idle));
        }

        [Theory]
        [InlineData(PTTSource.Mic)]
        [InlineData(PTTSource.ACC)]
        [InlineData(PTTSource.RCA)]
        public void A_hardware_key_is_not_this_tune_even_from_idle(PTTSource source)
        {
            Assert.False(TuneAnswer.ThisTuneKeyed(OnTheAir(source), Idle));
        }

        [Theory]
        [InlineData(PTTSource.SW)]
        [InlineData(PTTSource.None)]
        [InlineData(PTTSource.RCA)]
        public void A_transmission_already_up_at_the_press_is_not_this_tune(PTTSource source)
        {
            Assert.False(TuneAnswer.ThisTuneKeyed(OnTheAir(source), OnTheAir(source)));
        }

        [Theory]
        [InlineData(PTTSource.TUNE)]
        [InlineData(PTTSource.SW)]
        public void Another_clients_transmission_is_not_this_tune(PTTSource source)
        {
            Assert.False(TuneAnswer.ThisTuneKeyed(OnTheAir(source, anotherClient: true), Idle));
        }

        [Fact]
        public void Off_the_air_is_not_keyed()
        {
            Assert.False(TuneAnswer.ThisTuneKeyed(NotKeyed(allowed: true), Idle));
        }

        // ---- the watch ----

        private static (TuneAnswerWatch watch, Action<TuneReading> radio, Action<long> clock, Action end) Watch(TuneReading atPress)
        {
            TuneReading now = atPress;
            long ms = 0;
            bool owed = true;
            var w = new TuneAnswerWatch(atPress, () => now, () => owed, () => ms);
            return (w, r => now = r, m => ms = m, () => owed = false);
        }

        [Fact]
        public void A_keyed_radio_ends_the_wait_at_once()
        {
            var (w, radio, _, _) = Watch(Idle);
            Assert.Equal(TuneWatchStep.Wait, w.Step());

            radio(OnTheAir(PTTSource.TUNE));

            Assert.Equal(TuneWatchStep.Keyed, w.Step());
        }

        [Fact]
        public void Nothing_is_said_until_the_window_has_passed()
        {
            var (w, radio, clock, _) = Watch(Idle);
            radio(NotKeyed(allowed: false, notReady: true, reason: InterlockReason.OUT_OF_BAND));

            clock(TuneAnswer.WaitMs - 1);
            Assert.Equal(TuneWatchStep.Wait, w.Step());

            clock(TuneAnswer.WaitMs);
            Assert.Equal(TuneWatchStep.NotKeyed, w.Step());
        }

        [Fact]
        public void A_tune_pressed_over_someone_elses_transmission_waits_it_out()
        {
            TuneReading other = OnTheAir(PTTSource.RCA);
            var (w, _, clock, _) = Watch(other);

            Assert.Equal(TuneWatchStep.Wait, w.Step());
            clock(TuneAnswer.WaitMs);

            Assert.Equal(TuneWatchStep.NotKeyed, w.Step());
            Assert.Equal(Lexicon.Get("audio.tune.unconfirmed"), TuneAnswer.NotKeyedSentence(w.Last));
        }

        [Fact]
        public void A_lost_radio_ends_the_watch_with_nothing_to_say()
        {
            var (w, radio, clock, end) = Watch(Idle);
            radio(NotKeyed(allowed: false, notReady: true));
            clock(TuneAnswer.WaitMs);

            end();

            Assert.Equal(TuneWatchStep.Abandon, w.Step());
        }

        [Fact]
        public void A_keyed_report_after_the_session_ended_is_not_believed()
        {
            var (w, radio, _, end) = Watch(Idle);
            radio(OnTheAir(PTTSource.TUNE));
            end();

            Assert.Equal(TuneWatchStep.Abandon, w.Step());
        }

        // ---- the sentences ----

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
            Assert.Equal(Lexicon.Get(key), TuneAnswer.NotKeyedSentence(NotKeyed(allowed: false, reason: reason)));
        }

        [Fact]
        public void An_interlock_that_says_not_ready_is_a_refusal_with_TXAllowed_unread()
        {
            Assert.Equal(Lexicon.Get("audio.tune.refused_out_of_band"),
                TuneAnswer.NotKeyedSentence(NotKeyed(notReady: true, reason: InterlockReason.OUT_OF_BAND)));
        }

        [Theory]
        [InlineData(null)]   // never reported
        [InlineData(true)]   // reported, and the radio still did not key
        public void An_unknown_reading_is_never_a_refusal(bool? allowed)
        {
            string said = TuneAnswer.NotKeyedSentence(NotKeyed(allowed: allowed));

            Assert.Equal(Lexicon.Get("audio.tune.unconfirmed"), said);
            Assert.DoesNotContain("refused", said, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_reason_with_no_refusal_is_not_a_refusal()
        {
            // The interlock keeps its last reason; alone it says nothing.
            Assert.Equal(Lexicon.Get("audio.tune.unconfirmed"),
                TuneAnswer.NotKeyedSentence(NotKeyed(reason: InterlockReason.OUT_OF_BAND)));
        }

        [Fact]
        public void A_not_keyed_sentence_is_queued_so_a_cut_one_comes_back()
        {
            Assert.Equal(Radios.Speech.SpeechIntent.Queue, TuneAnswer.NotKeyedIntent);
            Assert.Contains("Radios.TuneAnswer.NotKeyedIntent", Read("JJFlexWpf/MainWindow.xaml.cs"), StringComparison.Ordinal);
        }

        // ---- the real rig ----

        private Radio ARadio()
        {
            var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { false }, null)!;
            _rig.theRadio = radio;
            return radio;
        }

        private static void Put(object o, string field, object value)
            => o.GetType().GetField(field, Private)!.SetValue(o, value);

        private void RadioReportsTXAllowed(Radio radio)
            => typeof(FlexBase).GetMethod("radioPropertyChangedHandler", Private)!.Invoke(_rig, new object?[]
            {
                radio, new PropertyChangedEventArgs("TXAllowed"),
                new ObservationBinding(radio, new ConnectionAttempt("test-serial")),
            });

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

            RadioReportsTXAllowed(radio);

            TuneReading reading = _rig.ReadTuneStanding();
            Assert.False(reading.TxAllowed);
            Assert.True(reading.RadioRefuses);
        }

        [Fact]
        public void The_rig_reads_the_radios_source_and_its_transmitting_client()
        {
            Radio radio = ARadio();
            Put(_rig, "clientHandle", 7u);
            Put(radio, "_interlockState", InterlockState.Transmitting);
            Put(radio, "_pttSource", PTTSource.TUNE);
            Put(radio, "_txClientHandle", 7u);

            TuneReading mine = _rig.ReadTuneStanding();
            Assert.True(TuneAnswer.ThisTuneKeyed(mine, Idle));

            Put(radio, "_txClientHandle", 9u);
            TuneReading theirs = _rig.ReadTuneStanding();
            Assert.True(theirs.AnotherClient);
            Assert.False(TuneAnswer.ThisTuneKeyed(theirs, Idle));
        }

        [Theory]
        [InlineData("_txAllowedReported = false;\r\n\r\n            // Fresh connection: whatever firmware")]
        [InlineData("rememberCommandRoute(null, null, null);\r\n            _txAllowedReported = false;")]
        [InlineData("if (!nowConnected) _txAllowedReported = false;")]
        public void What_the_radio_said_about_transmitting_ends_with_the_connection(string where)
        {
            Assert.Contains(where.Replace("\r\n", "\n"), Read("Radios/FlexBase.cs").Replace("\r\n", "\n"), StringComparison.Ordinal);
        }

        private static string Read(string relative)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            return File.ReadAllText(Path.Combine(d!.FullName, relative));
        }

        // ---- the prohibition: speech only ----

        [Fact]
        public void The_carrier_command_stays_unconditional()
        {
            string main = Read("JJFlexWpf/MainWindow.xaml.cs");

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
