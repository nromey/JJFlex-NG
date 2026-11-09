using System;

namespace Radios
{
    /// <summary>
    /// The noise floor every computer noise engine shows (#1019, ruled by Noel
    /// 2026-11-08 17:54): how much of the original audio is mixed back in, 0 to
    /// 100. Underneath, nothing changed: what is stored is still the wet fraction
    /// ("Strength", 0 to 1) that <see cref="DenoiseEngineSettings.Strength"/>,
    /// the receive slots, the presets and <c>TxConditioningSettings.NrStrength</c>
    /// have always held, so a file written by this build reads correctly in an
    /// older one and the reverse. Floor = 100 minus Strength times 100.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ONLY place that subtracts.</b> Every surface reads and writes the
    /// floor through here (the noise controller's lever, the Audio Workshop's
    /// transmit field); a structural test refuses any other caller of the slot's
    /// Strength outside an allow-list.
    /// </para>
    /// <para>
    /// <b>Bit-exact round trip.</b> A shown floor is a whole number; the stored
    /// value is built from it the way the Strength lever always built it, as the
    /// float nearest (100 - floor) / 100, so writing floor 20 stores exactly the
    /// bits of 0.8f. Single-precision 1 - 0.8f is not exactly 0.2, which is why
    /// the shown number is rounded rather than truncated.
    /// </para>
    /// <para>
    /// <b>One source for every default</b> (<see cref="DefaultStrength"/>), by
    /// engine and direction, here in Radios because the transmit profile
    /// (<c>TxConditioningSettings</c>) lives in Radios. It knows engine ids as
    /// strings only, as <see cref="DenoiseEngineSettings.EngineId"/> already does;
    /// no catalog comes down a layer.
    /// </para>
    /// </remarks>
    public static class NoiseFloor
    {
        /// <summary>The floor's least value: fully processed.</summary>
        public const int Min = 0;

        /// <summary>The floor's greatest value: the original audio only.</summary>
        public const int Max = 100;

        /// <summary>One step of the floor on every surface.</summary>
        public const int Step = 5;

        /// <summary>The shown floor, 0 to 100, for a stored wet fraction. A non-finite value reads as fully processed.</summary>
        public static int FromStrength(float strength)
        {
            if (!float.IsFinite(strength)) return Min;
            double clamped = Math.Clamp((double)strength, 0, 1);
            return (int)Math.Round((1 - clamped) * 100, MidpointRounding.AwayFromZero);
        }

        /// <summary>The stored wet fraction for a shown floor, clamped to 0 to 100 and rounded to a whole floor.</summary>
        public static float ToStrength(double floor)
        {
            if (!double.IsFinite(floor)) floor = Min;
            double whole = Math.Round(Math.Clamp(floor, Min, Max), MidpointRounding.AwayFromZero);
            return (float)((Max - whole) / 100.0);
        }

        /// <summary>The receive RNNoise floor default (ruled S3, 2026-11-09 15:41): 10.</summary>
        public const int ReceiveRnnoiseDefault = 10;

        /// <summary>The transmit RNNoise floor default (ruled S3): 20, unchanged until Noel has listened on transmit.</summary>
        public const int TransmitRnnoiseDefault = 20;

        /// <summary>Every other computer engine's floor default (ruled S3): 0, fully processed, as Strength 1 always was.</summary>
        public const int OtherEnginesDefault = 0;

        /// <summary>The default floor of an engine in a direction (ruled S3). NR2 0; DeepFilterNet3, NR4, NNR and Captured noise 0.</summary>
        public static int DefaultFloor(string engineId, DenoiseDirection direction) =>
            engineId == "rnnoise"
                ? (direction == DenoiseDirection.Transmit ? TransmitRnnoiseDefault : ReceiveRnnoiseDefault)
                : OtherEnginesDefault;

        /// <summary>The default stored wet fraction of an engine in a direction: <see cref="DefaultFloor"/> through the one conversion.</summary>
        public static float DefaultStrength(string engineId, DenoiseDirection direction) =>
            ToStrength(DefaultFloor(engineId, direction));
    }
}
