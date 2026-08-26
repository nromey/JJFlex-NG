using System;

namespace Radios
{
    /// <summary>
    /// Decisions about whether a transmission in progress is safe, kept as pure
    /// functions so they can be tested without a radio, a window or a thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not simply a private method on the PTT controller.</b> It
    /// was, briefly, and that is the shape this project keeps getting caught by:
    /// a warning whose decision lives inside a WPF class, reachable only by
    /// keying a real transmitter into a real fault. Such a warning compiles,
    /// reviews clean, and is indistinguishable from a working one right up until
    /// the day somebody needs it and hears nothing. The Alt+L binding that
    /// shipped completely dead on 2026-08-13 was the same shape.
    /// </para>
    /// <para>
    /// So the judgement lives here, where a test can put numbers in and read a
    /// verdict out, and the controller keeps only the parts that genuinely need
    /// a radio: reading the meters, playing the earcon, speaking the sentence.
    /// </para>
    /// </remarks>
    public static class TransmitSafety
    {
        /// <summary>
        /// THE reflected-power threshold, as a percentage of forward power.
        /// This is the one home; everything that judges reflected share names
        /// this constant or is tested against it.
        /// </summary>
        /// <remarks>
        /// MEASURED on 2026-08-22, not guessed. The bench 8600 transmitting into
        /// an EMPTY antenna connector — the dummy load was on the other port —
        /// sent 76 percent of its power straight back. Minutes later, into the
        /// load, 0.05 percent. Three orders of magnitude apart, so 40 percent
        /// sits in an enormous empty gap rather than on a judgement call. For
        /// scale it is a standing wave ratio near 5 to 1, past anything a
        /// working antenna presents.
        /// <para><b>Three consumers, kept in step by a test, not by this
        /// comment.</b> (1) The live PTT warning, through
        /// <see cref="ReflectedWarnFraction"/>. (2) The power-coming-back rule
        /// in tx-chain-rules.txt — a data file that cannot reference this
        /// constant, so ReflectedThresholdAgreementTests parses the shipped
        /// file and fails if the two drift. (3) The transmit-check tune probe's
        /// fallback, <c>TxTuneProbe.ReflectedSuspectPercent</c>, which is
        /// DELIBERATELY STRICTER and derives from this constant so the
        /// relationship is visible — see its own remarks. An operator who hears
        /// the live warning and then runs a check must not be given two
        /// different answers about the same station.</para>
        /// <para>History: this invariant was documented for the first two
        /// consumers, honoured for months, and then quietly broken when the
        /// probe's fallback was written at 20 without reading the note (#237).
        /// Hence the test — a comment asked future editors to keep the figures
        /// in step, and a future editor did not.</para>
        /// </remarks>
        public const double ReflectedWarnPercent = 40.0;

        /// <summary>
        /// <see cref="ReflectedWarnPercent"/> as a fraction between 0 and 1,
        /// for the live warning path which works in fractions.
        /// </summary>
        public const float ReflectedWarnFraction = (float)(ReflectedWarnPercent / 100.0);

        /// <summary>
        /// Seconds of transmit before the reflected-power warning may speak.
        /// <para>Two, where the audio-quality warnings wait five, and the
        /// difference is the point: a hot microphone is an embarrassment the
        /// operator can fix next over, while power coming back is arriving at
        /// the finals right now. One tick of settling is enough to reject a
        /// meter that has not caught up with key-down.</para>
        /// </summary>
        public const int ReflectedWarnSeconds = 2;

        /// <summary>
        /// Forward power below which a reflected fraction means nothing,
        /// because a meter wandering around zero can produce any ratio at all.
        /// </summary>
        public const float ReflectedWarnMinWatts = 1f;

        /// <summary>
        /// How much of the forward power is coming back, from 0 to 1, or NaN
        /// when the question cannot be answered.
        /// </summary>
        /// <remarks>
        /// NaN rather than 0 when there is too little power to judge. Returning
        /// a comfortable number for "no idea" is the exact defect this whole
        /// area exists to fix — the radio's own SWR meter answers 1.008 when it
        /// has nothing useful to say, and two bench sessions were measured
        /// through that reassurance.
        /// </remarks>
        public static float ReflectedFractionOf(float forwardWatts, float reflectedWatts)
        {
            if (float.IsNaN(forwardWatts) || float.IsNaN(reflectedWatts)) return float.NaN;
            if (forwardWatts < 0.05f) return float.NaN;
            if (reflectedWatts < 0f) return 0f;
            return Math.Min(reflectedWatts / forwardWatts, 1f);
        }

        /// <summary>
        /// Whether the operator should be told, right now, that their power is
        /// coming back instead of leaving.
        /// </summary>
        /// <param name="forwardWatts">Forward power in WATTS, not dBm.</param>
        /// <param name="reflectedWatts">Reflected power in WATTS, not dBm.</param>
        /// <param name="txSeconds">Seconds transmitting, in any keying state.</param>
        /// <param name="tuning">True while the antenna tuner is running a cycle.</param>
        /// <param name="alreadyWarned">True once this transmission has spoken.</param>
        public static bool ShouldWarnReflected(
            float forwardWatts, float reflectedWatts, int txSeconds, bool tuning, bool alreadyWarned)
        {
            // Once per transmission. A warning that repeats every second while
            // the operator is trying to act on it is noise, and noise is how a
            // warning gets switched off.
            if (alreadyWarned) return false;

            if (txSeconds < ReflectedWarnSeconds) return false;

            // A tune cycle transmits into a deliberately bad match and walks
            // toward a good one, so high reflected power during one is the tuner
            // doing its job. Without this, every routine tune-up would announce
            // a disconnected antenna — and an operator who has learned to ignore
            // a warning is worse off than one who never had it.
            if (tuning) return false;

            if (float.IsNaN(forwardWatts) || forwardWatts < ReflectedWarnMinWatts) return false;

            float back = ReflectedFractionOf(forwardWatts, reflectedWatts);
            if (float.IsNaN(back)) return false;

            return back > ReflectedWarnFraction;
        }

