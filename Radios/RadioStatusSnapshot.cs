namespace Radios
{
    /// <summary>
    /// Plain-English snapshot of radio state for speech and status display.
    /// Simple POCO with no radio dependency.
    /// </summary>
    public class RadioStatusSnapshot
    {
        public bool IsConnected { get; set; }
        public bool IsTransmitting { get; set; }
        public bool IsRemote { get; set; }
        public bool HasActiveSlice { get; set; }

        public string RadioModel { get; set; } = "";
        public string RadioNickname { get; set; } = "";

        /// <summary>Display frequency like "14.250.000"</summary>
        public string FrequencyDisplay { get; set; } = "";
        /// <summary>Spoken frequency like "14.250 megahertz"</summary>
        public string FrequencySpoken { get; set; } = "";

        public string Mode { get; set; } = "";
        /// <summary>Band name like "20m"</summary>
        public string BandName { get; set; } = "";
        /// <summary>Spoken band like "20 meter"</summary>
        public string BandSpoken { get; set; } = "";

        public string SliceLetter { get; set; } = "";

        /// <summary>Signal display: "S7" when receiving, "100W" when transmitting</summary>
        public string SignalDisplay { get; set; } = "";

        /// <summary>
        /// This client's slices when the snapshot was read: whether the slice
        /// census has arrived, which decides between "no active slice" and
        /// saying only the connection (#348). Read with the rest, so a spoken
        /// status built from the snapshot needs no second look at the radio.
        /// </summary>
        public int SliceCount { get; set; }

        /// <summary>A copy, for a caller that changes one reading and keeps the other (Sprint 49 Track N2).</summary>
        internal RadioStatusSnapshot Copy() => (RadioStatusSnapshot)MemberwiseClone();
    }
}
