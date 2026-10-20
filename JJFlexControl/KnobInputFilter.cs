using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JJFlexControl
{
    /// <summary>
    /// Stands between the Flex Control's decoded tokens and anything that acts on them, and
    /// drops bursts of input no hand could produce (#520). Pure: no port, no clock, no thread. The
    /// caller passes each token's arrival time and calls <see cref="Release"/> when
    /// <see cref="NextDueMs"/> comes round, and <see cref="Flush"/> when the port closes.
    /// </summary>
    /// <remarks>
    /// <para>Two of Noel's 2026-09-02 bench rulings, with their numbers from that measurement:</para>
    /// <list type="bullet">
    /// <item><b>A burst of three or more inputs faster than a hand is discarded.</b> The device
    /// holds input while the port is closed and replays it at line rate on open, and a stalled
    /// reader lands a backlog the same way. The bench measured gaps in whole multiples of 15.6 ms
    /// and never more than two events in one such tick from a hand, so three or more inside one
    /// tick is the burst. <b>A replay of only one or two inputs is NOT caught</b>: the measured
    /// ceiling of a hand is two per tick, so nothing here can tell those from a live turn. That is
    /// an open question for Noel, not a rule this class makes.</item>
    /// <item><b>A device report is never discarded with the burst.</b> <c>F0304</c> arrives in
    /// exactly that opening window and is probably the identity report. Any token that is not
    /// one of the fourteen input codes passes through at once and is not counted.</item>
    /// </list>
    /// <para><b>The ruling to suppress rotation near a press code is NOT built here.</b> It was
    /// built and taken out (repair round one): the measured long-press phantom lands 3.8 s before
    /// its press code, the double-press gap was never measured, and a guard wide enough to matter
    /// also drops a real final detent and delays every turn. It waits on a bench measurement.</para>
    /// <para><b>Timing is counted in whole ticks of 15.625 ms.</b> Two arrivals are
    /// <see cref="TicksBetween"/> ticks apart, rounded to the nearest whole tick. Whether that
    /// spacing is the knob's own report clock or the probe's poll loop is unsettled
    /// (ASK-520-tick-clock): the arrivals are host times either way. Input codes that follow one
    /// another with no whole tick between them form one group, and a group of more than two is a
    /// burst however long it chains: a replay at 9600 baud lands an event every two to four
    /// milliseconds.</para>
    /// <para><b>What it costs: each input is held until its group can take no more members, half
    /// a tick (about 8 ms) after the group's last arrival.</b> That is the smallest hold the rule
    /// allows, because any input could be the first of a burst, and dispatching it at once would
    /// let the first two steps of every replay through. Order is preserved.</para>
    /// </remarks>
    public sealed class KnobInputFilter
    {
        /// <summary>The measured spacing: 15.6 ms, taken as 1/64 s exactly (#520).</summary>
        public const double TickMs = 1000.0 / 64.0;

        /// <summary>
        /// The measured ceiling of a hand: never more than about two events in one tick (#520).
        /// Three or more in one tick is a burst.
        /// </summary>
        public const int MaxEventsPerTick = 2;

        /// <summary>
        /// A group can take no more members once half a tick has passed since its last arrival,
        /// because any later arrival rounds to at least one whole tick away. This is also the
        /// whole hold an input waits.
        /// </summary>
        public const double GroupCloseMs = 0.5 * TickMs;

        // A deadline handed out by NextDueMs must be met when Release is called AT it. Without
        // this, (t + hold) - t can come out a hair under hold, nothing is released, and the timer
        // is rearmed for the same instant forever.
        private const double DeadlineSlackMs = 1e-6;

        private static readonly HashSet<string> Rotations = new HashSet<string> { "D", "U" };
        private static readonly HashSet<string> Presses = new HashSet<string>
        {
            "S", "C", "L",
            "X1S", "X1C", "X1L",
            "X2S", "X2C", "X2L",
            "X3S", "X3C", "X3L",
        };

        /// <summary>True for the knob's two rotation codes.</summary>
        public static bool IsRotation(string token) => token != null && Rotations.Contains(token);

        /// <summary>True for any of the twelve press codes, the knob's own and the three buttons'.</summary>
        public static bool IsPress(string token) => token != null && Presses.Contains(token);

        /// <summary>
        /// True for anything that is not one of the fourteen input codes, such as <c>F0304</c>.
        /// These are never filtered, delayed or counted toward a burst.
        /// </summary>
        public static bool IsDeviceReport(string token) => !IsRotation(token) && !IsPress(token);

        /// <summary>How many whole ticks separate two arrivals, rounded to nearest.</summary>
        public static int TicksBetween(double earlierMs, double laterMs) =>
            (int)Math.Floor(Math.Abs(laterMs - earlierMs) / TickMs + 0.5);

        private sealed class TickGroup
        {
            public readonly List<string> Members = new List<string>();
            public double FirstAt;
            public double LastAt;
        }

        private readonly Action<string> _note;
        private TickGroup _open;
        private double _decidedThrough = double.NegativeInfinity;

        /// <param name="note">Told, in a sentence, about every discard, so the trace shows what
        /// the knob sent and why it did nothing. May be null.</param>
        public KnobInputFilter(Action<string> note = null)
        {
            _note = note;
        }

        /// <summary>Inputs discarded because they arrived faster than a hand could produce them.</summary>
        public int BurstEventsDiscarded { get; private set; }

        /// <summary>
        /// When <see cref="Release"/> next has something to decide, in the same clock as the
        /// arrival times; null when nothing is held.
        /// </summary>
        public double? NextDueMs => _open == null ? (double?)null : _open.LastAt + GroupCloseMs;

        /// <summary>
        /// Take one decoded token that arrived at <paramref name="atMs"/>. Returns, in order,
        /// whatever may now be acted on.
        /// </summary>
        public IReadOnlyList<string> Accept(string token, double atMs)
        {
            // Time never runs backwards here: an arrival stamped before a Release already decided
            // that instant is treated as arriving at it, so it cannot join a group that has
            // already been acted on.
            if (atMs < _decidedThrough) atMs = _decidedThrough;

            var output = new List<string>();
            ReleaseInto(output, atMs);

            if (IsDeviceReport(token))
            {
                output.Add(token);
                return output;
            }

            if (_open == null)
            {
                _open = new TickGroup { FirstAt = atMs };
            }
            _open.Members.Add(token);
            _open.LastAt = atMs;
            return output;
        }

        /// <summary>
        /// Decide whatever is due by <paramref name="nowMs"/>. Returns, in order, the tokens that
        /// may now be acted on.
        /// </summary>
        public IReadOnlyList<string> Release(double nowMs)
        {
            var output = new List<string>();
            ReleaseInto(output, nowMs);
            return output;
        }

        /// <summary>
        /// The port is closing and nothing more will arrive: decide what is held now, so a real
        /// step taken just before a close is acted on and a burst is still discarded.
        /// </summary>
        public IReadOnlyList<string> Flush()
        {
            var output = new List<string>();
            if (_open != null) Decide(output);
            return output;
        }

        private void ReleaseInto(List<string> output, double nowMs)
        {
            if (nowMs > _decidedThrough) _decidedThrough = nowMs;
            if (_open != null && nowMs - _open.LastAt >= GroupCloseMs - DeadlineSlackMs) Decide(output);
        }

        private void Decide(List<string> output)
        {
            TickGroup group = _open;
            _open = null;
            if (group.Members.Count <= MaxEventsPerTick)
            {
                output.AddRange(group.Members);
                return;
            }

            BurstEventsDiscarded += group.Members.Count;
            _note?.Invoke(group.Members.Count + " events with no whole tick between them, over "
                + (group.LastAt - group.FirstAt).ToString("0.0", CultureInfo.InvariantCulture)
                + " ms, discarded as a burst no hand could produce: "
                + string.Join(",", group.Members));
        }
    }
}
