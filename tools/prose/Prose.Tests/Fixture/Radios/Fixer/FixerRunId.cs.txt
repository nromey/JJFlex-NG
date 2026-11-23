using System;
using System.Text;

namespace Radios.Fixer
{
    /// <summary>
    /// The test ID stamped on everything one Fixer run records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One per run, generated at the start, on every stage result, the trace
    /// and the report — so a support thread can say "run A7R-4W2" and mean one
    /// specific set of measurements.
    /// </para>
    /// <para>
    /// <b>The alphabet is culled for reading aloud, not for entropy.</b> This
    /// ID gets read down a phone by one person and typed into an email by
    /// another, quite possibly with a screen reader spelling it out — so every
    /// symbol that shares a rhyme family with another is out (the whole
    /// B-C-D-E-G-P-T-V-Z "ee" family keeps one member; M and N keep one; five
    /// and nine keep one — the same confusion that gave aviation "niner"), and
    /// so is every visual pair (0/O, 1/I/l, 5/S, 8/B, 2/Z, 6/G) because the
    /// phone's screen is the other half of the journey. What survives is
    /// twelve symbols, each a stranger to all the others by ear and by eye.
    /// </para>
    /// <para>
    /// Six symbols from twelve is about three million distinct IDs. This is a
    /// correlation handle for a person's own support thread, not a global key;
    /// three million is plenty, and a seventh symbol would cost more in the
    /// reading than it buys in the arithmetic.
    /// </para>
    /// </remarks>
    public static class FixerRunId
    {
        /// <summary>The survivors of the cull, and nothing else. Tests hold
        /// the confusion families this was culled against and fail if an
        /// edit ever reunites two members of one.</summary>
        public const string Alphabet = "234567ARTWXY";

        /// <summary>Symbols per group. Two groups, one dash: XXX-XXX.</summary>
        public const int GroupLength = 3;

        /// <summary>The character between the groups. A dash survives every
        /// mail client and is silent enough to skip when reading aloud.</summary>
        public const char Separator = '-';

        /// <summary>Total length including the separator.</summary>
        public const int Length = GroupLength * 2 + 1;

        /// <summary>A new ID.</summary>
        public static string New() => New(Random.Shared);

        /// <summary>A new ID from a caller-supplied source, so tests can be
        /// deterministic without the format test knowing any example ID.</summary>
        public static string New(Random rng)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            var sb = new StringBuilder(Length);
            for (int i = 0; i < GroupLength * 2; i++)
            {
                if (i == GroupLength) sb.Append(Separator);
                sb.Append(Alphabet[rng.Next(Alphabet.Length)]);
            }
            return sb.ToString();
        }
    }
}
