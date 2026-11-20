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
    /// For a helper engine (NNR, NR2) it is late when the helper's own
    /// processing time for one frame, added over every channel its one worker
    /// serves, is longer than the frame's audio (<see cref="HelperServiceTime"/>;
    /// repair 1, after Codex's check). For a helper engine the pipeline calls
    /// this once per frame of NEW work, each answer counted once, and not at
    /// all on a buffer with no new answers (<see cref="HelperServiceMeter"/>;
    /// repair 2), so its "buffers" are frames of the helper's work. A helper frame that merely came back too
    /// late is a delivery fact, not an engine's cost: the pipeline counts those
    /// with a second rule of this class, traced as a diagnostic and never
    /// warned about.
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
        // - Helper engines (NNR, NR2), idle: 0 late answers in four of six
        //   runs, but a stretch of 38 to 100 late answers a second in the other
        //   two, on an idle machine, while the helper's own processing time was
        //   about 1.7 ms a frame. So late answers are NOT this rule's signal for
        //   the helper (repair 1): its processing time is, and late answers are
        //   traced as a separate delivery diagnostic.
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

        /// <summary>
        /// Buffers still to be discarded as grace before the rule judges anything
        /// (#1229 repair 1: the running tally counts only what the rule judged,
        /// so it reads this before each <see cref="Observe(bool)"/>).
        /// </summary>
        public int GraceLeft => _graceLeft;

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
        /// same engine. The pipeline's delivery diagnostic uses this form: a
        /// warm-up that never ends means the helper's answers never arrived in
        /// time, whatever the cause.
        /// </summary>
        public EngineKeepUpChange Warming(string engineId) => Warming(engineId, true);

        /// <summary>
        /// One buffer of a helper engine's warm-up, counted only when
        /// <paramref name="late"/>: the helper's processing time was over the
        /// audio's length during it. Falls behind once, after
        /// <see cref="StuckWarmup"/> such buffers in one warm-up of the same
        /// engine. This is the warning's form (repair 1): a helper too slow to
        /// keep up never finishes its warm-up, because its answers are always
        /// late, so without it the running-engine window would never see it;
        /// and a fast helper whose answers are delayed on the way back is not
        /// counted at all.
        /// </summary>
        public EngineKeepUpChange Warming(string engineId, bool late)
        {
            engineId ??= "";
            if (!string.Equals(engineId, EngineId, StringComparison.Ordinal)) Restart(engineId);
            if (Behind) return EngineKeepUpChange.None;
            if (!late || ++_warming < StuckWarmup) return EngineKeepUpChange.None;
            Behind = true;
            _knownGood = false;
            return EngineKeepUpChange.FellBehind;
        }

        /// <summary>
        /// <paramref name="count"/> warm-up buffers of a helper engine, all with
        /// the same lateness: exactly what <see cref="Warming(string, bool)"/>
        /// called that many times would do, in constant time (repair 4). The
        /// engine is restarted when it changes even when the count is zero.
        /// Returns the change the last of them made, if any.
        /// </summary>
        public EngineKeepUpChange Warming(string engineId, bool late, int count)
        {
            engineId ??= "";
            if (!string.Equals(engineId, EngineId, StringComparison.Ordinal)) Restart(engineId);
            if (Behind || !late || count <= 0) return EngineKeepUpChange.None;
            if ((long)_warming + count < StuckWarmup) { _warming += count; return EngineKeepUpChange.None; }
            _warming = StuckWarmup;
            Behind = true;
            _knownGood = false;
            return EngineKeepUpChange.FellBehind;
        }

        /// <summary>
        /// <paramref name="count"/> processed buffers, all with the same
        /// lateness: exactly what <see cref="Observe(bool)"/> called that many
        /// times would do, and bounded in time however large the count (repair
        /// 4, after Codex's third re-check). A helper engine's work arrives in
        /// batches of frames that share one cost, and a batch can be hundreds of
        /// frames after a channel leaves the round; repair 3's pipeline judged
        /// at most a window of them and discarded the rest, so an engine that
        /// had recovered could stay behind.
        /// </summary>
        /// <remarks>
        /// After the grace still to run, one window and the recovery hold (plus
        /// one) of identical buffers, the rule has settled: the window holds
        /// nothing but this lateness, the fall or the recovery has happened, and
        /// a further identical buffer changes nothing but where the ring
        /// writes next. So it observes at most that many (551 with the
        /// defaults) and moves the ring's position for the rest, which on a
        /// uniform window cannot matter but keeps the state exactly what the
        /// one-at-a-time calls leave. Returns the last change any of them made.
        /// No allocation.
        /// </remarks>
        public EngineKeepUpChange Observe(bool late, int count)
        {
            if (count <= 0) return EngineKeepUpChange.None;
            long settle = (long)_graceLeft + Window + RecoverHold + 1;
            int run = count < settle ? count : (int)settle;
            var change = EngineKeepUpChange.None;
            for (int i = 0; i < run; i++)
            {
                var c = Observe(late);
                if (c != EngineKeepUpChange.None) change = c;
            }
            if (count > run) _at = (int)((_at + (long)(count - run)) % Window);
            return change;
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
    /// <see cref="RxAudioPipeline.KeepUpResult"/> off the audio thread; or,
    /// with <see cref="Delivery"/> set, a change in a helper engine's delivery,
    /// which is traced and raised only on the pipeline's internal diagnostic
    /// event, never on the warning's.
    /// </summary>
    /// <param name="Helper">The engine runs in the helper, so its cost is the helper's own processing time for every channel it serves.</param>
    /// <param name="LateBuffers">Late buffers in the rule's window when the change was made (for a delivery change, buffers with a frame missing).</param>
    /// <param name="WindowBuffers">Buffers in the window then.</param>
    /// <param name="AverageMs">Average processing time per buffer, in milliseconds: the slot's, or for a helper engine the helper's for every channel it serves.</param>
    /// <param name="BufferMs">How much audio one buffer carried, in milliseconds (for a helper engine, one helper frame).</param>
    /// <param name="MissedBuffers">For a helper engine, buffers in the delivery window with a frame that did not come back in time: a diagnostic only.</param>
    /// <param name="Delivery">A delivery diagnostic rather than a keep-up change.</param>
    public sealed record EngineKeepUpResult(DenoiseFamily Family, string EngineId, EngineKeepUpChange Change, bool Helper,
        int LateBuffers, int WindowBuffers, double AverageMs, double BufferMs, int MissedBuffers = 0, bool Delivery = false)
    {
        /// <summary>One line for the diagnostic log.</summary>
        public string Describe() => Delivery
            ? FormattableString.Invariant($"{Family} slot, {EngineId}: delivery {Change}; {LateBuffers} of {WindowBuffers} buffers had a helper frame missing at its deadline (a diagnostic only: the warning follows the helper's processing time, average {AverageMs:F2} ms of work per {BufferMs:F1} ms of audio)")
            : FormattableString.Invariant($"{Family} slot, {EngineId}: {Change}; {LateBuffers} late of {WindowBuffers} buffers")
              + (Helper ? FormattableString.Invariant($" (the helper's processing time for every channel it serves, average {AverageMs:F2} ms per {BufferMs:F1} ms of audio; {MissedBuffers} buffers with a frame missing at its deadline)")
                  : FormattableString.Invariant($", average {AverageMs:F2} ms per {BufferMs:F1} ms buffer"));
    }
}
