using HamBands;

namespace Radios
{
    /// <summary>
    /// Builds plain-English radio status for speech and status display.
    /// </summary>
    public static class RadioStatusBuilder
    {
        /// <summary>
        /// Build a comprehensive multi-slice status for Ctrl+Shift+S.
        /// Returns something like:
        ///   "2 slices. Slice A selected, transmit, 14.250 megahertz, USB, pan center.
        ///    Slice B, 7.150 megahertz, LSB, muted, pan right."
        /// Falls back to single-slice BuildSpokenStatus if only one slice.
        /// </summary>
        public static string BuildFullSliceStatus(FlexBase radio)
        {
            if (radio == null)
                return "No radio connected";

            int numSlices = radio.MyNumSlices;
            if (numSlices == 0)
                return BuildSpokenStatus(radio);

            if (numSlices == 1)
                return BuildSpokenStatus(radio);

            var parts = new System.Collections.Generic.List<string>();
            parts.Add($"{numSlices} slices");

            int rxVfo = radio.RXVFO;
            int txVfo = radio.TXVFO;

            for (int i = 0; i < numSlices; i++)
            {
                var slice = radio.VFOToSlice(i);
                if (slice == null) continue;

                string letter = slice.Letter ?? i.ToString();
                double freqMhz = slice.Freq;
                string mode = slice.DemodMode ?? "";
                bool isMuted = slice.Mute;
                int pan = slice.AudioPan;
                bool isActive = (i == rxVfo);
                bool isTx = (i == txVfo);

                var sb = new System.Text.StringBuilder();
                sb.Append($"Slice {letter}");

                // MultiFlex ownership
                string owner = radio.GetSliceOwnerName(slice.ClientHandle);
                if (owner != null)
                    sb.Append($", {owner}");

                if (isActive) sb.Append(", selected");
                if (isTx) sb.Append(", transmit");

                sb.Append($", {freqMhz:F3} megahertz, {mode}");

                if (isMuted) sb.Append(", muted");

                // Pan in words, from the one shared scale (PanPhrase) — this
                // held its own hardcoded copy of the bands until 2026-08-27,
                // and the pan sub-layer's arrival made that two vocabularies
                // for one value.
                sb.Append($", pan {PanPhrase.Words(pan)}");

                parts.Add(sb.ToString());
            }

            return string.Join(". ", parts);
        }

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
            {
                // Assert an ABSENCE only when the slice census has actually
                // arrived. During a connect, slices take a second or two to
                // populate, and this used to return "no active slice" in that
                // window — a sentence that was false within two seconds, spoken
                // to the operator by the post-dialog status path (#348).
                // MyNumSlices > 0 is the same test SpeakConnectStatus applies
                // before trusting slice facts; until it passes, say only what
                // is known: the connection itself.
                return radio.MyNumSlices > 0
                    ? $"Connected to {snap.RadioModel}, no active slice"
                    : $"Connected to {snap.RadioModel}";
            }

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
                // Real watts, decimals and all. radio.SMeter truncates on
                // transmit, so sub-watt drive read as "0 watts" — the same
                // thing it says when the radio is not transmitting at all.
                snap.SignalDisplay = FlexBase.FormatForwardPowerSpoken(radio.ForwardPowerWatts);
            }
            else
            {
                // One rule, one place. This was inline here and inline again
                // in the spoken S-meter command, and the two drifted — one
                // multiplied the excess by six, the other by ten. See
                // SMeterReading for why multiplying is always wrong.
                snap.SignalDisplay = SMeterReading.Display(radio.SMeter);
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
