#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// What a Loopback ending really put back (#835, fix round 2026-10-29,
    /// item 2), and the one sentence that says so.
    /// </summary>
    /// <remarks>
    /// On the bench (2026-10-29 02:16-02:18) the radio was still transmitting on
    /// a hand-mic PTT when Noel disconnected; every Loopback restore was
    /// abandoned with the radio, and the window said "Loopback ended. Antenna,
    /// power, monitor and duplex settings restored. Some loopback settings could
    /// not be restored". The ending now names only what was put back.
    /// </remarks>
    public sealed class LoopbackEndOutcome
    {
        public const string Antenna = "antenna";
        public const string Power = "power";
        public const string Monitor = "monitor";
        public const string Duplex = "duplex";
        public const string Drive = "drive";
        public const string Mutes = "mutes";

        /// <summary>The order the parts are named in: the transmit chain first, the slices last.</summary>
        internal static readonly string[] Order = { Antenna, Power, Monitor, Duplex, Drive, Mutes };

        public LoopbackEndOutcome(IReadOnlyList<string> restored, IReadOnlyList<string> notRestored, string notes, string trouble)
        {
            Restored = InOrder(restored);
            NotRestored = InOrder(notRestored);
            Notes = notes ?? "";
            Trouble = trouble ?? "";
        }

        /// <summary>The parts of the station that were put back, named in <see cref="Order"/>.</summary>
        public IReadOnlyList<string> Restored { get; }

        /// <summary>The parts that were not: refused, unconfirmed, or abandoned with the radio.</summary>
        public IReadOnlyList<string> NotRestored { get; }

        /// <summary>What concerns the listening slice itself, already in sentences.</summary>
        public string Notes { get; }

        /// <summary>The trouble text <see cref="FlexBase.EndLoopbackArrangementAsync"/> has always returned.</summary>
        public string Trouble { get; }

        /// <summary>True when there was an arrangement and its ending ran.</summary>
        public bool Ended { get; private init; } = true;

        internal static LoopbackEndOutcome NothingArranged { get; } =
            new(Array.Empty<string>(), Array.Empty<string>(), "", "") { Ended = false };

        internal static LoopbackEndOutcome StillRunning(string trouble) =>
            new(Array.Empty<string>(), Array.Empty<string>(), trouble.Trim(), trouble) { Ended = false };

        /// <summary>
        /// The ending, as the window says it. Everything put back: the sentence
        /// it has always said. Something not: what was and what was not, by
        /// name, never a claim the radio did not confirm.
        /// </summary>
        public string Sentence()
        {
            string lead;
            if (!Ended) lead = "";
            else if (NotRestored.Count == 0)
                lead = Lexicon.Get("audio.check.loopback_ended");
            else if (Restored.Count == 0)
                // DRAFT PROSE (#835): awaiting Noel.
                lead = Lexicon.Get("audio.check.loopback_ended_nothing_restored", ("missing", Join(NotRestored)));
            else
                // DRAFT PROSE (#835): awaiting Noel.
                lead = Lexicon.Get("audio.check.loopback_ended_partly_restored",
                    ("restored", Join(Restored)), ("missing", Join(NotRestored)));
            return string.Join(" ", new[] { lead, Notes }.Where(s => s.Length > 0));
        }

        private static IReadOnlyList<string> InOrder(IReadOnlyList<string>? parts) =>
            (parts ?? Array.Empty<string>()).OrderBy(p => Array.IndexOf(Order, p) is int i && i >= 0 ? i : Order.Length).ToList();

        // DRAFT PROSE (#835): awaiting Noel.
        private static string PartName(string part) => Lexicon.Get(part switch
        {
            Antenna => "audio.check.loopback_part.antenna",
            Power => "audio.check.loopback_part.power",
            Monitor => "audio.check.loopback_part.monitor",
            Duplex => "audio.check.loopback_part.duplex",
            Drive => "audio.check.loopback_part.drive",
            _ => "audio.check.loopback_part.mutes",
        });

        private static string Join(IReadOnlyList<string> parts)
        {
            var names = parts.Select(PartName).ToList();
            if (names.Count == 1) return names[0];
            return string.Join(", ", names.Take(names.Count - 1)) + " " + Lexicon.Get("audio.check.loopback_part.and") + " " + names[^1];
        }
    }
}
