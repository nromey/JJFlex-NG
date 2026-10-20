using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Tests
{
    /// <summary>
    /// #520: the Flex Control's port runs at 9600 baud, ten bits a byte, so a two-byte <c>D;</c>
    /// takes at least 2.083 ms to arrive. A fixture faster than that certifies a stream the port
    /// cannot carry; round three's test spaced codes 2.05 ms apart and hid a real spill that way.
    /// Every knob fixture goes through <see cref="AssertNoFasterThanTheWire"/>.
    /// </summary>
    /// <remarks>
    /// Speculation, recorded so nobody leans on this harder than it bears: if the knob is a USB
    /// virtual COM port, the 9600 is a setting the device may ignore, and bytes could come faster.
    /// The bench's own figures hint at that (nine events and two or three <c>F0304</c> in 25 ms is
    /// more bytes than 9600 baud carries in 25 ms). Holding fixtures to 9600 keeps them physically
    /// possible either way; the quiet-gap rule itself does not depend on the line rate.
    /// </remarks>
    internal static class FlexControlKnobWire
    {
        public const double Baud = 9600;

        /// <summary>The least time <paramref name="bytes"/> bytes take at 9600 baud, ten bits a byte.</summary>
        public static double MsFor(int bytes) => bytes * 10 * 1000.0 / Baud;

        /// <summary>The least time one code and its delimiter take on the wire.</summary>
        public static double MsFor(string code) => MsFor(code.Length + 1);

        /// <summary>
        /// Codes sent back to back from <paramref name="start"/>, each stamped when its last byte can
        /// first have landed, one callback per code: as fast as the port allows.
        /// </summary>
        public static List<(string token, double at)> BackToBack(double start, IEnumerable<string> codes)
        {
            var arrivals = new List<(string, double)>();
            double t = start;
            foreach (string code in codes)
            {
                t += MsFor(code);
                arrivals.Add((code, t));
            }
            return arrivals;
        }

        public static List<(string token, double at)> BackToBack(double start, string code, int count) =>
            BackToBack(start, Enumerable.Repeat(code, count));

        /// <summary>
        /// Throws when a stream is faster than the wire. Consecutive tokens with one time are one
        /// callback; each callback's bytes must fit between it and the callback before it (or the
        /// port opening, for the first).
        /// </summary>
        public static void AssertNoFasterThanTheWire(IEnumerable<(string token, double at)> arrivals, double openedAt = 0)
        {
            double previous = openedAt;
            double? chunkAt = null;
            int chunkBytes = 0;
            foreach (var (token, at) in arrivals)
            {
                if (chunkAt.HasValue && at == chunkAt.Value)
                {
                    chunkBytes += token.Length + 1;
                    continue;
                }
                if (chunkAt.HasValue)
                {
                    Check(previous, chunkAt.Value, chunkBytes);
                    previous = chunkAt.Value;
                }
                if (at < previous) throw new InvalidOperationException("fixture goes back in time at " + at + " ms");
                chunkAt = at;
                chunkBytes = token.Length + 1;
            }
            if (chunkAt.HasValue) Check(previous, chunkAt.Value, chunkBytes);
        }

        /// <summary>The same check for a pump fixture: <paramref name="text"/> handed over at <paramref name="at"/>.</summary>
        public static void AssertTextFits(double previous, double at, string text) =>
            Check(previous, at, text.Count(c => c != '\0'));

        private static void Check(double previous, double at, int bytes)
        {
            double least = MsFor(bytes);
            if (at - previous < least - 1e-9)
                throw new InvalidOperationException(
                    "fixture faster than 9600 baud: " + bytes + " bytes need " + least.ToString("0.000") + " ms, got " + (at - previous).ToString("0.000") + " ms at " + at + " ms");
        }
    }
}
