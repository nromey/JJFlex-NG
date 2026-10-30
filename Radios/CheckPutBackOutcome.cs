#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// What the next connect's put-back did with the settings an audio check
    /// left on the radio (#835, fix round 1030): put back, left alone because
    /// something else had changed them, or not put back (written and not
    /// confirmed, or held because the radio began transmitting). The sentence
    /// the connect briefing says, with the alarm, and the reason kept in the
    /// reminder file until it has been said.
    /// </summary>
    /// <remarks>
    /// A setting that already held its original value is in none of the
    /// lists: nothing needed doing, and saying so per setting is noise for an
    /// operator who listens serially. When every setting was like that, the
    /// sentence says the settings were already back.
    /// </remarks>
    public sealed class CheckPutBackOutcome
    {
        private const string Marker = "put-back";

        public CheckPutBackOutcome(IEnumerable<string> putBack, IEnumerable<string> leftAlone, IEnumerable<string> notPutBack)
        {
            PutBack = Clean(putBack);
            LeftAlone = Clean(leftAlone);
            NotPutBack = Clean(notPutBack);
        }

        /// <summary>Settings (<see cref="CheckPutBack"/> names) put back, the radio having answered.</summary>
        public IReadOnlyList<string> PutBack { get; }

        /// <summary>Settings left alone: they no longer held the check's value.</summary>
        public IReadOnlyList<string> LeftAlone { get; }

        /// <summary>Settings not put back this time; still recorded for the next connect.</summary>
        public IReadOnlyList<string> NotPutBack { get; }

        /// <summary>One line, no tabs, for the reminder file: "put-back done=a,b left=c not=d".</summary>
        public string Reason =>
            Marker + " done=" + string.Join(",", PutBack) + " left=" + string.Join(",", LeftAlone) + " not=" + string.Join(",", NotPutBack);

        /// <summary>Read back a <see cref="Reason"/>; false for any other reason ("withheld", "unconfirmed").</summary>
        public static bool TryParse(string? reason, out CheckPutBackOutcome outcome)
        {
            outcome = new CheckPutBackOutcome(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            if (reason == null || !reason.StartsWith(Marker + " ", StringComparison.Ordinal)) return false;
            var lists = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (string field in reason[(Marker.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = field.IndexOf('=');
                if (eq <= 0) return false;
                lists[field[..eq]] = field[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
            string[] Get(string key) => lists.TryGetValue(key, out var v) ? v : Array.Empty<string>();
            outcome = new CheckPutBackOutcome(Get("done"), Get("left"), Get("not"));
            return true;
        }

        /// <summary>
        /// What an earlier put-back did that nobody has heard yet (a connect with
        /// speech suppressed, fix round 1030 P2), together with what a later one
        /// did. A setting the later one examined (<paramref name="examinedNow"/>)
        /// takes the later one's answer, or none when it already held its
        /// original; every other setting keeps the earlier answer.
        /// </summary>
        public static CheckPutBackOutcome Merge(CheckPutBackOutcome earlier, CheckPutBackOutcome now, IEnumerable<string> examinedNow)
        {
            if (earlier == null) return now;
            if (now == null) return earlier;
            var examined = new HashSet<string>(examinedNow ?? Array.Empty<string>(), StringComparer.Ordinal);
            IEnumerable<string> Kept(IEnumerable<string> list) => list.Where(s => !examined.Contains(s));
            return new CheckPutBackOutcome(
                Kept(earlier.PutBack).Concat(now.PutBack),
                Kept(earlier.LeftAlone).Concat(now.LeftAlone),
                Kept(earlier.NotPutBack).Concat(now.NotPutBack));
        }

        /// <summary>
        /// The connect briefing's sentence: a lead, then what was put back, what
        /// was left alone and what was not, each only when there is any.
        /// </summary>
        public string Sentence()
        {
            // DRAFT PROSE (#835): awaiting Noel.
            var parts = new List<string> { Lexicon.Get("audio.check.putback_lead") };
            if (PutBack.Count > 0)
                // DRAFT PROSE (#835): awaiting Noel.
                parts.Add(Lexicon.Get("audio.check.putback_done", ("parts", Name(PutBack))));
            if (LeftAlone.Count > 0)
                // DRAFT PROSE (#835): awaiting Noel.
                parts.Add(Lexicon.Get("audio.check.putback_left", ("parts", Name(LeftAlone))));
            if (NotPutBack.Count > 0)
                // DRAFT PROSE (#835): awaiting Noel.
                parts.Add(Lexicon.Get("audio.check.putback_not", ("parts", Name(NotPutBack))));
            if (parts.Count == 1)
                // DRAFT PROSE (#835): awaiting Noel.
                parts.Add(Lexicon.Get("audio.check.putback_already"));
            return string.Join(" ", parts);
        }

        /// <summary>The station part a setting is named as, reusing the Loopback ending's names.</summary>
        internal static string PartOf(string setting) => setting switch
        {
            CheckPutBack.TxAntenna => LoopbackEndOutcome.Antenna,
            CheckPutBack.RfPower => LoopbackEndOutcome.Power,
            CheckPutBack.TunePower => LoopbackEndOutcome.TunePower,
            CheckPutBack.Monitor => LoopbackEndOutcome.Monitor,
            CheckPutBack.FullDuplex => LoopbackEndOutcome.Duplex,
            CheckPutBack.XvtrDrive => LoopbackEndOutcome.Drive,
            _ => setting,
        };

        private static string Name(IEnumerable<string> settings) => LoopbackEndOutcome.NameParts(settings.Select(PartOf));

        private static IReadOnlyList<string> Clean(IEnumerable<string>? settings) =>
            (settings ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s) && s.IndexOfAny(new[] { ' ', ',', '=', '\t' }) < 0)
                .Distinct(StringComparer.Ordinal).ToList();
    }
}
