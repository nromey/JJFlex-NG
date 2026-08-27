#nullable enable
using System;
using System.Globalization;

namespace Radios
{
    /// <summary>
    /// A length of time as words a screen reader speaks cleanly: "42 seconds",
    /// "1 minute 40 seconds", "2 hours 5 minutes".
    /// </summary>
    /// <remarks>
    /// One home for the rule, in the <see cref="SMeterReading"/> tradition.
    /// Words rather than "1:40" because a colon-form duration is read
    /// inconsistently by synthesizers ("one forty", "one colon forty"), and
    /// whole numbers per the numeric-identifiers convention. Singulars are
    /// handled here because a lexicon template cannot pluralize.
    /// </remarks>
    public static class SpokenDuration
    {
        public static string English(TimeSpan span) => English(span.TotalSeconds);

        public static string English(double totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            long s = (long)Math.Round(totalSeconds);

            if (s < 1) return "under a second";
            if (s < 60) return Unit(s, "second");

            long minutes = s / 60;
            long seconds = s % 60;
            if (minutes < 60)
            {
                return seconds == 0
                    ? Unit(minutes, "minute")
                    : Unit(minutes, "minute") + " " + Unit(seconds, "second");
            }

            long hours = minutes / 60;
            minutes %= 60;
            return minutes == 0
                ? Unit(hours, "hour")
                : Unit(hours, "hour") + " " + Unit(minutes, "minute");
        }

        private static string Unit(long n, string word)
            => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? word : word + "s");
    }
}
