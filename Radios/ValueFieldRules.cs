#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// What an operator's press does to a value field, and what the field
    /// calls its unit. Pure, because the control itself cannot be built on a
    /// live desk.
    /// </summary>
    public static class ValueFieldRules
    {
        /// <summary>
        /// Where an arrow or Page key lands. <paramref name="floor"/> is the
        /// lowest an OPERATOR can reach, which can sit above the lowest the
        /// field can DISPLAY: a radio can report a value under the floor, and
        /// the field shows it. A press down from there stays put, so nothing is
        /// written to the radio because of what it reads.
        /// </summary>
        public static int Adjusted(int value, int delta, int min, int max, int floor)
        {
            int lowest = Math.Max(min, floor);
            if (delta < 0 && value <= lowest) return value;
            return Math.Clamp(value + delta, lowest, max);
        }

        /// <summary>
        /// The unit as it reads after this value: "1 watt", "5 watts". A value
        /// that shows decimals keeps the plural ("1.00 watts"), and a unit that
        /// is not a plural word does not change: "dB", "dBm", "dBFS", "percent",
        /// "hertz". Lower case and more than two letters is what tells a word
        /// from a symbol, so "ms" stays "ms".
        /// </summary>
        public static string UnitFor(string formattedValue, string unit)
        {
            if (formattedValue is not ("1" or "-1")) return unit;
            if (unit.Length <= 2 || !unit.EndsWith('s')) return unit;
            foreach (char c in unit) if (!char.IsLower(c)) return unit;
            return unit.Substring(0, unit.Length - 1);
        }
    }
}
