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
        ///   "2 slices. Slice A selected, transmit, 14.250 megahertz, USB, pan 50.
        ///    Slice B, 7.150 megahertz, LSB, muted, pan 85."
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

                // Pan as the number, same as the pan sub-layer says. #536,
                // ruled 2026-09-06: the words scale is gone entirely. It used
                // to live here as a hardcoded copy of the bands, then as a
                // shared call on one scale from 2026-08-27 — one vocabulary
                // instead of two. Now it is no vocabulary instead of one,
                // which keeps this line and the control in agreement for the
                // same reason the shared scale did.
                sb.Append($", pan {pan}");

                parts.Add(sb.ToString());
            }

            return string.Join(". ", parts);
        }

        /// <summary>
        /// Build a concise spoken status message.
        /// </summary>
        public static string BuildSpokenStatus(FlexBase radio) => BuildSpokenStatus(radio, null, null);

        /// <summary>
        /// The concise spoken status, with the frequency and mode the app has
        /// just ASKED the radio for in place of what it reports (#1131). A
        /// memory recall sends one request and the radio answers in its own
        /// time: the landing after the Memories window closed was built 17 ms
        /// before the radio's mode status, so it said "Listening on 14.100
        /// megahertz, USB" for a memory that put the slice in CW, and no CW word
        /// ever followed. Null leaves that part as the radio reports it. Never
        /// applied while transmitting (a recall changes the receive slice).
        /// </summary>
        public static string BuildSpokenStatus(FlexBase radio, ulong? askedFrequencyHz, string? askedMode)
        {
            if (radio == null)
                return "No radio connected";

            var snap = BuildDetailedStatus(radio);
            ApplyAskedFor(snap, askedFrequencyHz, askedMode);
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
        /// The producer's answer for a spoken status, for the speech arbiter to
        /// ask before replaying an unheard copy (Sprint 49 Track N2): true while
        /// the radio still reports what the sentence says, or still reports what
        /// it reported when the sentence was built (a memory recall the radio has
        /// not answered yet, whose sentence names the asked-for frequency).
        /// False once the radio has moved anywhere else, so "Listening on 10.125
        /// megahertz, 30 meter band" is not replayed after a band change to 20
        /// meters. The signal reading is not part of the claim.
        /// </summary>
        public static System.Func<bool> StillDescribes(FlexBase radio, ulong? askedFrequencyHz, string? askedMode)
        {
            var built = BuildDetailedStatus(radio);
            string reported = StatedPart(built);
            ApplyAskedFor(built, askedFrequencyHz, askedMode);
            string said = StatedPart(built);
            return () => StillDescribes(said, reported, StatedPart(BuildDetailedStatus(radio)));
        }

        /// <summary>The rule <see cref="StillDescribes(FlexBase, ulong?, string?)"/> applies, on the stated parts.</summary>
        internal static bool StillDescribes(string said, string reportedWhenSaid, string now) =>
            now == said || now == reportedWhenSaid;

        /// <summary>
        /// What a spoken status states, as one comparable value: receiving or
        /// transmitting, the spoken frequency, the mode, the band and the slice;
        /// with no active slice, only that and the model. Never the signal
        /// reading, which moves while the claim stays true.
        /// </summary>
        internal static string StatedPart(RadioStatusSnapshot snap)
        {
            if (!snap.IsConnected) return "not connected";
            if (!snap.HasActiveSlice) return "no active slice|" + snap.RadioModel;
            return string.Join("|", snap.IsTransmitting ? "transmitting" : "listening",
                snap.FrequencySpoken, snap.Mode, snap.BandSpoken, snap.SliceLetter);
        }

        /// <summary>
        /// Put what the app asked for (a memory's frequency and mode, #1131) in
        /// place of what the radio reported before it answered: the frequency,
        /// its band, and the mode. Nothing without an active slice, or while
        /// transmitting.
        /// </summary>
        internal static void ApplyAskedFor(RadioStatusSnapshot snap, ulong? frequencyHz, string? mode)
        {
            if (!snap.HasActiveSlice || snap.IsTransmitting) return;
            if (frequencyHz is ulong freq && freq > 0)
            {
                snap.FrequencyDisplay = FormatFreqDisplay(freq);
                snap.FrequencySpoken = FrequencyToSpoken(freq);
                var band = Bands.Query(freq);
                snap.BandName = band?.Name ?? "";
                snap.BandSpoken = band == null ? "" : BandToSpoken(band.Name);
            }
            if (!string.IsNullOrEmpty(mode)) snap.Mode = mode;
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
