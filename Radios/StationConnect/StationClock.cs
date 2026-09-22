using System;

namespace Radios.StationConnect
{
    /// <summary>
    /// Monotonic elapsed time for every station-connect deadline. Never a
    /// wall clock: a deadline is "this many milliseconds from now", compared
    /// against a counter that cannot jump when the operating system adjusts
    /// the date. Tests substitute a hand-advanced clock so a twenty-second
    /// wait costs nothing and expires exactly when the test says.
    /// </summary>
    public interface IStationClock
    {
        /// <summary>Milliseconds on a monotonic counter.</summary>
        long NowMs { get; }
    }

    /// <summary>The production clock: <see cref="Environment.TickCount64"/>.</summary>
    public sealed class MonotonicStationClock : IStationClock
    {
        public static readonly MonotonicStationClock Instance = new MonotonicStationClock();
        public long NowMs => Environment.TickCount64;
    }

    /// <summary>
    /// A deadline on the monotonic clock. Every inner wait is clipped to the
    /// remainder of its phase, so no path can outlive the phase it belongs to.
    /// </summary>
    public readonly struct StationDeadline
    {
        public readonly long AtMs;
        public StationDeadline(long atMs) { AtMs = atMs; }

        public static StationDeadline In(IStationClock clock, int ms) =>
            new StationDeadline(clock.NowMs + Math.Max(0, ms));

        public bool Passed(IStationClock clock) => clock.NowMs >= AtMs;

        public int RemainingMs(IStationClock clock) =>
            (int)Math.Max(0, Math.Min(int.MaxValue, AtMs - clock.NowMs));

        /// <summary>A deadline at most <paramref name="ms"/> away, and never
        /// later than this one: the inner wait clipped to the phase.</summary>
        public StationDeadline Clip(IStationClock clock, int ms) =>
            new StationDeadline(Math.Min(AtMs, clock.NowMs + Math.Max(0, ms)));
    }

    /// <summary>
    /// The ceilings from the design (section 5), in one place so nobody
    /// invents one. These are responsiveness ceilings, not measured
    /// completion times: a wait ending at the ceiling is an UNCONFIRMED
    /// result, never a success.
    /// </summary>
    public sealed class StationDeadlines
    {
        /// <summary>One bounded profile read (inventory and selection).</summary>
        public int ProfileReadMs = 2500;

        /// <summary>Autosave confirmation, and live transmit-chain capture.</summary>
        public int AutosaveAndCaptureMs = 3000;

        /// <summary>Confirmation that a transmit or microphone action took effect.</summary>
        public int TxMicEffectMs = 3000;

        /// <summary>The whole station phase: roster, global decision, load, allocation.</summary>
        public int StationPhaseMs = 20000;

        /// <summary>The whole post-station phase: transmit, microphone, live audio, assessment.</summary>
        public int PostStationPhaseMs = 20000;

        /// <summary>One fresh-resource request, inside the station phase.</summary>
        public int AllocationRequestMs = 2000;

        /// <summary>Readback after the deferred disconnect-time create.</summary>
        public int DisconnectCreateConfirmMs = 3000;

        /// <summary>How long to wait for the roster to become knowable before
        /// the automatic load is refused as unknown. Inside the station phase.</summary>
        public int RosterSettleMs = 5000;

        public static StationDeadlines Default() => new StationDeadlines();
    }
}
