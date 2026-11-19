using System;

namespace JJFlexWpf
{
    /// <summary>What one buffer told <see cref="EngineKeepUpRule"/>.</summary>
    public enum EngineKeepUpChange
    {
        /// <summary>Nothing the operator needs to hear about.</summary>
        None,
        /// <summary>The engine has kept up for a whole window since it started (or since a gap): the verdict for this run is "fine".</summary>
        KeepingUp,
        /// <summary>The engine has been late too often, for long enough: it cannot keep up on this computer right now.</summary>
        FellBehind,
        /// <summary>After falling behind, the engine has kept up for a whole window again.</summary>
        Recovered,
    }

    /// <summary>
    /// Whether one receive slot's engine is keeping up with the audio, decided
    /// from facts about each buffer (Sprint 49 Track L, #866; Noel 2026-11-19
    /// 07:03, X2: the warning fires when an engine falls behind, that is, takes
    /// longer to process audio than the audio lasts). Pure and windowless: it
    /// is told what happened and answers; the pipeline times the slots, and the
    /// noise controller decides what, if anything, to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What "late" means is the caller's.</b> For an engine in this process
    /// a buffer is late when its slot took longer than the audio it carried.
    /// For a helper engine (NNR, NR2) it is late when the running bank counted
    /// a late frame: the helper's frames come back through a pipe, so the time
    /// the call takes says nothing, and the bank's own count is the fact.
    /// </para>
    /// <para>
    /// <b>Sustained, not momentary.</b> A buffer now and then is late on any
    /// computer (the scheduler, a garbage collection, a burst after a gap); the
    /// playback queue absorbs it. The rule keeps a sliding window of the last
    /// <see cref="Window"/> buffers and falls behind only when at least
    /// <see cref="FallAt"/> of them were late. It comes back only when the
    /// window has held at most <see cref="RecoverAt"/> late buffers for a
    /// further <see cref="RecoverHold"/> buffers, so an engine on the edge does
    /// not flap between the two.
    /// </para>
    /// <para>
    /// <b>Start-up is not slowness.</b> After <see cref="Restart"/> (a new
    /// engine, a new bank, the end of a warm-up or switch) or <see cref="Gap"/>
    /// (a buffer the slot did not process, or a pause in the stream) the next
    /// <see cref="Grace"/> buffers are not counted at all. The figures behind
    /// every default are on <see cref="DefaultWindow"/>.
    /// </para>
    /// <para>
    /// <b>No allocation, no lock, no logging</b>: it runs on the receive audio
    /// thread, once per buffer per slot. Every array is made in the constructor.
    /// </para>
    /// </remarks>
    public sealed class EngineKeepUpRule
    {
        // ── The defaults, and how they were chosen ──
        //
        // Measured 2026-11-19 on the operator's machine (an Intel Core Ultra 9
        // 285HX, 24 logical processors, mains power), every receive engine run
        // through the real RxAudioPipeline at the receive loop's pace, one 10 ms
        // stereo buffer every 10 ms, spun rather than slept so the pace is exact
        // (EngineKeepUpMeasurementTests prints every figure; the Track L report
        // has them in words). "Loaded" is the audio thread sharing ONE
        // processor with three busy threads. The short of it:
        //
        // - Engines in this process, idle: at most 1 late buffer in any second
        //   once running (RNNoise, DeepFilterNet3, NR4, Captured noise; worst
        //   single buffer 12 ms). Loaded: 0 to 4 in most runs, and 23 in the
        //   one run where DeepFilterNet3 really fell behind (mean 3.8 ms, worst
        //   92 ms); that run fell, came back for under four seconds, and fell
        //   again.
        // - Helper engines (NNR, NR2), idle: 0 in four of six runs, but a
        //   stretch of 38 to 100 late buffers a second in the other two, on an
        //   idle machine. See the report: the counts alone cannot tell that from
        //   a loaded helper (48 to 74), so for the helper this rule warns
        //   whenever its audio really goes out unprocessed, which is the
        //   failure the warning exists for, whatever its cause.
        // - After the helper is killed and restarts, its pipeline refills with
        //   11 late buffers in a row, then none.

