using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// The 60 meter band as the US rule stands since 2026-02-13 (FCC 25-60,
    /// ET Docket 23-120, 91 FR 1405): four channels and one segment, and the
    /// three decisions every way onto 60 m shares: where a stop tunes, which
    /// modes are corrected, and what counts as a valid dial.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule, 47 CFR 97.303(h)(3), 97.305(c), 97.307(f)(14) and
    /// 97.313(i).</b> Four channels, centred on 5332, 5348, 5373 and 5405 kHz
    /// (the fifth, 5358.5, was deleted: it now lies inside the segment), and
    /// the segment 5351.5 to 5366.5 kHz. Phone, RTTY, data and CW on both,
    /// never wider than 2.8 kHz. On a channel the CW carrier IS the centre; a
    /// phone, data or RTTY carrier sits 1.5 kHz below it, so the upper
    /// sideband fills the 2.8 kHz channel. Power is radiated power (ERP):
    /// 100 W on the channels, 9.15 W in the segment.</para>
    /// <para><b>Choices within the rule (orchestrator's, within Noel's ruling
    /// of 2026-11-19, not the rule itself).</b> On a channel: USB, DIGU and
    /// CW are kept; LSB becomes USB and DIGL becomes DIGU, because the only
    /// placement the rule describes for phone and data is the upper sideband
    /// one; the radio's RTTY mode becomes DIGU, because the RTTY the rule
    /// names on a channel is the PSK31-class 60H0J2B designator, sent as
    /// upper sideband data; everything else becomes USB. In the segment: USB,
    /// LSB, CW, DIGU, DIGL and RTTY are kept; AM, SAM, DSB, the FM modes and
    /// FreeDV become USB.</para>
    /// <para><b>Not here:</b> the 2.8 kHz bandwidth check on the transmit
    /// filter and any enforcement at transmit time. Nothing in this class
    /// keys, refuses or limits power.</para>
    /// </remarks>
    public static class SixtyMeterChannels
    {
        /// <summary>
        /// A channel: its centre, its radiated power limit, and its name.
        /// </summary>
        /// <param name="CentreMHz">The channel centre, which is the CW carrier.
        /// A phone or data dial is <see cref="CarrierBelowCentreHz"/> lower.</param>
        /// <param name="MaxErpW">Effective radiated power, watts: transmitter
        /// PEP times the antenna's gain over a dipole. A check that reads only
        /// the radio's output power cannot judge it.</param>
        /// <param name="Label">How the channel is named when spoken.</param>
        public record Channel(double CentreMHz, double MaxErpW, string Label)
        {
            /// <summary>The centre in hertz: the CW carrier.</summary>
            public ulong CentreHz => ToHz(CentreMHz);
        }

        /// <summary>
        /// The contiguous segment: its edges, the one dial the rotation stops
        /// on, and its radiated power limit (ERP, as <see cref="Channel.MaxErpW"/>).
        /// </summary>
        public record Segment(double StartMHz, double EndMHz, double StopMHz, double MaxErpW)
        {
            public ulong StartHz => ToHz(StartMHz);
            public ulong EndHz => ToHz(EndMHz);
            public ulong StopHz => ToHz(StopMHz);
        }

        /// <summary>
        /// A phone, data or RTTY carrier sits this far below a channel's
        /// centre (97.303(h)(3)): 5330.5, 5346.5, 5371.5 and 5403.5 kHz.
        /// </summary>
        public const ulong CarrierBelowCentreHz = 1_500;

        /// <summary>
        /// US channels (97.303(h)(3), US23). The labels are the ARRL's
        /// numbering, 1 to 4 (approved by Noel 2026-11-21).
        /// </summary>
        public static readonly Channel[] USChannels =
        {
            new(5.3320, 100, "Channel 1"),
            new(5.3480, 100, "Channel 2"),
            new(5.3730, 100, "Channel 3"),
            new(5.4050, 100, "Channel 4"),
        };

        /// <summary>
        /// The US segment, 5351.5 to 5366.5 kHz, 9.15 W ERP (97.313(i)). Its
        /// stop, 5357.0 kHz, keeps every allowed mode's nominal emission inside
        /// the edges (5354.0 to 5360.0 at its widest), so the stop does not move
        /// when the operator changes mode.
        /// </summary>
        public static readonly Segment USSegment = new(5.3515, 5.3665, 5.3570, 9.15);

        /// <summary>
        /// Country allocations keyed by country code.
        /// Future: add UK, Canada, etc. with their specific allocations.
        /// </summary>
        private static readonly Dictionary<string, (Channel[] Channels, Segment? Segment)> CountryAllocations = new()
        {
            ["US"] = (USChannels, USSegment),
        };

        /// <summary>Get the allocation for a country code, or null if not in table.</summary>
        public static (Channel[] Channels, Segment? Segment)? GetAllocation(string countryCode)
        {
            if (string.IsNullOrEmpty(countryCode)) return null;
            return CountryAllocations.TryGetValue(countryCode.ToUpperInvariant(), out var alloc)
                ? alloc
                : null;
        }

        /// <summary>Total navigation stops (channels + the segment if present).</summary>
        public static int GetNavigationStopCount(string countryCode)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return 0;
            return alloc.Value.Channels.Length + (alloc.Value.Segment != null ? 1 : 0);
        }

        /// <summary>How the segment is named when spoken (a correction, a boundary notice).</summary>
        public static string SegmentLabel => Lexicon.Get("settings.band.digital_segment");

        // ---------------- modes ----------------

        /// <summary>Upper case, trimmed; null and blank become "".</summary>
        private static string Norm(string? mode) => (mode ?? "").Trim().ToUpperInvariant();

        /// <summary>
        /// The mode a slice ends in on a channel: USB, DIGU and CW are kept,
        /// LSB becomes USB, DIGL and RTTY become DIGU, anything else USB.
        /// </summary>
        public static string ChannelFinalMode(string? mode)
        {
            string m = Norm(mode);
            return m switch
            {
                "USB" or "DIGU" or "CW" => m,
                "DIGL" or "RTTY" => "DIGU",
                _ => "USB",
            };
        }

        /// <summary>
        /// The mode a slice ends in in the segment: USB, LSB, CW, DIGU, DIGL
        /// and RTTY are kept; anything else (AM, SAM, DSB, the FM modes,
        /// FreeDV, an unknown mode) becomes USB.
        /// </summary>
        public static string SegmentFinalMode(string? mode)
        {
            string m = Norm(mode);
            return m is "USB" or "LSB" or "CW" or "DIGU" or "DIGL" or "RTTY" ? m : "USB";
        }

        /// <summary>
        /// The nominal emission of <paramref name="mode"/> relative to the
        /// dial, in hertz: USB and DIGU from the dial to 3 kHz above it, LSB
        /// and DIGL from 3 kHz below it to the dial, CW and RTTY 500 Hz
        /// centred on the dial. Null for a mode the segment does not keep.
        /// </summary>
        /// <remarks>
        /// <para>Nominal on purpose: this judges where a stop or a remembered
        /// dial may sit, with no slice to read. The transmitted signal itself
        /// (the slice's own filter, which an operator may widen) is the
        /// transmit-time check's to judge.</para>
        /// <para><b>Why 3 kHz and not the rule's 2.8.</b> 2.8 kHz is a WIDTH,
        /// not an offset. The app's default transmit filter
        /// (<see cref="AudioChainPreset.TxFilterLow"/> to
        /// <see cref="AudioChainPreset.TxFilterHigh"/>, 100 to 2,900 Hz) is
        /// 2.8 kHz wide but reaches 2.9 kHz from the dial, so a sideband judged
        /// as 0 to 2,800 Hz let a snap to a segment edge put the default signal
        /// 100 Hz outside the segment (found by both checkers of the first
        /// round). The sideband is the default filter plus 100 Hz of margin on
        /// the outer edge, so every snap and every validity judgement here keeps
        /// the default signal at least 100 Hz inside both edges. If the default
        /// filter ever widens, widen this with it.</para>
        /// </remarks>
        public static (long Low, long High)? NominalEmission(string? mode) => Norm(mode) switch
        {
            "USB" or "DIGU" => (0, 3_000),
            "LSB" or "DIGL" => (-3_000, 0),
            "CW" or "RTTY" => (-250, 250),
            _ => null,
        };

        /// <summary>
        /// The dial for <paramref name="finalMode"/> on <paramref name="ch"/>:
        /// the centre for CW, 1.5 kHz below it for everything else a channel
        /// keeps (USB and DIGU).
        /// </summary>
        public static ulong ChannelDialHz(Channel ch, string finalMode) =>
            Norm(finalMode) == "CW" ? ch.CentreHz : ch.CentreHz - CarrierBelowCentreHz;

        // ---------------- places ----------------

        /// <summary>
        /// A dial counts as on a channel within this of the centre or of the
        /// phone and data dial. Recognition only: VALIDITY is exact.
        /// </summary>
        private const ulong RecognitionHz = 100;

        private static ulong Distance(ulong a, ulong b) => a > b ? a - b : b - a;

        private static Channel? ChannelAt((Channel[] Channels, Segment? Segment) alloc, ulong hz)
        {
            foreach (var ch in alloc.Channels)
            {
                if (Distance(hz, ch.CentreHz) <= RecognitionHz
                    || Distance(hz, ch.CentreHz - CarrierBelowCentreHz) <= RecognitionHz)
                    return ch;
            }
            return null;
        }

        private static bool InSegment(Segment seg, ulong hz) => hz >= seg.StartHz && hz <= seg.EndHz;

        /// <summary>
        /// True when the dial <paramref name="frequencyHz"/> lies inside the
        /// country's segment. A place, for the boundary notice; whether a mode
        /// fits there is <see cref="IsValidSixtyMeterFrequency"/>'s question.
        /// </summary>
        public static bool IsInSegment(string countryCode, ulong frequencyHz)
        {
            var alloc = GetAllocation(countryCode);
            return alloc?.Segment is { } seg && InSegment(seg, frequencyHz);
        }

        // ---------------- the three decisions ----------------

        /// <summary>
        /// Determines if the current mode is appropriate for a 60m frequency.
        /// Returns (requiredMode, segmentLabel) if a mode change is needed,
        /// (null, segmentLabel) if mode is already correct,
        /// or null if the frequency is not on a recognized 60m allocation.
        /// </summary>
        /// <remarks>
        /// About the MODE only. A dial on a channel's centre in USB keeps its
        /// mode here and is judged by <see cref="IsValidSixtyMeterFrequency"/>:
        /// no mode change can repair a dial.
        /// </remarks>
        public static (string? RequiredMode, string SegmentLabel)? GetModeAdvisory(
            string countryCode, ulong frequencyHz, string currentMode)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return null;

            string current = Norm(currentMode);

            if (ChannelAt(alloc.Value, frequencyHz) is { } ch)
            {
                string final = ChannelFinalMode(current);
                return (final == current ? null : final, ch.Label);
            }

            if (alloc.Value.Segment is { } seg && InSegment(seg, frequencyHz))
            {
                string final = SegmentFinalMode(current);
                return (final == current ? null : final, SegmentLabel);
            }

            return null;
        }

        /// <summary>
        /// A mode the radio must be moved to on arrival at a 60m frequency, and
        /// the label of what requires it (the segment's name, "Channel 1").
        /// </summary>
        /// <remarks>
        /// Carries no sentence (#312, repair round two): what may be SAID
        /// depends on whether the request left, which only
        /// <see cref="SixtyMeterModeDelivery"/> knows.
        /// </remarks>
        public sealed record ModeCorrection(string RequiredMode, string SegmentLabel);

        /// <summary>
        /// <see cref="GetModeAdvisory"/> turned into what every arrival path acts
        /// on: null when nothing has to change (not a 60m allocation, or already
        /// on a permitted mode), otherwise the mode to ask for and why (#312).
        /// </summary>
        /// <remarks>
        /// One decision for every path that can land on 60m — the band jump,
        /// the channel rotation and typed frequency entry — and one way of
        /// saying it, <see cref="SixtyMeterModeDelivery"/>. The band jump had
        /// this inline and was, until #312, the only path that applied it at
        /// all.
        /// </remarks>
        public static ModeCorrection? GetModeCorrection(
            string countryCode, ulong frequencyHz, string? currentMode)
        {
            var advisory = GetModeAdvisory(countryCode, frequencyHz, currentMode ?? "");
            if (advisory?.RequiredMode is not { } required) return null;
            return new ModeCorrection(required, advisory.Value.SegmentLabel);
        }

        /// <summary>
        /// One stop of the Alt+Shift+Up / Alt+Shift+Down rotation: where to tune,
        /// <c>ChannelMode</c> (always null now: <see cref="Rotate"/> never fills
        /// it, and a channel stop that changes the mode says so through
        /// <c>Correction</c> like the segment does), the correction a stop owes
        /// (null when the mode is already permitted), and the arrival sentence
        /// WITHOUT any correction clause.
        /// </summary>
        /// <remarks>
        /// The correction clause is not here on purpose (#312, repair round one):
        /// whether "mode set to CW" is true depends on whether the radio took
        /// the write, which a pure planner cannot know.
        /// <see cref="SixtyMeterModeDelivery"/> appends the clause that is true.
        /// </remarks>
        public sealed record RotationStop(int Index, ulong FrequencyHz, string? ChannelMode,
            ModeCorrection? Correction, string Arrival);

        /// <summary>
        /// The next stop of the 60m rotation — the channels, then the
        /// segment — from <paramref name="currentIndex"/> in
        /// <paramref name="direction"/>, wrapping. Null when the country has no
        /// allocation or the allocation has no stops.
        /// </summary>
        /// <remarks>
        /// <para><b>The dial follows the FINAL mode</b>, the mode the slice ends
        /// in. A channel stop keeps USB, DIGU or CW and asks for
        /// <see cref="ChannelFinalMode"/> otherwise; CW tunes the carrier to the
        /// centre, USB and DIGU tune the dial 1.5 kHz below it. A kept mode's
        /// arrival names the dial and the mode. A mode that changes comes back
        /// as a <see cref="ModeCorrection"/>, never as <c>ChannelMode</c>, with an
        /// arrival that names only the channel and the dial, so the caller says
        /// it through <see cref="SixtyMeterModeDelivery"/> as the segment does:
        /// "switching to" the mode, never the mode as done (#1249).
        /// <c>ChannelMode</c> is therefore always null now; the caller's use of
        /// it stays until its own lines can change. Until the 2026 rule was applied every channel stop
        /// tuned the centre in USB, which put the signal 1.5 kHz high, half of it
        /// outside the channel.</para>
        /// <para><b>The segment stop</b> is <see cref="Segment.StopMHz"/> for every
        /// mode, and sets a mode only when the current one is not kept there,
        /// through <see cref="GetModeCorrection"/>; the caller says so through
        /// <see cref="SixtyMeterModeDelivery"/> (#312).</para>
        /// <para>Pure: the caller tunes, requests the mode and speaks. Pulled out of
        /// <c>MainWindow.SixtyMeterChannelNavigate</c> so the decision can be
        /// tested without a window or a radio.</para>
        /// </remarks>
        public static RotationStop? Rotate(
            string countryCode, int currentIndex, int direction, string? currentMode)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return null;

            int stopCount = alloc.Value.Channels.Length + (alloc.Value.Segment != null ? 1 : 0);
            if (stopCount == 0) return null;

            int index = ((currentIndex + direction) % stopCount + stopCount) % stopCount;

            if (index < alloc.Value.Channels.Length)
            {
                var ch = alloc.Value.Channels[index];
                string finalMode = ChannelFinalMode(currentMode);
                ulong dial = ChannelDialHz(ch, finalMode);
                string freq = $"{dial / 1_000_000.0:F4}";
                if (finalMode == Norm(currentMode))
                    return new RotationStop(index, dial, null, null,
                        Lexicon.Get("connect.home.sixty_meter_channel",
                            ("label", ch.Label), ("freq", freq), ("mode", ModeWords.Spoken(finalMode))));
                // The mode changes: asked for and said the way the segment's
                // is, through SixtyMeterModeDelivery ("switching to"), so the
                // arrival never names a mode the slice is not yet in (#1249).
                // The dial is still the one for the mode it ends in.
                return new RotationStop(index, dial, null, new ModeCorrection(finalMode, ch.Label),
                    Lexicon.Get("connect.home.sixty_meter_channel_arriving",
                        ("label", ch.Label), ("freq", freq)));
            }

            var seg = alloc.Value.Segment!;
            ulong freqHz = seg.StopHz;
            string arrival = Lexicon.Get("connect.home.sixty_meter_digital", ("freq", $"{seg.StopMHz:F4}"));
            return new RotationStop(index, freqHz, null,
                GetModeCorrection(countryCode, freqHz, currentMode), arrival);
        }

        private static ulong ToHz(double mhz) => (ulong)(mhz * 1_000_000.0 + 0.5);

        /// <summary>
        /// Whether <paramref name="frequencyHz"/> is a lawful dial for
        /// <paramref name="mode"/> as the mode stands: on a channel, exactly the
        /// centre for CW or exactly 1.5 kHz below it for USB and DIGU (no other
        /// mode is valid on a channel); in the segment, a kept mode whose
        /// nominal emission (<see cref="NominalEmission"/>) lies wholly inside
        /// the edges. True for a country with no allocation.
        /// </summary>
        /// <remarks>
        /// So a USB dial at a channel centre is NOT valid, nor a CW dial at a
        /// USB dial point, nor 5358.5 kHz as a channel (it is a segment
        /// frequency now). The bandwidth of the real transmit filter is not
        /// judged here.
        /// </remarks>
        public static bool IsValidSixtyMeterFrequency(string countryCode, ulong frequencyHz, string? mode)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return true; // No rules = no restrictions

            string m = Norm(mode);

            if (ChannelAt(alloc.Value, frequencyHz) is { } ch)
                return m is "USB" or "DIGU" or "CW" && frequencyHz == ChannelDialHz(ch, m);

            if (alloc.Value.Segment is { } seg && SegmentFinalMode(m) == m
                && NominalEmission(m) is { } e)
            {
                long low = (long)frequencyHz + e.Low;
                long high = (long)frequencyHz + e.High;
                return low >= (long)seg.StartHz && high <= (long)seg.EndHz;
            }

            return false;
        }

        /// <summary>
        /// The lawful dial nearest <paramref name="frequencyHz"/> for the mode
        /// the slice will end in there. Unchanged when it is already lawful.
        /// Used by band memory and the band jump; typed entry does not snap.
        /// </summary>
        /// <remarks>
        /// <para>The place is the channel the dial is on (its centre or its
        /// phone and data dial, within 100 Hz), else the segment when the dial is
        /// inside it, else whichever channel or the segment is nearest. On a
        /// channel the answer is <see cref="ChannelDialHz"/> for
        /// <see cref="ChannelFinalMode"/>; in the segment it is the nearest dial
        /// at which the final mode's nominal emission fits inside the edges.</para>
        /// <para>So every operator's remembered 60 m USB at 5332.0 kHz, written
        /// by the old channel stops, comes back as 5330.5, and a correct 5330.5
        /// stays where it is. A country with no allocation is left alone.</para>
        /// </remarks>
        public static ulong SnapToDial(string countryCode, ulong frequencyHz, string? mode)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return frequencyHz;

            Channel? ch = ChannelAt(alloc.Value, frequencyHz);
            bool toSegment = false;
            if (ch == null)
            {
                var seg0 = alloc.Value.Segment;
                if (seg0 != null && InSegment(seg0, frequencyHz))
                {
                    toSegment = true;
                }
                else
                {
                    ulong best = ulong.MaxValue;
                    foreach (var c in alloc.Value.Channels)
                    {
                        ulong d = System.Math.Min(Distance(frequencyHz, c.CentreHz),
                            Distance(frequencyHz, c.CentreHz - CarrierBelowCentreHz));
                        if (d < best) { best = d; ch = c; }
                    }
                    if (seg0 != null)
                    {
                        ulong d = frequencyHz < seg0.StartHz ? seg0.StartHz - frequencyHz : frequencyHz - seg0.EndHz;
                        if (d < best) { ch = null; toSegment = true; }
                    }
                }
            }

            if (ch != null) return ChannelDialHz(ch, ChannelFinalMode(mode));
            if (!toSegment) return frequencyHz;

            var seg = alloc.Value.Segment!;
            var e = NominalEmission(SegmentFinalMode(mode))!.Value;
            long lowest = (long)seg.StartHz - e.Low;
            long highest = (long)seg.EndHz - e.High;
            long f = (long)frequencyHz;
            if (f < lowest) f = lowest;
            if (f > highest) f = highest;
            return (ulong)f;
        }

        /// <summary>
        /// Where 60 m starts with nothing remembered: the first channel's dial
        /// for the mode the slice will end in there (5330.5 kHz in USB, 5332.0
        /// in CW). A country with no allocation uses the US table, as before.
        /// </summary>
        public static ulong DefaultDialHz(string countryCode, string? mode)
        {
            var alloc = GetAllocation(countryCode) ?? GetAllocation("US")!;
            return ChannelDialHz(alloc.Value.Channels[0], ChannelFinalMode(mode));
        }
    }
}
