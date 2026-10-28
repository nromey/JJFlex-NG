using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// Which ways of checking your audio a radio and a connection offer, and
    /// which one is chosen (#835). Pure, so a test can walk every case without a
    /// window or a radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A method is left out only for a LASTING reason</b> (ruled by Noel
    /// 2026-10-27 13:57, with the design): the connection is SmartLink, the
    /// radio has one receiver, or it lists no transverter port. A PASSING
    /// reason (the radio is transmitting, a take is already running, recovery is
    /// pending, the mode is not SSB, no slice is free) leaves the method offered
    /// and refuses at Start, saying why. A method left out is never in the tab
    /// order, which is the point: an operator should not meet a choice their
    /// radio cannot make.
    /// </para>
    /// <para>
    /// <b>Over SmartLink only the first three are offered.</b> Off the air needs
    /// IQ, which SmartLink does not carry (13:39, and the 2026-10-22 08:08 "no IQ
    /// over SmartLink"). Loopback needs no IQ but is LOCAL ONLY until it has
    /// been benched remotely (ruled 15:32: "local only until we are able to
    /// bench it").
    /// </para>
    /// <para>
    /// <b>Saved choices keep their meaning.</b> The method is saved per radio,
    /// and the same radio can be reached both ways. A saved method the
    /// connection does not offer falls back to that connection's default, for
    /// this session only and quietly; the saved choice is not overwritten.
    /// </para>
    /// </remarks>
    public static class AudioCheckMethods
    {
        /// <summary>
        /// The order the methods are offered in: what you hear, from nothing to
        /// the most involved. Not the enum order, which is fixed by saved files.
        /// </summary>
        public static IReadOnlyList<AudioCheckListenMethods> DisplayOrder { get; } = new[]
        {
            AudioCheckListenMethods.MicTest,
            AudioCheckListenMethods.Monitor,
            AudioCheckListenMethods.RecordPlayback,
            AudioCheckListenMethods.OffAir,
            AudioCheckListenMethods.Loopback,
        };

        /// <summary>
        /// The reasons <c>FlexBase.OffAirListenbackRefusalReason</c> gives that
        /// are a fact about the radio or the connection rather than about this
        /// moment. Kept as ONE-SCU-NOT-CONFIRMED on purpose: whether a
        /// one-receiver radio can make a take is still to be settled at the
        /// bench, and until then such a radio is refused as before (13:48).
        /// </summary>
        public static bool IsLastingOffAirReason(string reason) => reason is
            "no-radio" or "smartlink-iq-unavailable" or "one-scu-not-confirmed" or "no-transverter-port";

        /// <summary>The methods this radio and connection offer, in display order.</summary>
        /// <param name="connected">A radio is attached.</param>
        /// <param name="remote">The connection is SmartLink.</param>
        /// <param name="offAirRefusal">The radio's <c>OffAirListenbackRefusalReason</c>, or empty.</param>
        /// <param name="loopbackSupported">The radio's <c>LoopbackSupported</c>: two receivers and a transverter port.</param>
        public static IReadOnlyList<AudioCheckListenMethods> Offered(bool connected, bool remote,
            string offAirRefusal, bool loopbackSupported)
        {
            var offered = new List<AudioCheckListenMethods>
            {
                AudioCheckListenMethods.MicTest,
                AudioCheckListenMethods.Monitor,
                AudioCheckListenMethods.RecordPlayback,
            };
            if (!connected || remote) return offered;
            if (!IsLastingOffAirReason(offAirRefusal ?? "")) offered.Add(AudioCheckListenMethods.OffAir);
            if (loopbackSupported) offered.Add(AudioCheckListenMethods.Loopback);
            return offered;
        }

        /// <summary>
        /// The method a connection starts on when the operator has chosen none.
        /// Record and play back over SmartLink (ruled 2026-10-22 08:08): monitor
        /// audio arrives delayed there. Monitor otherwise, as it always was.
        /// </summary>
        public static AudioCheckListenMethods DefaultFor(bool remote) =>
            remote ? AudioCheckListenMethods.RecordPlayback : AudioCheckListenMethods.Monitor;

        /// <summary>
        /// Was the saved method chosen by the operator, or is it the file's
        /// default? Files written before the chosen flag existed carry Monitor
        /// either way, so only a saved method OTHER than Monitor proves a choice.
        /// </summary>
        public static bool WasChosen(AudioCheckListenMethods saved, bool chosenFlag) =>
            chosenFlag || saved != AudioCheckListenMethods.Monitor;

        /// <summary>The method to show: the saved one if it was chosen and is offered, otherwise the connection's default.</summary>
        public static AudioCheckListenMethods Resolve(AudioCheckListenMethods saved, bool chosen,
            IReadOnlyList<AudioCheckListenMethods> offered, bool remote)
        {
            if (chosen && Contains(offered, saved)) return saved;
            var fallback = DefaultFor(remote);
            return Contains(offered, fallback) ? fallback : offered.Count > 0 ? offered[0] : fallback;
        }

        /// <summary>Methods whose check obeys the "Transmit power during checks" choice.</summary>
        /// <remarks>
        /// Test your microphone always runs at zero power, so its name stays
        /// true (14:57). Off the air and Loopback run the one-watt transverter
        /// arrangement, which owns power. For those three the choice would be a
        /// control that does nothing, so it is hidden.
        /// </remarks>
        public static bool UsesCheckPower(AudioCheckListenMethods method) =>
            method is AudioCheckListenMethods.Monitor or AudioCheckListenMethods.RecordPlayback;

        /// <summary>Methods that leave something to play afterwards.</summary>
        public static bool MakesTake(AudioCheckListenMethods method) =>
            method is AudioCheckListenMethods.RecordPlayback or AudioCheckListenMethods.OffAir;

        // ── Explaining the method (Noel 2026-10-28 00:15, ruled 00:20) ──────────
        // Ctrl+F1 on the method says what the control is and what the method
        // shown right now does; F1 opens the help at that method's section.

        /// <summary>
        /// The lexicon key of the method's one description: what Start does,
        /// what you hear, where the verdict appears, and what the method needs.
        /// </summary>
        /// <remarks>
        /// ONE account per method. The status line under the method, the
        /// Ctrl+F1 answer and the method's section on the help page all say
        /// these words, and a test holds the help page to them verbatim, so the
        /// three cannot drift apart. The keys keep their <c>status.</c> names
        /// because the status line was their first reader.
        /// </remarks>
        // DRAFT PROSE (#835): awaiting Noel. The descriptions, in Radios/Lexicon/audio.json.
        public static string DescriptionKey(AudioCheckListenMethods method) => method switch
        {
            AudioCheckListenMethods.MicTest => "audio.check.status.mic_test",
            AudioCheckListenMethods.RecordPlayback => "audio.check.status.record",
            AudioCheckListenMethods.OffAir => "audio.check.status.off_air",
            AudioCheckListenMethods.Loopback => "audio.check.status.loopback",
            _ => "audio.check.status.monitor"
        };

        /// <summary>The method's description, in its current words.</summary>
        public static string Description(AudioCheckListenMethods method) => Lexicon.Get(DescriptionKey(method));

        /// <summary>
        /// What Ctrl+F1 on the method says: one sentence about the control, then
        /// the description of the method shown.
        /// </summary>
        // DRAFT PROSE (#835): awaiting Noel. The control sentence is audio.check.method_help.
        public static string ContextHelp(AudioCheckListenMethods method) =>
            Lexicon.Get("audio.check.method_help") + " " + Description(method);

        /// <summary>The help context the dialog's page is mapped under, with no method's section.</summary>
        public const string HelpPageContext = "CheckYourAudio";

        /// <summary>
        /// The help context F1 asks for while <paramref name="method"/> is shown:
        /// that method's section on the page. A method with no section opens the
        /// page itself.
        /// </summary>
        /// <remarks>
        /// Each is a plain page-plus-anchor entry in <c>HelpLauncher.ContextMap</c>,
        /// so whatever launcher replaces the compiled help carries it over as it
        /// stands.
        /// </remarks>
        public static string HelpContext(AudioCheckListenMethods method) => method switch
        {
            AudioCheckListenMethods.MicTest => "CheckYourAudioMicTest",
            AudioCheckListenMethods.Monitor => "CheckYourAudioMonitor",
            AudioCheckListenMethods.RecordPlayback => "CheckYourAudioRecord",
            AudioCheckListenMethods.OffAir => "CheckYourAudioOffAir",
            AudioCheckListenMethods.Loopback => "CheckYourAudioLoopback",
            _ => HelpPageContext
        };

        private static bool Contains(IReadOnlyList<AudioCheckListenMethods> list, AudioCheckListenMethods m)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == m) return true;
            return false;
        }
    }
}