        /// <summary>The sliding window, in buffers: 100 buffers is one second of the radio's 10 ms packets.</summary>
        public const int DefaultWindow = 100;

        /// <summary>
        /// Late buffers in one window that mean "cannot keep up": a fifth of
        /// every second. Five times the worst a loaded machine produced for an
        /// engine that was keeping up (4), and twenty times the idle figure (1),
        /// yet reached within a second by an engine that is late in nearly
        /// every buffer, and by the one measured real fall (23).
        /// </summary>
        public const int DefaultFallAt = 20;

        /// <summary>At most this many late buffers in a whole window counts as keeping up: the idle figure with one to spare.</summary>
        public const int DefaultRecoverAt = 2;

        /// <summary>
        /// After a fall, how many more buffers the window must stay at or under
        /// <see cref="RecoverAt"/> before the engine counts as recovered: four
        /// seconds, so five seconds clean in all. The measured real fall came
        /// back for under four seconds before falling again; a shorter hold
        /// would have called that a recovery, and a warning that may be said
        /// again after a recovery would have been said twice in nine seconds.
        /// </summary>
        public const int DefaultRecoverHold = 400;

        /// <summary>
        /// Buffers not counted after a start, a switch or a gap: half a second.
        /// The longest start-up lateness measured on a running bank was the
        /// helper's pipeline refilling after a restart, 11 buffers in a row;
        /// this is more than four times that.
        /// </summary>
        public const int DefaultGrace = 50;

        /// <summary>
        /// Buffers a helper engine may spend warming up before the rule says it
        /// cannot keep up: three seconds. A helper engine that is too slow never
        /// finishes its warm-up (the slot hands over only after a tenth of a
        /// second with no late frame), so it never becomes the running engine
        /// and the window above never sees it. A delivered helper bank measured
        /// about a tenth of a second of warm-up once its channel was open; this
        /// is thirty times that.
        /// </summary>
        public const int DefaultStuckWarmup = 300;

        public int Window { get; }
        public int FallAt { get; }
        public int RecoverAt { get; }
        public int Grace { get; }
        public int StuckWarmup { get; }
        public int RecoverHold { get; }
        private int _warming, _cleanRun;

        private readonly bool[] _late;
        private int _at, _filled, _lateCount, _graceLeft;
        private bool _knownGood;

        /// <summary>The engine whose buffers are being counted, or "" before the first.</summary>
        public string EngineId { get; private set; } = "";

        /// <summary>True from a <see cref="EngineKeepUpChange.FellBehind"/> until a <see cref="EngineKeepUpChange.Recovered"/>, a restart or a gap.</summary>
        public bool Behind { get; private set; }

        /// <summary>Late buffers in the window now.</summary>
        public int LateInWindow => _lateCount;

        /// <summary>Buffers in the window now, at most <see cref="Window"/>.</summary>
        public int Filled => _filled;

        public EngineKeepUpRule(int window = DefaultWindow, int fallAt = DefaultFallAt, int recoverAt = DefaultRecoverAt, int grace = DefaultGrace,
            int stuckWarmup = DefaultStuckWarmup, int recoverHold = DefaultRecoverHold)
        {
            if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
            if (fallAt < 1 || fallAt > window) throw new ArgumentOutOfRangeException(nameof(fallAt));
            if (recoverAt < 0 || recoverAt >= fallAt) throw new ArgumentOutOfRangeException(nameof(recoverAt));
            if (grace < 0) throw new ArgumentOutOfRangeException(nameof(grace));
            if (stuckWarmup < 1) throw new ArgumentOutOfRangeException(nameof(stuckWarmup));
            if (recoverHold < 0) throw new ArgumentOutOfRangeException(nameof(recoverHold));
            Window = window; FallAt = fallAt; RecoverAt = recoverAt; Grace = grace; StuckWarmup = stuckWarmup; RecoverHold = recoverHold;
            _late = new bool[window];
            _graceLeft = grace;
        }

