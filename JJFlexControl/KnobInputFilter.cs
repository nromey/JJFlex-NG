using System;
using System.Collections.Generic;
using System.Linq;

namespace JJFlexControl
{
    /// <summary>
    /// Stands between the Flex Control's decoded tokens and anything that acts on them, and
    /// drops the input nobody's hand produced (#520). Pure: no port, no clock, no thread. The
    /// caller passes each token's arrival time and calls <see cref="Release"/> when
    /// <see cref="NextDueMs"/> comes round.
    /// </summary>
    /// <remarks>
    /// <para>Every rule here was ruled by Noel at the bench on 2026-09-02, and every number comes
    /// from that measurement (#520), not from this code:</para>
    /// <list type="bullet">
    /// <item><b>A burst faster than a hand is discarded.</b> The knob reports on a 15.6 ms clock
    /// and a genuine turn never puts more than two events in one tick. The device holds input
    /// while the port is closed and replays it at line rate on open; a stalled reader lands a
    /// backlog the same way. Both put many events inside one tick, so one rule covers both.</item>
    /// <item><b>A device report is never discarded with the burst.</b> <c>F0304</c> arrives in
    /// exactly that opening window and is probably the identity report. Any token that is not
    /// one of the fourteen input codes passes through untouched and is not counted.</item>
    /// <item><b>Rotation within a tick or two of any press code is discarded.</b> Pressing
    /// mechanically nudges the encoder, and that phantom step would move the frequency every
    /// time the operator presses. The window is the upper end of the ruling, two ticks, and it
    /// is symmetric: the measured phantoms came BEFORE their press codes, so a rotation is held
    /// for that long to see whether a press follows.</item>
    /// </list>
    /// <para><b>Timing is counted in device ticks, never in raw milliseconds.</b> Two arrivals
    /// are <see cref="TicksBetween"/> ticks apart, rounded to the nearest whole tick, because
    /// the wrist has already been sampled at 64 Hz before any byte reaches us. A gap that
    /// rounds to zero means the same tick, and input codes that follow one another with no whole
    /// tick between them form one group. A group of more than two is a burst, however long it
    /// runs: a replay at 9600 baud lands an event every two to four milliseconds, so the whole
    /// backlog chains into one group and goes together.</para>
    /// <para>What it costs: a press is held until its tick is over (under 8 ms) and a rotation
    /// until no press can still fall inside two ticks of it (about 39 ms). Order is
    /// preserved.</para>
    /// </remarks>
    public sealed class KnobInputFilter
    {
        /// <summary>The knob's report clock: 15.6 ms measured, 1/64 s exactly (#520).</summary>
        public const double TickMs = 1000.0 / 64.0;

        /// <summary>
        /// The physical ceiling of a hand: a genuine turn never puts more than about two events in
        /// one tick (#520). Three or more in one tick is a burst.
        /// </summary>
        public const int MaxEventsPerTick = 2;

        /// <summary>"Within a tick or two of any press code" (#520), taken at its upper end.</summary>
        public const int PressGuardTicks = 2;

        /// <summary>
        /// How long a rotation must be held before no press can still arrive within
        /// <see cref="PressGuardTicks"/> of it: a gap of 2.5 ticks rounds to 3.
        /// </summary>
        public const double RotationHoldMs = (PressGuardTicks + 0.5) * TickMs;

        /// <summary>A tick's group can take no more members once half a tick has passed.</summary>
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

        /// <summary>How many whole device ticks separate two arrivals, rounded to nearest.</summary>
        public static int TicksBetween(double earlierMs, double laterMs) =>
            (int)Math.Floor(Math.Abs(laterMs - earlierMs) / TickMs + 0.5);

        private sealed class TickGroup
        {
            public int Size;
        }

        private sealed class Held
        {
            public string Token;
            public double At;
            public TickGroup Group;
        }

        private readonly Action<string> _note;
        private readonly List<Held> _held = new List<Held>();
        private readonly List<double> _pressTimes = new List<double>();
        private TickGroup _openGroup;
        private double _lastInputAt = double.NaN;
        private double _decidedThrough = double.NegativeInfinity;

        /// <param name="note">Told, in a sentence, about every discard, so the trace shows what
        /// the knob sent and why it did nothing. May be null.</param>
        public KnobInputFilter(Action<string> note = null)
        {
            _note = note;
        }

