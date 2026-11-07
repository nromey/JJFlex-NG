#nullable enable
using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// What a meter is ABOUT, for choosing one: the grouping the meter picker
    /// offers so an operator does not arrow through a hundred meters to find
    /// the one they want.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruled by Noel 2026-08-21: <i>"users should be able to select the meter
    /// that they want, probably based on category, so that they don't have to
    /// sort through meters they may not need."</i> The six categories are the
    /// ones the register entry names. <see cref="Other"/> is a seventh so a
    /// meter nobody has classified is still offered rather than misfiled. The
    /// order here is the order the picker lists them in.
    /// </para>
    /// <para>
    /// This is function, not provenance. <see cref="MeterGroup"/> already
    /// partitions by where a meter comes from (the radio, a slice, an
    /// amplifier); that partition stays in each entry's text, and this is the
    /// one the operator chooses by.
    /// </para>
    /// </remarks>
    public enum MeterCategory
    {
        Transmit,
        Receive,
        PowerAndSwr,
        TemperatureAndVoltage,
        Amplifier,
        Audio,
        Other,
    }

    /// <summary>
    /// Which <see cref="MeterCategory"/> a meter belongs to, from the radio's
    /// source and name for it and the units it reports in.
    /// </summary>
    /// <remarks>
    /// The mapping is a DRAFT judgement, written from the meter names a
    /// FLEX-8600 and a FLEX-6300 publish (see <see cref="MeterInventory.Find"/>
    /// for the census). Rules are tried in order, so a meter that could be read
    /// two ways lands in the first: an amplifier's meters are the amplifier's
    /// whatever they measure; forward and reflected power and SWR are power and
    /// SWR wherever they come from; heat, voltage and current are temperature
    /// and voltage; a slice's meters are receive.
    /// </remarks>
    public static class MeterCategories
    {
        private static readonly HashSet<string> PowerAndSwrNames = Names("FWDPWR", "REFPWR", "SWR", "PAEFF");

        private static readonly HashSet<string> TemperatureAndVoltageNames =
            Names("PATEMP", "+13.8A", "+13.8B", "MAINFAN", "FAN", "TEMP");

        private static readonly HashSet<string> TransmitNames =
            Names("ALC", "HWALC", "TX_AGC", "TXAGC", "RM_TX_AGC", "ATTN_FPGA");

        private static readonly HashSet<string> AudioNames =
            Names("MIC", "MICPEAK", "COMPPEAK", "SC_MIC", "CODEC", "SC_FILT_1", "SC_FILT_2",
                  "AFTEREQ", "AFRAMP", "B4RAMP", "POST_P");

        /// <summary>Every category, in the order the picker offers them.</summary>
        public static IReadOnlyList<MeterCategory> InOrder { get; } = (MeterCategory[])Enum.GetValues(typeof(MeterCategory));

        /// <param name="source">The radio's source for the meter: <c>SLC</c>,
        /// <c>AMP</c>, <c>TX-</c> and so on. Empty when not known.</param>
        /// <param name="name">The radio's name for the meter.</param>
        /// <param name="unitsText">The units in words, as
        /// <see cref="MeterReading.UnitsText"/> gives them, or empty.</param>
        public static MeterCategory Of(string? source, string? name, string? unitsText)
        {
            string src = (source ?? "").Trim();
            string n = (name ?? "").Trim();
            string units = (unitsText ?? "").Trim();

            if (string.Equals(src, "AMP", StringComparison.OrdinalIgnoreCase)) return MeterCategory.Amplifier;

            if (PowerAndSwrNames.Contains(n) || Is(units, "watts") || Is(units, "to 1"))
                return MeterCategory.PowerAndSwr;

            if (TemperatureAndVoltageNames.Contains(n) || Is(units, "degrees C") || Is(units, "degrees F")
                || Is(units, "volts") || Is(units, "amps"))
                return MeterCategory.TemperatureAndVoltage;

            if (string.Equals(src, "SLC", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "LEVEL", StringComparison.OrdinalIgnoreCase))
                return MeterCategory.Receive;

            if (TransmitNames.Contains(n)) return MeterCategory.Transmit;
            if (AudioNames.Contains(n) || Is(units, "dBFS")) return MeterCategory.Audio;

            if (src.StartsWith("TX", StringComparison.OrdinalIgnoreCase)) return MeterCategory.Transmit;
            if (src.StartsWith("RX", StringComparison.OrdinalIgnoreCase)) return MeterCategory.Receive;

            return MeterCategory.Other;
        }

        private static bool Is(string units, string word) =>
            string.Equals(units, word, StringComparison.OrdinalIgnoreCase);

        private static HashSet<string> Names(params string[] names) =>
            new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }
}
