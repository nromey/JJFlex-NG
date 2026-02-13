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
    }
}
