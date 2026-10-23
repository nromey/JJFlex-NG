#nullable enable
namespace Radios
{
    /// <summary>
    /// What a reflected-power cut knew when it cut: the rung, the reading it
    /// judged, and the TX antenna it named (#571). Carried from
    /// <see cref="TransmitKillSwitch"/>'s watcher to the PTT safety
    /// controller so a cut from a transmit check starts the same hold the
    /// controller's own cut does, with the same words.
    /// </summary>
    public readonly struct ReflectedCutNotice
    {
        public TransmitSafety.ReflectedCut Rung { get; }
        public TransmitPowerReading Reading { get; }
        public string Antenna { get; }

        /// <summary>True when the notice carries a judged reading; false for <see cref="WithoutReading"/>.</summary>
        public bool HasReading { get; }

        public ReflectedCutNotice(TransmitSafety.ReflectedCut rung, in TransmitPowerReading reading, string? antenna)
        {
            Rung = rung;
            Reading = reading;
            Antenna = antenna ?? "";
            HasReading = true;
        }

        private ReflectedCutNotice(string antenna)
        {
            Rung = TransmitSafety.ReflectedCut.None;
            Reading = TransmitPowerReading.None;
            Antenna = antenna;
            HasReading = false;
        }

        /// <summary>
        /// A cut requested for the reflected-power source by a caller that
        /// had no reading in hand. The hold still starts; its reason says
        /// "too much of your power" rather than inventing a number.
        /// </summary>
        public static ReflectedCutNotice WithoutReading(string? antenna) => new ReflectedCutNotice(antenna ?? "");

        public override string ToString()
            => HasReading
                ? Rung + " rung, " + Reading + ", antenna " + (Antenna.Length == 0 ? "unknown" : Antenna)
                : "no reading, antenna " + (Antenna.Length == 0 ? "unknown" : Antenna);
    }
}
