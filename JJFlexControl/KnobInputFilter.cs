using System;
using System.Collections.Generic;
using System.Globalization;

namespace JJFlexControl
{
    /// <summary>
    /// Stands between the Flex Control's decoded tokens and anything that acts on them, and
    /// ignores what the knob replays when the port opens (#520). Pure: no port, no clock, no
    /// thread. One filter per open; the caller passes the open time and each token's arrival time.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule, ruled by Noel 2026-10-20 ("I'd do A"):</b> discard input codes during a
    /// short opening window after the port opens, and after that act on every input code at once,
    /// with no hold and no grouping. The device saves what was done to it while the port was
    /// closed and hands it all over on open; once connected, nothing real is ever dropped. This
    /// replaced a "discard any burst faster than a hand" rule, which dropped a real turn whenever
    /// one serial read carried three detents: every token in a read shares one arrival time, so a
    /// read describes the read, not the events inside it.</para>
    /// <para><b>A device report is never discarded, inside the window or out.</b> <c>F0304</c>
    /// arrives in exactly that opening window and is probably the identity report. Any token that
    /// is not one of the fourteen input codes passes through at once.</para>
    /// <para><b>The window is a FIXED length from the port opening, <see cref="OpeningWindowMs"/>,
    /// about 331 ms</b> (round three, ruled by Noel 2026-10-20). It does not end early on any gap
    /// or any code. Its length is 25 ms (every opening sequence the bench saw, the <c>F0304</c>
    /// reports and any replay, arrived "in the first 25 ms") plus the time the largest turn the
    /// bench recorded, 147 detents, takes to drain at 9600 baud (147 codes of two bytes, ten bits a
    /// byte). An earlier version ended the window at the first quiet gap after 25 ms; that let a
    /// replay delivered in two serial callbacks, or an F0304 between them, close the window early
    /// and send the replay's tail to the radio, because a whole read is stamped with one time.</para>
    /// <para>The 147 is a recorded turn, not the device's measured capacity, so a saved replay
    /// longer than that would still reach the radio after the window; ASK-520-opening-window has
    /// the bench reading that would measure the capacity, and the drain time, and could shorten
    /// the window.</para>
    /// <para><b>What it costs:</b> a real turn made in the first moment after the port opens is
    /// dropped with the replay. That is the trade Noel accepted.</para>
    /// <para><b>Not built here:</b> the press-nudge ruling. It waits on a bench measurement of the
    /// gap between a phantom step and its press code.</para>
    /// </remarks>
    public sealed class KnobInputFilter
    {
        /// <summary>Every measured opening sequence arrived within the first 25 ms (#520).</summary>
        public const double MeasuredOpeningMs = 25.0;

        /// <summary>The largest turn the bench recorded: 147 detents in one uncontrolled twirl (#520).</summary>
        public const int LargestRecordedTurn = 147;

        /// <summary>
        /// The whole window, fixed from the port opening: the measured 25 ms plus the time
        /// <see cref="LargestRecordedTurn"/> rotation codes ("D;", two bytes, ten bits a byte) take
        /// to drain at 9600 baud. About 331 ms.
        /// </summary>
        public const double OpeningWindowMs = MeasuredOpeningMs + LargestRecordedTurn * 2 * 10 * 1000.0 / 9600.0;

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
        private double _lastDroppedAt;
        private bool _windowOpen = true;
        private readonly List<string> _dropped = new List<string>();

        /// <param name="openedAtMs">When the port opened, on the same clock as the arrivals.</param>
        /// <param name="note">Told, in a sentence, what the opening window discarded. May be null.</param>
        public KnobInputFilter(double openedAtMs, Action<string> note = null)
        {
            _openedAt = openedAtMs;
            _lastDroppedAt = openedAtMs;
            _note = note;
        }

        /// <summary>Input codes discarded by the opening window.</summary>
        public int ReplayEventsDiscarded => _dropped.Count;

        /// <summary>True until the opening window has ended.</summary>
        public bool InOpeningWindow => _windowOpen;

        /// <summary>
        /// Take one decoded token that arrived at <paramref name="atMs"/>. Returns true when it
        /// should be acted on, which is at once: there is no hold.
        /// </summary>
        public bool Accept(string token, double atMs)
        {
            if (_windowOpen && WindowEndsBefore(atMs)) CloseWindow(atMs);

            bool keep = !_windowOpen || IsDeviceReport(token);
            if (!keep)
            {
                _dropped.Add(token);
                _lastDroppedAt = Math.Max(_lastDroppedAt, atMs);
            }
            return keep;
        }

        /// <summary>
        /// The port is closing; say what the window dropped if it never got to say so.
        /// Nothing is held, so there is nothing to release.
        /// </summary>
        public void Close(double atMs)
        {
            if (_windowOpen) CloseWindow(atMs);
        }

        // Fixed length: no gap and no code ends it early (round three).
        private bool WindowEndsBefore(double atMs) => atMs - _openedAt >= OpeningWindowMs;

        private void CloseWindow(double atMs)
        {
            _windowOpen = false;
            if (_dropped.Count == 0) return;
            _note?.Invoke("opening window ignored " + _dropped.Count + " input codes in the first " + OpeningWindowMs.ToString("0", CultureInfo.InvariantCulture) + " ms after the port opened, the last at "
                + (_lastDroppedAt - _openedAt).ToString("0.0", CultureInfo.InvariantCulture) + " ms after open: "
                + string.Join(",", _dropped));
        }
    }
}
