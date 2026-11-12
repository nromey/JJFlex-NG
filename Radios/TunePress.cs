#nullable enable

namespace Radios
{
    /// <summary>What a press of the tune key (Ctrl+T, or ATU Tune on the menu) does, by tuner state.</summary>
    public enum TunePressOutcome
    {
        /// <summary>The radio's ATU runs a tune cycle (automatic type). Unchanged by #1127.</summary>
        AtuTune,

        /// <summary>
        /// The tune carrier's own key (manual type), and the press says so.
        /// Manual is the type after a failed ATU tune and after a bypass (Jim's
        /// design, kept: "bypass the tuner", "Turn autotune off"), and the type
        /// a radio with no ATU has from the start. A press with our carrier up
        /// turns it off; a new press starts one through the tune carrier toggle
        /// (MainWindow.TuneKeyCarrierPress, batch 12 repair round 1).
        /// </summary>
        TuneCarrier,

        /// <summary>No ATU, and a type that would ask for one: refused, as before.</summary>
        NoTuner,

        /// <summary>
        /// An ATU, with the tuner type the operator set to none. Not ruled
        /// (#1127's ruling names a failed or bypassed ATU and a radio with
        /// none); left exactly as it was, which sends nothing.
        /// </summary>
        TunerOff,
    }

    /// <summary>
    /// The one place that decides what the tune key does (#1127, ruled by Noel
    /// 2026-11-12 08:05, J4).
    /// </summary>
    /// <remarks>
    /// Until then the key said "ATU tuning" whatever happened next, and in
    /// manual type, which every radio that connects with its ATU bypassed runs
    /// in for the whole session, the press keyed a plain tune carrier. A radio
    /// with no ATU refused the press outright. Now manual type, with or without
    /// an ATU, makes the key the tune carrier's own: the carrier goes through
    /// the tune carrier toggle (not the tuner latch, which a second press used
    /// to find set while the carrier stayed on the air), the press says it is
    /// a tune carrier, and a press with the carrier up turns it off.
    /// Automatic type is unchanged.
    /// </remarks>
    public static class TunePress
    {
        public static TunePressOutcome For(bool radioHasAtu, FlexBase.FlexTunerTypes type)
        {
            if (type == FlexBase.FlexTunerTypes.manual) return TunePressOutcome.TuneCarrier;
            if (!radioHasAtu) return TunePressOutcome.NoTuner;
            return type == FlexBase.FlexTunerTypes.auto ? TunePressOutcome.AtuTune : TunePressOutcome.TunerOff;
        }
    }
}