        /// <summary>
        /// The sentence to speak, naming the transmit antenna when one is known
        /// and sharpening the wording when the operator has declared a dummy
        /// load.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Naming the port is the whole point rather than a nicety. "Check the
        /// antenna" is advice; "check ANT1" is an instruction — and the operator
        /// this is built for is blind and cannot read the labels moulded into
        /// the back panel.
        /// </para>
        /// <para>
        /// The declared-load variant exists because that combination is not a
        /// weaker signal, it is a far stronger one. An operator who has said "I
        /// am into a dummy load" has told us to expect almost nothing back. Most
        /// of it coming back means the load is not in the path they think it is
        /// — which is EXACTLY the fault of 2026-08-22, where the load sat on
        /// ANT2 while ANT1 was selected and two sessions of measurements were
        /// taken before anyone noticed it never got warm. That sentence is worth
        /// saying out loud rather than folding into the generic one.
        /// </para>
        /// </remarks>
        public static string ReflectedWarningText(
            float fraction, string antennaName, bool dummyLoadDeclared = false)
        {
            int percent = (int)Math.Round(fraction * 100f);
            bool named = !string.IsNullOrWhiteSpace(antennaName);

            string key = dummyLoadDeclared
                ? (named ? "audio.ptt.power_coming_back_on_dummy_load"
                         : "audio.ptt.power_coming_back_dummy_load")
                : (named ? "audio.ptt.power_coming_back_on"
                         : "audio.ptt.power_coming_back");

            return named
                ? Lexicon.Get(key, ("percent", percent), ("antenna", antennaName))
                : Lexicon.Get(key, ("percent", percent));
        }

        /// <summary>
        /// Forward watts above which the reflected-power CUT may act (#224).
        /// Below it there is little to protect and cutting costs the operator
        /// a contact for nothing — the bench dead key measured 0.22 W into an
        /// open port, harmless. Above it the radio is folding back to survive
        /// something: the case that started this had 13.4 of 17.5 W coming
        /// straight back. The boundary between "worth telling you" and
        /// "worth stopping for", ruled at ten by Noel 2026-08-25.
        /// </summary>
        public const float ReflectedCutMinForwardWatts = 10f;

        /// <summary>
        /// Whether the transmission should be CUT, not merely warned about
        /// (#224). Only ever true when the operator turned the setting on: an
        /// app that unilaterally unkeys a transmitter has taken the station
        /// away mid-transmission, and some operators — a reactive load, a
        /// tuner mid-cycle, an experimental antenna — would find that
        /// intolerable.
        /// </summary>
        /// <param name="settingEnabled">The operator's own choice. Never
        /// defaulted to true by a caller.</param>
        /// <param name="alreadyWarned">
        /// True once <see cref="ShouldWarnReflected"/> has fired this
        /// transmission. The cut requires it, which is the two-samples rule
        /// arriving by reuse rather than by a second counter: the warning
        /// fired on an EARLIER sample, this decision reads the current one,
        /// so a single transient at key-down can never cut — the same
        /// reasoning as the antenna checker's early stop.
        /// </param>
        /// <param name="forwardWatts">Forward power in WATTS.</param>
        /// <param name="reflectedWatts">Reflected power in WATTS.</param>
        /// <param name="tuning">True while the antenna tuner runs a cycle —
        /// high reflected power during one is the tuner working, and a cut
        /// here would kill every tune-up the operator starts.</param>
        public static bool ShouldCutReflected(bool settingEnabled, bool alreadyWarned,
                                              float forwardWatts, float reflectedWatts,
                                              bool tuning)
        {
            if (!settingEnabled || !alreadyWarned || tuning) return false;
            if (float.IsNaN(forwardWatts) || forwardWatts <= ReflectedCutMinForwardWatts)
                return false;

            float back = ReflectedFractionOf(forwardWatts, reflectedWatts);
            return !float.IsNaN(back) && back >= ReflectedWarnFraction;
        }

        /// <summary>
        /// What is said when the cut fires. It must say what happened, why,
        /// and above all that the operator is NO LONGER TRANSMITTING — they
        /// have no visual cue that it happened and will keep talking.
        /// </summary>
        public static string ReflectedCutText(float fraction, string antennaName)
        {
            int percent = (int)Math.Round(fraction * 100f);
            bool named = !string.IsNullOrWhiteSpace(antennaName);
            string key = named ? "audio.ptt.reflected_cut_on" : "audio.ptt.reflected_cut";
            return named
                ? Lexicon.Get(key, ("percent", percent), ("antenna", antennaName))
                : Lexicon.Get(key, ("percent", percent));
        }
    }
}