        /// <summary>
        /// An engine starts being counted afresh: a new engine or bank in the
        /// slot, or the end of a warm-up or switch. Forgets the window and
        /// begins the grace. Says nothing: an engine that was behind and was
        /// switched away has not been seen to recover.
        /// </summary>
        public void Restart(string engineId)
        {
            EngineId = engineId ?? "";
            Forget();
        }

        /// <summary>A buffer the slot did not process, or a pause in the stream: what follows is start-up again.</summary>
        public void Gap() => Forget();

        private void Forget()
        {
            Array.Clear(_late);
            _at = 0; _filled = 0; _lateCount = 0;
            _graceLeft = Grace;
            _knownGood = false;
            _warming = 0; _cleanRun = 0;
            Behind = false;
        }

        /// <summary>
        /// One buffer during which a helper engine was warming up to replace
        /// whatever the slot runs (or nothing). Counted only for a helper
        /// engine, and only while the slot is processing: an engine in this
        /// process always produces, so its warm-up always ends. Falls behind
        /// once, after <see cref="StuckWarmup"/> such buffers in a row for the
        /// same engine.
        /// </summary>
        public EngineKeepUpChange Warming(string engineId)
        {
            engineId ??= "";
            if (!string.Equals(engineId, EngineId, StringComparison.Ordinal)) Restart(engineId);
            if (Behind) return EngineKeepUpChange.None;
            if (++_warming < StuckWarmup) return EngineKeepUpChange.None;
            Behind = true;
            _knownGood = false;
            return EngineKeepUpChange.FellBehind;
        }

        /// <summary>
        /// One processed buffer of the engine being counted.
        /// </summary>
        /// <param name="late">Whether this buffer was late, as the caller defines it (see the remarks).</param>
        public EngineKeepUpChange Observe(bool late)
        {
            if (_graceLeft > 0) { _graceLeft--; return EngineKeepUpChange.None; }

            if (_filled == Window) { if (_late[_at]) _lateCount--; }
            else _filled++;
            _late[_at] = late;
            if (late) _lateCount++;
            _at = (_at + 1) % Window;

            if (!Behind && _lateCount >= FallAt)
            {
                Behind = true;
                _knownGood = false;
                _cleanRun = 0;
                return EngineKeepUpChange.FellBehind;
            }
            bool clean = _filled == Window && _lateCount <= RecoverAt;
            if (Behind)
            {
                // Back only after the window has stayed clean for the hold as well.
                _cleanRun = clean ? _cleanRun + 1 : 0;
                if (_cleanRun > RecoverHold) { Behind = false; _knownGood = true; _cleanRun = 0; return EngineKeepUpChange.Recovered; }
                return EngineKeepUpChange.None;
            }
            if (clean && !_knownGood) { _knownGood = true; return EngineKeepUpChange.KeepingUp; }
            return EngineKeepUpChange.None;
        }
    }
}

namespace JJFlexWpf
{
    /// <summary>
    /// A keep-up change for one receive slot's engine, raised by
    /// <see cref="RxAudioPipeline.KeepUpResult"/> off the audio thread.
    /// </summary>
    /// <param name="Helper">The engine runs in the helper, so lateness was counted by its bank rather than timed.</param>
    /// <param name="LateBuffers">Late buffers in the rule's window when the change was made.</param>
    /// <param name="WindowBuffers">Buffers in the window then.</param>
    /// <param name="AverageMs">The slot's average processing time per buffer, in milliseconds (0 while a helper engine warms up).</param>
    /// <param name="BufferMs">How much audio one buffer carried, in milliseconds.</param>
    public sealed record EngineKeepUpResult(DenoiseFamily Family, string EngineId, EngineKeepUpChange Change, bool Helper,
        int LateBuffers, int WindowBuffers, double AverageMs, double BufferMs)
    {
        /// <summary>One line for the diagnostic log.</summary>
        public string Describe() =>
            FormattableString.Invariant($"{Family} slot, {EngineId}: {Change}; {LateBuffers} late of {WindowBuffers} buffers")
            + (Helper ? " (late frames counted by the helper's bank)"
                : FormattableString.Invariant($", average {AverageMs:F2} ms per {BufferMs:F1} ms buffer"));
    }
}
