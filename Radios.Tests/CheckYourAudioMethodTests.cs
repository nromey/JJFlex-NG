using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Which ways of checking your audio a radio and a connection offer, which
    /// one is shown, and that the saved choice keeps its meaning (#835).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are Noel's, 2026-10-27: a method is left out only for a
    /// LASTING reason (13:57); over SmartLink nothing take-based is offered
    /// (13:39) and Loopback is local only (15:32); Record yourself is the
    /// SmartLink default (2026-10-22 08:08); Test your microphone always runs at
    /// zero power (14:57); new enum values go at the end so saved values keep
    /// their meaning.
    /// </para>
    /// <para>
    /// <b>What these cannot see:</b> the window itself. That the cycle really
    /// rebuilds, that a collapsed section really leaves the tab order, and what
    /// NVDA says, need a press at the bench. These tests do not compile against
    /// the base commit (<c>44cbe31a</c>): <c>AudioCheckMethods</c>, the two new
    /// enum values and the chosen flag did not exist. That is the red run.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class CheckYourAudioMethodTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope;
        private string Dir => _scope.Directory;

        public CheckYourAudioMethodTests()
        {
            _scope = new RadioConfigStaticsScope(nameof(CheckYourAudioMethodTests));
        }

        public void Dispose() => _scope.Dispose();

        // Fix round 2026-10-28: this computer's microphone leads every offer (ruled 20:30).
        private static readonly AudioCheckListenMethods[] SmartLinkThree =
        {
            AudioCheckListenMethods.ComputerMic,
            AudioCheckListenMethods.MicTest, AudioCheckListenMethods.Monitor, AudioCheckListenMethods.RecordPlayback,
        };

        [Fact]
        public void Saved_values_keep_their_numbers_and_the_new_ones_go_at_the_end()
        {
            // The per-radio file stores the name, but a number is what the old
            // Workshop cast a cycle index to, and a renumbering is the change
            // that silently reinterprets a saved choice.
            Assert.Equal(0, (int)AudioCheckListenMethods.Monitor);
            Assert.Equal(1, (int)AudioCheckListenMethods.RecordPlayback);
            Assert.Equal(2, (int)AudioCheckListenMethods.OffAir);
            Assert.Equal(3, (int)AudioCheckListenMethods.MicTest);
            Assert.Equal(4, (int)AudioCheckListenMethods.Loopback);
            Assert.Equal(5, (int)AudioCheckListenMethods.ComputerMic);
            Assert.Equal(6, Enum.GetValues<AudioCheckListenMethods>().Length);
        }

        [Fact]
        public void Every_method_is_offered_somewhere_and_shown_in_the_ruled_order()
        {
            // An escalation (ruled 2026-10-28 20:30): the computer's microphone,
            // the radio's at zero power, live, recorded, then off the air.
            Assert.Equal(new[]
            {
                AudioCheckListenMethods.ComputerMic,
                AudioCheckListenMethods.MicTest, AudioCheckListenMethods.Monitor, AudioCheckListenMethods.RecordPlayback,
                AudioCheckListenMethods.OffAir, AudioCheckListenMethods.Loopback,
            }, AudioCheckMethods.DisplayOrder);
            Assert.Equal(AudioCheckMethods.DisplayOrder, AudioCheckMethods.Offered(true, false, "", true));
        }

        [Theory]
        // Over SmartLink: the three, whatever the radio could do locally. Off
        // the air needs IQ; Loopback is local only until benched remotely.
        [InlineData(true, "", true)]
        [InlineData(true, "smartlink-iq-unavailable", true)]
        [InlineData(true, "one-scu-not-confirmed", false)]
        public void Over_SmartLink_only_the_three_are_offered(bool remote, string refusal, bool loopback)
        {
            Assert.Equal(SmartLinkThree, AudioCheckMethods.Offered(true, remote, refusal, loopback));
        }

        [Theory]
        [InlineData("one-scu-not-confirmed")]
        [InlineData("no-transverter-port")]
        [InlineData("no-radio")]
        public void A_lasting_reason_takes_Off_the_air_out(string refusal)
        {
            Assert.True(AudioCheckMethods.IsLastingOffAirReason(refusal));
            Assert.DoesNotContain(AudioCheckListenMethods.OffAir, AudioCheckMethods.Offered(true, false, refusal, false));
        }

        [Theory]
        // Passing reasons refuse at Start and say why; they never remove the
        // method, or the operator would watch it vanish and come back.
        [InlineData("radio-busy")]
        [InlineData("take-already-running")]
        [InlineData("recovery-pending")]
        [InlineData("ssb-only")]
        [InlineData("no-free-slice")]
        public void A_passing_reason_leaves_Off_the_air_offered(string reason)
        {
            Assert.False(AudioCheckMethods.IsLastingOffAirReason(reason));
            Assert.Contains(AudioCheckListenMethods.OffAir, AudioCheckMethods.Offered(true, false, reason, true));
        }

        [Fact]
        public void Loopback_follows_LoopbackSupported_and_nothing_else_locally()
        {
            Assert.Contains(AudioCheckListenMethods.Loopback, AudioCheckMethods.Offered(true, false, "", true));
            Assert.DoesNotContain(AudioCheckListenMethods.Loopback, AudioCheckMethods.Offered(true, false, "", false));
        }

        [Fact]
        public void With_no_radio_only_this_computers_microphone_is_offered()
        {
            // Ruled 2026-10-28 20:34: with no connection the window opens
            // offering only the computer-mic method, and saves nothing.
            Assert.Equal(new[] { AudioCheckListenMethods.ComputerMic },
                AudioCheckMethods.Offered(false, false, "no-radio", false));
            Assert.Equal(AudioCheckListenMethods.ComputerMic,
                AudioCheckMethods.Resolve(AudioCheckListenMethods.Monitor, chosen: true,
                    AudioCheckMethods.Offered(false, false, "no-radio", false), remote: false));
            Assert.False(AudioCheckMethods.KeysTheRadio(AudioCheckListenMethods.ComputerMic));
            Assert.False(AudioCheckMethods.UsesCheckPower(AudioCheckListenMethods.ComputerMic));
            Assert.All(AudioCheckMethods.DisplayOrder.Where(m => m != AudioCheckListenMethods.ComputerMic),
                m => Assert.True(AudioCheckMethods.KeysTheRadio(m)));
        }

        [Fact]
        public void The_SmartLink_default_is_Record_yourself_and_the_local_one_Monitor()
        {
            Assert.Equal(AudioCheckListenMethods.RecordPlayback, AudioCheckMethods.DefaultFor(remote: true));
            Assert.Equal(AudioCheckListenMethods.Monitor, AudioCheckMethods.DefaultFor(remote: false));
        }

        [Fact]
        public void A_never_chosen_method_starts_on_the_connections_default()
        {
            var local = AudioCheckMethods.Offered(true, false, "", true);
            var smart = AudioCheckMethods.Offered(true, true, "", true);
            Assert.Equal(AudioCheckListenMethods.Monitor,
                AudioCheckMethods.Resolve(AudioCheckListenMethods.Monitor, chosen: false, local, remote: false));
            Assert.Equal(AudioCheckListenMethods.RecordPlayback,
                AudioCheckMethods.Resolve(AudioCheckListenMethods.Monitor, chosen: false, smart, remote: true));
        }

        [Fact]
        public void A_chosen_method_the_connection_offers_is_kept()
        {
            var smart = AudioCheckMethods.Offered(true, true, "", true);
            Assert.Equal(AudioCheckListenMethods.Monitor,
                AudioCheckMethods.Resolve(AudioCheckListenMethods.Monitor, chosen: true, smart, remote: true));
            Assert.Equal(AudioCheckListenMethods.MicTest,
                AudioCheckMethods.Resolve(AudioCheckListenMethods.MicTest, chosen: true, smart, remote: true));
        }

        [Theory]
        [InlineData(AudioCheckListenMethods.OffAir)]
        [InlineData(AudioCheckListenMethods.Loopback)]
        public void A_saved_method_SmartLink_does_not_offer_falls_back_to_Record_yourself(AudioCheckListenMethods saved)
        {
            var smart = AudioCheckMethods.Offered(true, true, "", true);
            Assert.Equal(AudioCheckListenMethods.RecordPlayback,
                AudioCheckMethods.Resolve(saved, chosen: true, smart, remote: true));
            // And locally the same saved choice comes straight back.
            var local = AudioCheckMethods.Offered(true, false, "", true);
            Assert.Equal(saved, AudioCheckMethods.Resolve(saved, chosen: true, local, remote: false));
        }

        [Fact]
        public void A_saved_method_a_one_receiver_radio_does_not_offer_falls_back_to_Monitor_locally()
        {
            var oneScu = AudioCheckMethods.Offered(true, false, "one-scu-not-confirmed", false);
            Assert.Equal(AudioCheckListenMethods.Monitor,
                AudioCheckMethods.Resolve(AudioCheckListenMethods.OffAir, chosen: true, oneScu, remote: false));
        }

        [Fact]
        public void Only_a_method_other_than_Monitor_proves_a_choice_in_an_old_file()
        {
            Assert.False(AudioCheckMethods.WasChosen(AudioCheckListenMethods.Monitor, chosenFlag: false));
            Assert.True(AudioCheckMethods.WasChosen(AudioCheckListenMethods.Monitor, chosenFlag: true));
            Assert.True(AudioCheckMethods.WasChosen(AudioCheckListenMethods.RecordPlayback, chosenFlag: false));
            Assert.True(AudioCheckMethods.WasChosen(AudioCheckListenMethods.OffAir, chosenFlag: false));
        }

        [Theory]
        // Test your microphone always runs at zero power (14:57); Off the air
        // and Loopback run the one-watt arrangement, which owns power.
        [InlineData(AudioCheckListenMethods.MicTest, false)]
        [InlineData(AudioCheckListenMethods.Monitor, true)]
        [InlineData(AudioCheckListenMethods.RecordPlayback, true)]
        [InlineData(AudioCheckListenMethods.OffAir, false)]
        [InlineData(AudioCheckListenMethods.Loopback, false)]
        public void The_check_power_choice_is_shown_only_where_it_acts(AudioCheckListenMethods method, bool uses)
        {
            Assert.Equal(uses, AudioCheckMethods.UsesCheckPower(method));
        }

        [Theory]
        [InlineData(AudioCheckListenMethods.MicTest, false)]
        [InlineData(AudioCheckListenMethods.Monitor, false)]
        [InlineData(AudioCheckListenMethods.RecordPlayback, true)]
        [InlineData(AudioCheckListenMethods.OffAir, true)]
        // The loopback's take lives on its listening slice, which goes when
        // the check does, so there is nothing left for Play last take.
        [InlineData(AudioCheckListenMethods.Loopback, false)]
        public void Play_last_take_belongs_to_the_methods_that_leave_a_take(AudioCheckListenMethods method, bool makes)
        {
            Assert.Equal(makes, AudioCheckMethods.MakesTake(method));
        }

        [Fact]
        public void Choosing_a_method_marks_it_chosen_and_saving_other_prefs_does_not()
        {
            const string id = "5555-6666-7777-8888";
            Assert.False(RadioConfig.LoadForRadio(id).AudioCheckListenMethodChosen);

            // Power and watts saved without a method: a SmartLink fallback shown
            // in the window must not become the operator's choice.
            Assert.True(RadioConfig.RecordAudioCheckPref(id, AudioCheckPref.PowerMode, AudioCheckListenMethods.Monitor, AudioCheckPowerModes.LowPower, 5, false));
            var afterPower = RadioConfig.LoadForRadio(id);
            Assert.False(afterPower.AudioCheckListenMethodChosen);
            Assert.Equal(AudioCheckListenMethods.Monitor, afterPower.AudioCheckListenMethod);

            Assert.True(RadioConfig.RecordAudioCheckPref(id, AudioCheckPref.ListenMethod, AudioCheckListenMethods.MicTest, AudioCheckPowerModes.LowPower, 5, false));
            var afterMethod = RadioConfig.LoadForRadio(id);
            Assert.True(afterMethod.AudioCheckListenMethodChosen);
            Assert.Equal(AudioCheckListenMethods.MicTest, afterMethod.AudioCheckListenMethod);
            Assert.Equal(AudioCheckPowerModes.LowPower, afterMethod.AudioCheckPowerMode);
        }

        [Fact]
        public void A_config_written_before_the_flag_reads_as_not_chosen_and_keeps_its_method()
        {
            const string id = "1212-3434-5656-7878";
            var dir = Path.Combine(Dir, "radios", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<RadioConfig><Version>1</Version><RadioId>" + id + "</RadioId>" +
                "<AudioCheckListenMethod>RecordPlayback</AudioCheckListenMethod></RadioConfig>");

            var cfg = RadioConfig.LoadForRadio(id);
            Assert.Equal(AudioCheckListenMethods.RecordPlayback, cfg.AudioCheckListenMethod);
            Assert.False(cfg.AudioCheckListenMethodChosen);
            // ...and is still treated as chosen, because it is not the default.
            Assert.True(AudioCheckMethods.WasChosen(cfg.AudioCheckListenMethod, cfg.AudioCheckListenMethodChosen));
        }
    }
}
