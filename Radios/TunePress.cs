#nullable enable

namespace Radios
{
    /// <summary>What a press of the tune key (Ctrl+T, or ATU Tune on the menu) does, by tuner state.</summary>
    public enum TunePressOutcome
    {
        /// <summary>The radio's ATU runs a tune cycle (automatic type). Unchanged by #1127.</summary>
        AtuTune,

        /// <summary>
        /// A plain tune carrier (manual type), and the press says so. Manual is
        /// the type after a failed ATU tune and after a bypass (Jim's design,
        /// kept: "bypass the tuner", "Turn autotune off"), and the type a radio
        /// with no ATU has from the start.
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
    /// an ATU, sends the plain carrier through the same latch as before
    /// (FlexBase.FlexTunerOn's manual branch) and says it is a tune carrier.
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