        /// <summary>Events discarded because they arrived faster than a hand could produce them.</summary>
        public int BurstEventsDiscarded { get; private set; }

        /// <summary>Rotation steps discarded because they fell within two ticks of a press code.</summary>
        public int PhantomStepsDiscarded { get; private set; }

        /// <summary>
        /// When <see cref="Release"/> next has something to decide, in the same clock as the
        /// arrival times; null when nothing is held.
        /// </summary>
        public double? NextDueMs
        {
            get
            {
                if (_held.Count == 0) return null;
                Held head = _held[0];
                // The head's group may still be open by reference after its time has passed;
                // the deadline is the LATER of the group closing and the head's own hold, so a
                // Release at it always decides something.
                double closesAt = ReferenceEquals(head.Group, _openGroup)
                    ? _lastInputAt + GroupCloseMs
                    : head.At;
                if (IsRotation(head.Token) && head.Group.Size <= MaxEventsPerTick)
                {
                    return Math.Max(closesAt, head.At + RotationHoldMs);
                }
                return closesAt;
            }
        }

        /// <summary>
        /// Take one decoded token that arrived at <paramref name="atMs"/>. Returns, in order,
        /// whatever may now be acted on. Arrival times must not go backwards.
        /// </summary>
        public IReadOnlyList<string> Accept(string token, double atMs)
        {
            // Time never runs backwards here: an arrival stamped before a Release already decided
            // that instant is treated as arriving at it, so it cannot join a group whose members
            // have already been acted on.
            if (atMs < _decidedThrough) atMs = _decidedThrough;

            var output = new List<string>();
            ReleaseInto(output, atMs);

            if (IsDeviceReport(token))
            {
                output.Add(token);
                return output;
            }

            if (_openGroup == null || double.IsNaN(_lastInputAt) || TicksBetween(_lastInputAt, atMs) != 0)
            {
                _openGroup = new TickGroup();
            }
            _openGroup.Size++;
            _lastInputAt = atMs;
            if (IsPress(token)) _pressTimes.Add(atMs);

            _held.Add(new Held { Token = token, At = atMs, Group = _openGroup });
            ReleaseInto(output, atMs);
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

        private void ReleaseInto(List<string> output, double nowMs)
        {
            if (nowMs > _decidedThrough) _decidedThrough = nowMs;
            while (_held.Count > 0)
            {
                Held head = _held[0];

                bool groupClosed = !ReferenceEquals(head.Group, _openGroup)
                    || nowMs - _lastInputAt >= GroupCloseMs - DeadlineSlackMs;
                if (!groupClosed) break;

                if (head.Group.Size > MaxEventsPerTick)
                {
                    DiscardBurst(head.Group);
                    continue;
                }

                if (IsPress(head.Token))
                {
                    _held.RemoveAt(0);
                    output.Add(head.Token);
                    continue;
                }

                // A rotation. A press inside the window, before or after it, makes it a phantom.
                if (NearPress(head.At))
                {
                    _held.RemoveAt(0);
                    PhantomStepsDiscarded++;
                    _note?.Invoke("rotation " + head.Token + " discarded: within "
                        + PressGuardTicks + " ticks of a press code, so the press nudged the encoder");
                    continue;
                }

                if (nowMs - head.At < RotationHoldMs - DeadlineSlackMs) break; // a press may yet arrive

                _held.RemoveAt(0);
                output.Add(head.Token);
            }

            // Press times older than any rotation still to be judged are no longer needed.
            double horizon = (_held.Count > 0 ? _held[0].At : nowMs) - RotationHoldMs;
            _pressTimes.RemoveAll(t => t < horizon);
        }

        private bool NearPress(double rotationAt) =>
            _pressTimes.Any(p => TicksBetween(p, rotationAt) <= PressGuardTicks);

        private void DiscardBurst(TickGroup group)
        {
            List<Held> members = _held.Where(h => ReferenceEquals(h.Group, group)).ToList();
            _held.RemoveAll(h => ReferenceEquals(h.Group, group));
            BurstEventsDiscarded += members.Count;
            if (members.Count == 0) return;
            double span = members[members.Count - 1].At - members[0].At;
            _note?.Invoke(members.Count + " events with no whole tick between them, over "
                + span.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " ms, discarded as a burst no hand could produce: "
                + string.Join(",", members.Select(m => m.Token)));
        }
    }
}
