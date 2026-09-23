using System;

namespace Radios.StationConnect
{
    /// <summary>
    /// The offer to load the owner's profile after the other operator left
    /// (case 3 of the 2026-09-22 ruling), bound to the connection attempt it
    /// was made on. The dialog carries this token to its Yes, and the load
    /// refuses it if the connection has since been replaced: a reconnect on
    /// the same rig while the prompt is open must not apply the old consent
    /// to the new connection (Track G2 re-review, section 5).
    /// </summary>
    public sealed class OwnerLoadOffer
    {
        public OwnerLoadOffer(int attemptGeneration, string serial)
        {
            AttemptGeneration = attemptGeneration;
            Serial = serial ?? "";
        }

        /// <summary>The connection attempt the offer was made on.</summary>
        public int AttemptGeneration { get; }

        /// <summary>The radio the offer was made for.</summary>
        public string Serial { get; }

        public override string ToString() => "offer on attempt " + AttemptGeneration + " for " + Serial;
    }
}
