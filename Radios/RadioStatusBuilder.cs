using HamBands;

namespace Radios
{
    /// <summary>
    /// Builds plain-English radio status for speech and status display.
    /// </summary>
    public static class RadioStatusBuilder
    {
        /// <summary>
        /// Build a concise spoken status message.
        /// </summary>
        public static string BuildSpokenStatus(FlexBase radio)
        {
            if (radio == null)
                return "No radio connected";

            var snap = BuildDetailedStatus(radio);
            if (!snap.IsConnected)
                return "No radio connected";

            if (!snap.HasActiveSlice)
                return $"Connected to {snap.RadioModel}, no active slice";

            string bandPart = string.IsNullOrEmpty(snap.BandSpoken) ? "" : $", {snap.BandSpoken} band";
            string slicePart = string.IsNullOrEmpty(snap.SliceLetter) ? "" : $", slice {snap.SliceLetter}";

            if (snap.IsTransmitting)
            {
                return $"Transmitting on {snap.FrequencySpoken}, {snap.Mode}{bandPart}{slicePart}, {snap.SignalDisplay}";
            }
            else
            {
                return $"Listening on {snap.FrequencySpoken}, {snap.Mode}{bandPart}{slicePart}";
            }
        }

        /// <summary>
        /// Build a full status snapshot for the status dialog.
        /// </summary>
        public static RadioStatusSnapshot BuildDetailedStatus(FlexBase radio)
        {
            var snap = new RadioStatusSnapshot();
            if (radio == null)
                return snap;

            snap.IsConnected = true;
            snap.RadioModel = radio.RadioModel;
            snap.RadioNickname = radio.RadioNickname;
            snap.IsRemote = radio.RemoteRig;
            snap.IsTransmitting = radio.Transmit;
            snap.HasActiveSlice = radio.HasActiveSlice;

            if (!snap.HasActiveSlice)
                return snap;

            ulong freq = radio.Frequency;
            snap.FrequencyDisplay = FormatFreqDisplay(freq);
            snap.FrequencySpoken = FrequencyToSpoken(freq);
            snap.Mode = radio.Mode;

            snap.SliceLetter = snap.IsTransmitting
                ? radio.TXSliceLetter
                : radio.ActiveSliceLetter;

            // Band lookup
            var band = Bands.Query(freq);
            if (band != null)
            {
                snap.BandName = band.Name;
                snap.BandSpoken = BandToSpoken(band.Name);
            }

            // Signal: S-units when receiving, watts when transmitting
            if (snap.IsTransmitting)
            {
                snap.SignalDisplay = $"{radio.SMeter} watts";
            }
            else
            {
                int s = radio.SMeter;
                if (s <= 9)
                    snap.SignalDisplay = $"S{s}";
                else
                    snap.SignalDisplay = $"S9 plus {(s - 9) * 6}";
            }

            return snap;
        }

        /// <summary>
        /// Format frequency in Hz to display string like "14.250.000".
        /// Mirrors globals.vb FormatFreqUlong pattern.
        /// </summary>
        internal static string FormatFreqDisplay(ulong freqHz)
        {
            string str = freqHz.ToString();
            // Pad to at least 7 characters
            while (str.Length < 7)
                str = "0" + str;
            int len = str.Length;
            return str.Substring(0, len - 6) + "." + str.Substring(len - 6, 3) + "." + str.Substring(len - 3);
        }

        /// <summary>
        /// Convert frequency in Hz to spoken form like "14.250 megahertz".
        /// </summary>
        internal static string FrequencyToSpoken(ulong freqHz)
        {
            double mhz = freqHz / 1_000_000.0;
            // Format to 3 decimal places (kHz resolution)
            return $"{mhz:F3} megahertz";
        }

        /// <summary>
        /// Convert band name like "20m" to spoken form like "20 meter".
        /// </summary>
        internal static string BandToSpoken(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";

            // "20m" → "20 meter", "70cm" → "70 centimeter", "6mm" → "6 millimeter"
            if (name.EndsWith("cm"))
                return name.Substring(0, name.Length - 2) + " centimeter";
            if (name.EndsWith("mm"))
                return name.Substring(0, name.Length - 2) + " millimeter";
            if (name.EndsWith("m"))
                return name.Substring(0, name.Length - 1) + " meter";

            return name;
        }
    }
}
