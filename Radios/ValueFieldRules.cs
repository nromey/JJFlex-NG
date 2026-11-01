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
    }
}
