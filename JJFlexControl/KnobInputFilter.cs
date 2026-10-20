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
    /// thread. One filter per open; the caller passes the open time, each read's time, and what it
    /// saw when it checked the port's buffer.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule, ruled by Noel 2026-10-20 at 12:25:</b> <i>"Why can't we just wait until
    /// the knob calms down."</i> Turns saved while nothing was listening are thrown away, never
    /// replayed, so at open every input code is ignored until the knob is found quiet. After that
    /// every input code is acted on at once, with no hold and no grouping, and nothing real is ever
    /// dropped. This replaces a fixed opening window (07:57, option A) whose length had to guess
    /// how big the saved pile could get, and could not.</para>
    /// <para><b>"Quiet" means two things observed together, and only a buffer check can declare
    /// it</b> (round five, approved by Noel 12:52). A check, made by the caller between reads,
    /// finds the port's buffer EMPTY, and no text has been read for <see cref="QuietGapMs"/>.
    /// A read never declares the knob ready by itself, however long after the last one it comes.
    /// The reason: a read's time is when Windows got round to delivering the text, not when the
    /// bytes arrived. A first callback delayed past the gap, or a reader stalled between two saved
    /// batches, would otherwise make saved turns look like a fresh turn after a quiet spell, and
    /// tune the radio.</para>
    /// <para><b>Why that closes it, given the caller reads and checks under one lock:</b> bytes
    /// leave the port's buffer only by being read, and every read restarts the wait. So if any
    /// byte reached the buffer during the gap, either it was read during the gap, and the gap
    /// restarted, or it is still there when the check is made, and the check finds the buffer
    /// not empty. A check that finds it empty a full gap after the last read therefore shows that
    /// nothing reached the port's buffer for that whole gap.</para>
    /// <para><b>What it still cannot see:</b> bytes the knob has not yet sent, or that are still on
    /// their way to the port's buffer. A pause in the knob's own replay longer than the gap would
    /// end the wait early. That is the knob's timing, not ours, and only a bench trace of the
    /// gaps between batches can show it, which is why the gap errs long.</para>
    /// <para><b>Any read restarts the wait, a device report included.</b> Noel, 12:25: <i>"The knob
    /// sends these queued actions in batches, wait until it calms down and then be ready to
    /// tune."</i> An <c>F0304</c> between two batches is the knob still talking.</para>
    /// <para><b>A device report is never discarded, waiting or not.</b> <c>F0304</c> arrives in
    /// exactly this opening moment and is probably the identity report. Any token that is not
    /// one of the fourteen input codes passes through at once.</para>
    /// <para><b>What it costs:</b> if the knob is being turned as the port opens, nothing happens
    /// until the turning pauses for <see cref="QuietGapMs"/>, plus up to one
    /// <see cref="QuietCheckMs"/>. That is the trade Noel accepted. There is deliberately no cap on
    /// the wait, no sound and no setting.</para>
    /// <para><b>Not built here:</b> the press-nudge ruling. It waits on a bench measurement of the
    /// gap between a phantom step and its press code.</para>
    /// </remarks>
    public sealed class KnobInputFilter
    {
        /// <summary>
        /// How long no text may be read, ending in a buffer check that finds the buffer empty,
        /// before the knob's input is acted on: half a second.
        /// </summary>
        /// <remarks>
        /// <para><b>No measurement sets this number; it is chosen to err long.</b> The bench
        /// (#520) saw every opening sequence, the <c>F0304</c> reports and any replay, arrive in
        /// the first 25 ms, but its probe polled with a 5 ms sleep and timestamped what each poll
        /// read, so it cannot show the pause between the knob's replay batches. Nobody has
        /// measured that pause.</para>
        /// <para><b>It must be longer than any pause inside the knob's own replay.</b> Our side's
        /// delays, a late callback or a stalled reader, no longer matter here: the buffer check
        /// sees bytes waiting however late the callback is. Half a second is twenty times the
        /// whole measured opening sequence and about thirty-two of the knob's 15.6 ms report
        /// ticks.</para>
        /// <para><b>Too long costs only a moment's deafness at connect</b>; too short replays the
        /// saved pile onto the radio. A bench trace prints every gap between reads while waiting,
        /// which is the reading that would justify shortening it.</para>
        /// </remarks>
        public const double QuietGapMs = 500.0;

        /// <summary>
        /// How often the caller checks the port's buffer while waiting: every 50 ms, so the knob is
        /// found quiet at most this long after the gap has passed. Checking stops once it is.
        /// </summary>
        public const int QuietCheckMs = 50;

        /// <summary>The trace lists at most this many read gaps, and says how many it left out.</summary>
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
        private double _lastRead;
        private bool _waiting = true;
        private int _ignored;
        private int _reportsPassed;
        private int _checksWithBytesWaiting;
        private readonly List<string> _ignoredOrder = new List<string>();
        private readonly Dictionary<string, int> _ignoredCounts = new Dictionary<string, int>();
        private readonly List<double> _reads = new List<double>();
        private int _readsNotListed;

        /// <param name="openedAtMs">When the port was asked to open, on the same clock as the reads and checks.</param>
        /// <param name="note">Told, in a sentence, what the wait ignored and when it ended. May be null.</param>
        public KnobInputFilter(double openedAtMs, Action<string> note = null)
        {
            _openedAt = openedAtMs;
            _lastRead = openedAtMs;
            _note = note;
        }

        /// <summary>Input codes ignored while waiting for the knob to go quiet.</summary>
        public int ReplayEventsDiscarded => _ignored;

        /// <summary>True until a buffer check has found the knob quiet.</summary>
        public bool WaitingForQuiet => _waiting;

        /// <summary>
        /// Take one decoded token read from the port at <paramref name="atMs"/>. Returns true when
        /// it should be acted on, which is at once: there is no hold. While waiting, it restarts
        /// the wait and never ends it.
        /// </summary>
        public bool Accept(string token, double atMs)
        {
            if (!_waiting) return true;

            if (_reads.Count == 0 || atMs > _lastRead)
            {
                if (_reads.Count < GapsListed + 1) _reads.Add(atMs);
                else _readsNotListed++;
            }
            _lastRead = Math.Max(_lastRead, atMs);

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
        /// The caller checked the port's buffer at <paramref name="atMs"/>, between reads, and found
        /// <paramref name="unreadBytes"/> bytes waiting. The only thing that ends the wait: an empty
        /// buffer a full <see cref="QuietGapMs"/> after the last read.
        /// </summary>
        public void CheckQuiet(double atMs, int unreadBytes)
        {
            if (!_waiting) return;
            if (unreadBytes > 0)
            {
                _checksWithBytesWaiting++;
                return;
            }
            if (atMs - _lastRead < QuietGapMs) return;

            _waiting = false;
            Report("knob ready at " + Ms(atMs - _openedAt) + " ms after the open call: no text had been read from the port for "
                + Ms(atMs - _lastRead) + " ms, and the port's buffer was empty when checked");
        }

        /// <summary>
        /// The port is closing; say what the wait ignored if it never ended. Nothing is held, so
        /// there is nothing to release.
        /// </summary>
        public void Close(double atMs)
        {
            if (!_waiting) return;
            Report("the port closed at " + Ms(atMs - _openedAt) + " ms after the open call before a buffer check had found the knob quiet");
        }

        private void Report(string opening)
        {
            if (_note == null) return;
            var line = new StringBuilder(opening);
            line.Append(". While waiting it ignored ").Append(_ignored).Append(" input codes");
            if (_ignored > 0)
                line.Append(" (").Append(string.Join(", ", _ignoredOrder.Select(t => t + " " + _ignoredCounts[t]))).Append(')');
            line.Append(" and passed ").Append(_reportsPassed).Append(" device reports, and ")
                .Append(_checksWithBytesWaiting).Append(" buffer checks found bytes waiting unread.");
            if (_reads.Count == 0)
            {
                line.Append(" Nothing was read while waiting.");
            }
            else
            {
                line.Append(" Reads: the first at ").Append(Ms(_reads[0] - _openedAt)).Append(" ms after the open call");
                if (_reads.Count > 1)
                {
                    line.Append(", then gaps of ");
                    line.Append(string.Join(", ", Enumerable.Range(1, _reads.Count - 1).Select(i => Ms(_reads[i] - _reads[i - 1]))));
                    line.Append(" ms");
                }
                if (_readsNotListed > 0) line.Append(", and ").Append(_readsNotListed).Append(" more reads not listed");
                line.Append('.');
            }
            line.Append(" These are the times the text was read from the port, one read often carrying many codes, not the times the bytes left the knob.");
            _note(line.ToString());
        }

        private static string Ms(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
