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
            return SpokenText(snap);
        }

        /// <summary>The concise spoken status, from one reading and nothing else.</summary>
        internal static string SpokenText(RadioStatusSnapshot snap)
        {
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
                // is known: the connection itself. Read into the snapshot with
                // everything else (Sprint 49 Track N2 repair 1).
                return snap.SliceCount > 0
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
            snap.SliceCount = radio.MyNumSlices;

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
        /// A spoken status and its own answer to "is this still where the
        /// radio is", for the speech arbiter to ask before replaying an unheard
        /// copy (Sprint 49 Track N2). The text and the answer come from ONE
        /// reading of the radio (repair 1, Codex's F1): building the text and
        /// then reading the radio again for its answer let a status report
        /// arriving between the two certify a sentence it never described.
        /// </summary>
        /// <param name="ask">What a memory recall asked the radio for (#1131), or null: the text names it, and the answer allows the radio's way there.</param>
        public static SpokenStatus CaptureSpokenStatus(FlexBase radio, StatusAsk? ask) =>
            CaptureSpokenStatus(() => BuildDetailedStatus(radio), ask);

        /// <summary>As above, reading the radio through <paramref name="read"/>: called once now, and once at every later question.</summary>
        internal static SpokenStatus CaptureSpokenStatus(System.Func<RadioStatusSnapshot> read, StatusAsk? ask)
        {
            var reported = read();
            var claim = RecallClaim(reported, ask) ?? new StatusClaim(reported.Copy(), reported, null);
            return new SpokenStatus(SpokenText(claim.Said), () => claim.Holds(read()));
        }

        /// <summary>
        /// The claim of a sentence naming what a recall asked for, or null when
        /// the ask must not be used and the sentence says what the radio reports
        /// (Sprint 49 Track N2 repair 2, Codex's R1). The ask is used only while
        /// it is still pending, asked HERE before the first sentence is built and
        /// not only before a replay, and only while the radio is on the recall's
        /// own way: everything a recall does not change as it was at the
        /// operator's action, and each of frequency and mode either that action's
        /// or the asked one. A tune or slice change made between the action and
        /// this moment fails that, so it never becomes the old recall's "before".
        /// </summary>
        private static StatusClaim? RecallClaim(RadioStatusSnapshot reported, StatusAsk? ask)
        {
            if (ask == null) return null;
            var said = reported.Copy();
            ApplyAskedFor(said, ask.FrequencyHz, ask.Mode);
            // A sentence naming nothing the radio has not already reported has no recall to wait for.
            if (StatedPart(said) == StatedPart(reported)) return new StatusClaim(said, reported, null);
            if (!ask.StillPending()) return null;
            if (ask.Before == null || StatusClaim.RestOf(reported) != StatusClaim.RestOf(ask.Before)) return null;
            var claim = new StatusClaim(said, ask.Before, ask.StillPending);
            return claim.Holds(reported) ? claim : null;
        }

        /// <summary>
        /// What one spoken status claims, and the rule for whether the radio
        /// still bears it out. It holds while the radio reports what it says;
        /// the signal reading is not part of the claim.
        ///
        /// A memory recall's sentence names the asked-for frequency and mode
        /// before the radio has answered (#1131), and the radio answers them as
        /// separate status reports, in either order. So while that recall is
        /// still pending (inside its lifetime, and no later mode choice has
        /// superseded it), each of those two parts may be either what was asked
        /// or what the radio reported when the sentence was built; everything
        /// else (receiving or transmitting, the slice, the connection) must be
        /// as said. A part seen at its asked value never goes back to its old
        /// one, and once the whole sentence has been seen true, or the recall
        /// is no longer pending, only the sentence itself holds (repair 1,
        /// Codex's F2).
        /// </summary>
        internal sealed class StatusClaim
        {
            private readonly object _gate = new();
            private readonly string _said;
            private readonly RadioStatusSnapshot _saidSnap, _before;
            private System.Func<bool>? _pending;
            private bool _frequencyArrived, _modeArrived;

            /// <param name="said">What the sentence says.</param>
            /// <param name="before">Where the radio's way to it starts: for a recall, what the radio reported at the operator's action (repair 2, Codex's R1); otherwise the reading the sentence was built from.</param>
            /// <param name="askPending">The recall's pending answer, or null when only the sentence itself holds.</param>
            internal StatusClaim(RadioStatusSnapshot said, RadioStatusSnapshot before, System.Func<bool>? askPending)
            {
                _saidSnap = said;
                _before = before;
                _said = StatedPart(said);
                // A sentence naming nothing it did not start from has no recall to wait for.
                _pending = _said == StatedPart(before) ? null : askPending;
            }

            /// <summary>What the sentence says.</summary>
            internal RadioStatusSnapshot Said => _saidSnap;

            internal bool Holds(RadioStatusSnapshot now)
            {
                lock (_gate)
                {
                    if (StatedPart(now) == _said) { _pending = null; return true; }
                    if (_pending == null) return false;
                    if (!_pending()) { _pending = null; return false; }
                    if (RestOf(now) != RestOf(_saidSnap)) return false;
                    bool frequencyAsked = FrequencyOf(now) == FrequencyOf(_saidSnap);
                    bool modeAsked = now.Mode == _saidSnap.Mode;
                    _frequencyArrived |= frequencyAsked;
                    _modeArrived |= modeAsked;
                    bool frequencyOk = frequencyAsked || (!_frequencyArrived && FrequencyOf(now) == FrequencyOf(_before));
                    bool modeOk = modeAsked || (!_modeArrived && now.Mode == _before.Mode);
                    return frequencyOk && modeOk;
                }
            }

            /// <summary>The frequency and the band it puts the slice in: a recall changes them together.</summary>
            private static string FrequencyOf(RadioStatusSnapshot s) => s.FrequencySpoken + "|" + s.BandSpoken;

            /// <summary>Everything a recall does not change.</summary>
            internal static string RestOf(RadioStatusSnapshot s) =>
                string.Join("|", s.IsConnected, s.HasActiveSlice, s.IsTransmitting, s.SliceLetter, s.RadioModel);
        }

        /// <summary>
        /// What a spoken status states, as one comparable value: receiving or
        /// transmitting, the spoken frequency, the mode, the band and the slice;
        /// with no active slice, the sentence itself, because whether the slice
        /// census has arrived decides whether it asserts the absence at all, and
        /// comparing the words keeps the claim from drifting from that rule
        /// (repair 2, Codex's R2). Never the signal reading, which moves while
        /// the claim stays true.
        /// </summary>
        internal static string StatedPart(RadioStatusSnapshot snap)
        {
            if (!snap.IsConnected) return "not connected";
            if (!snap.HasActiveSlice) return "no active slice|" + SpokenText(snap);
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
