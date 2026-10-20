using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// 60 meter channelization data. US allocation per FCC Part 97.303(h):
    /// five channelized frequencies (USB, 100W PEP max) plus a digital segment.
    /// Sprint 22 Phase 10.
    /// </summary>
    public static class SixtyMeterChannels
    {
        public record Channel(double FrequencyMHz, string Mode, int MaxPowerW, string Label);
        public record DigiSegment(double StartMHz, double EndMHz, int MaxPowerW);

        /// <summary>US allocation (FCC Part 97.303(h)) — channelized.</summary>
        public static readonly Channel[] USChannels =
        {
            new(5.3320, "USB", 100, "Channel 1"),
            new(5.3480, "USB", 100, "Channel 2"),
            new(5.3585, "USB", 100, "Channel 3"),
            new(5.3730, "USB", 100, "Channel 4"),
            new(5.4050, "USB", 100, "Channel 5"),
        };

        /// <summary>US 60m digital segment (CW/digital modes, 100W PEP).</summary>
        public static readonly DigiSegment USDigiSegment = new(5.3515, 5.3665, 100);

        /// <summary>
        /// Country allocations keyed by country code.
        /// Future: add UK, Canada, etc. with their specific allocations.
        /// </summary>
        private static readonly Dictionary<string, (Channel[] Channels, DigiSegment? Digi)> CountryAllocations = new()
        {
            ["US"] = (USChannels, USDigiSegment),
        };

        /// <summary>Get the allocation for a country code, or null if not in table.</summary>
        public static (Channel[] Channels, DigiSegment? Digi)? GetAllocation(string countryCode)
        {
            if (string.IsNullOrEmpty(countryCode)) return null;
            return CountryAllocations.TryGetValue(countryCode.ToUpperInvariant(), out var alloc)
                ? alloc
                : null;
        }

        /// <summary>Total navigation stops (channels + digi segment if present).</summary>
        public static int GetNavigationStopCount(string countryCode)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return 0;
            return alloc.Value.Channels.Length + (alloc.Value.Digi != null ? 1 : 0);
        }

        /// <summary>
        /// Determines if the current mode is appropriate for a 60m frequency.
        /// Returns (requiredMode, segmentLabel) if a mode change is needed,
        /// (null, segmentLabel) if mode is already correct,
        /// or null if the frequency is not on a recognized 60m allocation.
        /// </summary>
        public static (string? RequiredMode, string SegmentLabel)? GetModeAdvisory(
            string countryCode, ulong frequencyHz, string currentMode)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return null;

            double freqMHz = frequencyHz / 1_000_000.0;

            // Check channelized frequencies (USB only)
            foreach (var ch in alloc.Value.Channels)
            {
                if (System.Math.Abs(freqMHz - ch.FrequencyMHz) < 0.0001)
                {
                    if (!string.Equals(currentMode, ch.Mode, System.StringComparison.OrdinalIgnoreCase))
                        return (ch.Mode, ch.Label);
                    return (null, ch.Label);
                }
            }

            // Check digi segment (CW/DIGU/DIGL only)
            if (alloc.Value.Digi is { } digi && freqMHz >= digi.StartMHz && freqMHz <= digi.EndMHz)
            {
                string upper = (currentMode ?? "").ToUpperInvariant();
                bool isDigiOrCW = upper is "CW" or "DIGU" or "DIGL";
                if (!isDigiOrCW)
                    return ("CW", "digital segment");
                return (null, "digital segment");
            }

            return null;
        }

        /// <summary>
        /// A mode the radio must be moved to on arrival at a 60m frequency, and
        /// the clause that says so — <c>", mode set to CW for digital segment"</c>.
        /// </summary>
        public sealed record ModeCorrection(string RequiredMode, string SegmentLabel, string Advisory);

        /// <summary>
        /// <see cref="GetModeAdvisory"/> turned into what every arrival path acts
        /// on: null when nothing has to change (not a 60m allocation, or already
        /// on a permitted mode), otherwise the mode to set and the advisory
        /// clause to append to whatever the path already says (#312).
        /// </summary>
        /// <remarks>
        /// One composition for every path that can land on 60m — the band jump,
        /// the channel rotation and typed frequency entry — so the three cannot
        /// grow three wordings for one correction. The band jump had this inline
        /// and was, until #312, the only path that applied it at all.
        /// </remarks>
        public static ModeCorrection? GetModeCorrection(
            string countryCode, ulong frequencyHz, string? currentMode)
        {
            var advisory = GetModeAdvisory(countryCode, frequencyHz, currentMode ?? "");
            if (advisory?.RequiredMode is not { } required) return null;
            return new ModeCorrection(required, advisory.Value.SegmentLabel,
                Lexicon.Get("settings.band.mode_advisory",
                    ("mode", required), ("segment", advisory.Value.SegmentLabel)));
        }

        /// <summary>
        /// One stop of the Alt+Shift+Up / Alt+Shift+Down rotation: where to tune,
        /// the mode to set (null to leave the mode alone), and the sentence.
        /// </summary>
        public sealed record RotationStop(int Index, ulong FrequencyHz, string? ModeToSet, string Announcement);

        /// <summary>
        /// The next stop of the 60m rotation — the five channels, then the
        /// digital segment — from <paramref name="currentIndex"/> in
        /// <paramref name="direction"/>, wrapping. Null when the country has no
        /// allocation or the allocation has no stops.
        /// </summary>
        /// <remarks>
        /// <para>A channel stop always sets the channel's own mode, exactly as
        /// it always has. <b>The digital-segment stop sets a mode only when the
        /// current one is not permitted there</b>, through
        /// <see cref="GetModeCorrection"/>, and says so with the band jump's own
        /// advisory clause (#312). Until #312 that stop tuned and spoke and
        /// never touched the mode, so one more press from a USB channel left the
        /// operator inside the digital-only segment on USB, told only that they
        /// had arrived.</para>
        /// <para>Pure: the caller tunes, sets the mode and speaks. Pulled out of
        /// <c>MainWindow.SixtyMeterChannelNavigate</c> so the decision can be
        /// tested without a window or a radio.</para>
        /// </remarks>
        public static RotationStop? Rotate(
            string countryCode, int currentIndex, int direction, string? currentMode)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return null;

            int stopCount = alloc.Value.Channels.Length + (alloc.Value.Digi != null ? 1 : 0);
            if (stopCount == 0) return null;

            int index = ((currentIndex + direction) % stopCount + stopCount) % stopCount;

            if (index < alloc.Value.Channels.Length)
            {
                var ch = alloc.Value.Channels[index];
                return new RotationStop(index, ToHz(ch.FrequencyMHz), ch.Mode,
                    Lexicon.Get("connect.home.sixty_meter_channel",
                        ("label", ch.Label), ("freq", $"{ch.FrequencyMHz:F4}"), ("mode", ch.Mode)));
            }

            var digi = alloc.Value.Digi!;
            ulong freqHz = ToHz(digi.StartMHz);
            string arrival = Lexicon.Get("connect.home.sixty_meter_digital", ("freq", $"{digi.StartMHz:F4}"));
            var correction = GetModeCorrection(countryCode, freqHz, currentMode);
            return new RotationStop(index, freqHz, correction?.RequiredMode,
                arrival + (correction?.Advisory ?? ""));
        }

        private static ulong ToHz(double mhz) => (ulong)(mhz * 1_000_000.0 + 0.5);

        /// <summary>
        /// Check if a frequency (in Hz) is on a valid 60m channel or within the digi segment.
        /// Returns true if the frequency is valid for 60m TX.
        /// </summary>
        public static bool IsValidSixtyMeterFrequency(string countryCode, ulong frequencyHz)
        {
            var alloc = GetAllocation(countryCode);
            if (alloc == null) return true; // No rules = no restrictions

            double freqMHz = frequencyHz / 1_000_000.0;

            // Check channelized frequencies (within 100 Hz tolerance)
            foreach (var ch in alloc.Value.Channels)
            {
                if (System.Math.Abs(freqMHz - ch.FrequencyMHz) < 0.0001)
                    return true;
            }

            // Check digi segment
            if (alloc.Value.Digi is { } digi)
            {
                if (freqMHz >= digi.StartMHz && freqMHz <= digi.EndMHz)
                    return true;
            }

            return false;
        }
    }
}
