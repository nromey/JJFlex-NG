#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// What an operator's press does to a value field, and what the field
    /// calls its unit. Pure, because the control itself cannot be built on a
    /// live desk.
    /// </summary>
    /// <summary>What became of a number typed into a value field.</summary>
    public enum TypedEntry
    {
        /// <summary>The value set is the value typed.</summary>
        Taken,

        /// <summary>A value was set, but not the one typed: it was past an end of the range.</summary>
        Clamped,

        /// <summary>The field's owner refused it; the field keeps the value it had.</summary>
        Refused,

        /// <summary>It was not a number.</summary>
        NotANumber,
    }

    public static class ValueFieldRules
    {
        /// <summary>
        /// Judge a typed number. Every field goes on clamping as it always has;
        /// the answer exists for a press that would act on the value at once,
        /// which must not go on with a number the operator did not type.
        /// </summary>
        /// <param name="parsed">The text read as a number.</param>
        /// <param name="typed">The number, signed.</param>
        /// <param name="min">The field's minimum.</param>
        /// <param name="max">The field's maximum.</param>
        /// <param name="refusal">The owner's refusal, asked before any clamp; null when it has none.</param>
        /// <param name="value">The value to set, for Taken and Clamped.</param>
        /// <param name="refused">The sentence, for Refused.</param>
        public static TypedEntry Judge(bool parsed, int typed, int min, int max, Func<int, string?>? refusal,
            out int value, out string? refused)
        {
            value = typed;
            refused = null;
            if (!parsed) return TypedEntry.NotANumber;
            refused = refusal?.Invoke(typed);
            if (refused != null) return TypedEntry.Refused;
            value = Math.Clamp(typed, min, max);
            return value == typed ? TypedEntry.Taken : TypedEntry.Clamped;
        }

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
