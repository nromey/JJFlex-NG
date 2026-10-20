using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JJFlexControl
{
    /// <summary>
    /// Stands between the Flex Control's decoded tokens and anything that acts on them, and
    /// ignores what the knob replays when the port opens (#520). Pure: no port, no clock, no
    /// thread. One filter per open; the caller passes the open time and each token's arrival time.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule, ruled by Noel 2026-10-20 at 12:25:</b> <i>"Why can't we just wait until
    /// the knob calms down."</i> Turns saved while nothing was listening are thrown away, never
    /// replayed, so at open every input code is ignored until the knob has gone quiet: nothing at
    /// all from it for <see cref="QuietGapMs"/>. After that every input code is acted on at once,
    /// with no hold and no grouping, and nothing real is ever dropped. This replaces a fixed
    /// opening window (07:57, option A) whose length had to guess how big the saved pile could
    /// get, and could not.</para>
    /// <para><b>Any token restarts the quiet timer, a device report included.</b> Noel, 12:25:
    /// <i>"The knob sends these queued actions in batches, wait until it calms down and then be
    /// ready to tune."</i> An <c>F0304</c> between two batches is the knob still talking, so it
    /// must not end the wait early.</para>
    /// <para><b>A device report is never discarded, waiting or not.</b> <c>F0304</c> arrives in
    /// exactly this opening moment and is probably the identity report. Any token that is not
    /// one of the fourteen input codes passes through at once.</para>
    /// <para><b>What it costs:</b> if the knob is being turned as the port opens, nothing happens
    /// until the turning pauses for <see cref="QuietGapMs"/>. That is the trade Noel accepted.
    /// There is deliberately no cap on the wait, no sound and no setting.</para>
    /// <para><b>The wait ends lazily.</b> The filter has no timer: the knob is declared ready by
    /// the first token that arrives a full gap after the last one, and that token is acted on.
    /// Nothing is held, so nothing waits on the declaration.</para>
    /// <para><b>Not built here:</b> the press-nudge ruling. It waits on a bench measurement of the
    /// gap between a phantom step and its press code.</para>
    /// </remarks>
    public sealed class KnobInputFilter
    {
        /// <summary>
        /// How long the knob must send nothing at all before its input is acted on: half a second.
        /// </summary>
        /// <remarks>
        /// <para><b>No measurement sets this number; it is chosen to err long.</b> The bench
        /// (#520) saw every opening sequence, the <c>F0304</c> reports and any replay, arrive in
        /// the first 25 ms, but its probe polled with a 5 ms sleep and timestamped what each poll
        /// read, so it cannot show the pause between the knob's replay batches. Nobody has
        /// measured that pause.</para>
        /// <para><b>It must be longer than any pause inside the replay, real or apparent.</b> The
        /// apparent ones come from our side: Windows hands serial text over in callbacks, so the
        /// gaps this filter sees are gaps between callbacks, not between bytes. A quiet gap of
        /// 7.8 ms (round two) ended between two callbacks 10 ms apart and sent the replay's tail
        /// to the radio. Half a second is twenty times the whole measured opening sequence, about
        /// thirty-two of the knob's 15.6 ms report ticks, and comfortably longer than an ordinary
        /// stall of our reader thread (a garbage collection, a busy thread pool), which is
        /// typically tens of milliseconds. Speculation, unmeasured: a USB serial driver commonly
        /// batches on a timer of around 16 ms; the knob's driver has not been identified.</para>
        /// <para><b>Too long costs only a moment's deafness at connect</b>; too short replays the
        /// saved pile onto the radio. A bench trace prints every gap it saw while waiting, which is
        /// the reading that would justify shortening it.</para>
        /// </remarks>
        public const double QuietGapMs = 500.0;

        /// <summary>The trace lists at most this many arrival gaps, and says how many it left out.</summary>
        public const int GapsListed = 100;

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
        /// These are never discarded.
        /// </summary>
        public static bool IsDeviceReport(string token) => !IsRotation(token) && !IsPress(token);

        private readonly double _openedAt;
        private readonly Action<string> _note;
        private double _lastArrival;
        private bool _waiting = true;
        private int _ignored;
        private int _reportsPassed;
        private readonly List<string> _ignoredOrder = new List<string>();
        private readonly Dictionary<string, int> _ignoredCounts = new Dictionary<string, int>();
        private readonly List<double> _arrivals = new List<double>();
        private int _arrivalsNotListed;

        /// <param name="openedAtMs">When the port was asked to open, on the same clock as the arrivals.</param>
        /// <param name="note">Told, in a sentence, what the wait ignored and when it ended. May be null.</param>
        public KnobInputFilter(double openedAtMs, Action<string> note = null)
        {
            _openedAt = openedAtMs;
            _lastArrival = openedAtMs;
            _note = note;
        }

        /// <summary>Input codes ignored while waiting for the knob to go quiet.</summary>
        public int ReplayEventsDiscarded => _ignored;

        /// <summary>True until the knob has gone quiet for <see cref="QuietGapMs"/>.</summary>
        public bool WaitingForQuiet => _waiting;

        /// <summary>
        /// Take one decoded token that arrived at <paramref name="atMs"/>. Returns true when it
        /// should be acted on, which is at once: there is no hold.
        /// </summary>
        public bool Accept(string token, double atMs)
        {
            if (!_waiting) return true;

            if (atMs - _lastArrival >= QuietGapMs)
            {
                DeclareReady(_lastArrival + QuietGapMs, "the next code came at " + Ms(atMs - _openedAt) + " ms and was acted on");
                return true;
            }

            // Still waiting. Every token, a device report included, restarts the quiet timer.
            if (_arrivals.Count == 0 || atMs > _lastArrival)
            {
                if (_arrivals.Count < GapsListed + 1) _arrivals.Add(atMs);
                else _arrivalsNotListed++;
            }
            _lastArrival = Math.Max(_lastArrival, atMs);

            if (IsDeviceReport(token))
            {
                _reportsPassed++;
                return true;
            }
            _ignored++;
            if (_ignoredCounts.TryGetValue(token, out int n)) _ignoredCounts[token] = n + 1;
            else
            {
                _ignoredCounts[token] = 1;
                _ignoredOrder.Add(token);
            }
            return false;
        }

        /// <summary>
        /// The port is closing; say what the wait ignored if it never got to say so. Nothing is
        /// held, so there is nothing to release.
        /// </summary>
        public void Close(double atMs)
        {
            if (!_waiting) return;
            if (atMs - _lastArrival >= QuietGapMs)
                DeclareReady(_lastArrival + QuietGapMs, "nothing more came before the port closed at " + Ms(atMs - _openedAt) + " ms");
            else
                Report("the port closed at " + Ms(atMs - _openedAt) + " ms after the open call with the knob never quiet for " + Ms(QuietGapMs) + " ms");
        }

        private void DeclareReady(double readyAt, string how)
        {
            _waiting = false;
            Report("knob ready " + Ms(readyAt - _openedAt) + " ms after the open call, once it had sent nothing for " + Ms(QuietGapMs) + " ms; " + how);
        }

        private void Report(string ending)
        {
            if (_note == null) return;
            var line = new StringBuilder(ending);
            line.Append(". While waiting it ignored ").Append(_ignored).Append(" input codes");
            if (_ignored > 0)
                line.Append(" (").Append(string.Join(", ", _ignoredOrder.Select(t => t + " " + _ignoredCounts[t]))).Append(')');
            line.Append(" and passed ").Append(_reportsPassed).Append(" device reports.");
            if (_arrivals.Count == 0)
            {
                line.Append(" Nothing arrived while waiting.");
            }
            else
            {
                line.Append(" Arrivals: the first at ").Append(Ms(_arrivals[0] - _openedAt)).Append(" ms after the open call");
                if (_arrivals.Count > 1)
                {
                    line.Append(", then gaps of ");
                    line.Append(string.Join(", ", Enumerable.Range(1, _arrivals.Count - 1).Select(i => Ms(_arrivals[i] - _arrivals[i - 1]))));
                    line.Append(" ms");
                }
                if (_arrivalsNotListed > 0) line.Append(", and ").Append(_arrivalsNotListed).Append(" more arrivals not listed");
                line.Append('.');
            }
            line.Append(" These are the times Windows handed the text over, one callback often carrying many codes, not the times the bytes left the knob.");
            _note(line.ToString());
        }

        private static string Ms(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
