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
