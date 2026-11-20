using System;
using System.Globalization;
using System.Threading;

namespace JJFlexWpf
{
    /// <summary>Why a <see cref="SlotTallyReport"/> was written.</summary>
    public enum SlotTallyReason
    {
        /// <summary>A minute of the engine running has passed.</summary>
        Minute,
        /// <summary>The slot has processed nothing for <see cref="SlotTally.StopDebounceSeconds"/>: it was turned off, or its engine stopped processing.</summary>
        Stopped,
        /// <summary>The slot began processing with a different engine.</summary>
        Changed,
        /// <summary>PC audio went off: the receive loop ended.</summary>
        FeedEnded,
        /// <summary>The slot's total for the whole PC-audio session, written when PC audio went off.</summary>
        Session,
    }

    /// <summary>
    /// One period of a receive slot's running tally (#1229): what its engine
    /// did from the period's start to its end. A diagnostic line for the
    /// trace, never spoken.
    /// </summary>
    public readonly struct SlotTallyReport
    {
        public readonly DenoiseFamily Family;
        /// <summary>The engine counted; for a session total, the last engine the slot ran ("" if none).</summary>
        public readonly string EngineId;
        public readonly bool Helper;
        public readonly SlotTallyReason Reason;
        /// <summary>Receive buffers the slot processed with this engine.</summary>
        public readonly long Buffers;
        /// <summary>
        /// What the warning's rule actually judged: buffers for an engine in this
        /// process, helper frames for a helper engine. Not the rule's grace after
        /// a start or a gap, which it discards (repair 1), and not a helper's
        /// warm-up, during which the period's engine is the one being replaced.
        /// </summary>
        public readonly long Judged;
        /// <summary>Of <see cref="Judged"/>, late by the warning's rule.</summary>
        public readonly long Late;
        /// <summary>Buffers in which a helper frame missed its deadline (helper engines only).</summary>
        public readonly long Missed;
        /// <summary>How many delivery dips (the diagnostic's FellBehind) began in the period.</summary>
        public readonly long DipsBegan;
        /// <summary>Average processing time per 10 ms of audio, in milliseconds; negative when nothing was timed.</summary>
        public readonly double AverageMsPer10;
        /// <summary>
        /// The worst whole second's processing time per 10 ms of audio, in
        /// milliseconds; negative when the period (or session) held no whole
        /// second of audio. A part-second never enters it (repair 1).
        /// </summary>
        public readonly double WorstSecondMsPer10;
        /// <summary>Whole seconds of audio, counted from the period's start; seconds never span periods.</summary>
        public readonly long Seconds;
        /// <summary>
        /// The period's trailing part-second, per 10 ms of audio, kept apart from
        /// the whole seconds; negative when there is none, and always negative on
        /// a session total, which adds up whole seconds only.
        /// </summary>
        public readonly double PartSecondMsPer10;
        /// <summary>The period's length on the clock, in seconds.</summary>
        public readonly double PeriodSeconds;
        /// <summary>For a session total: how many periods it adds up.</summary>
        public readonly long Periods;
        /// <summary>Reports that could not be queued before this one because the reader had fallen behind.</summary>
        public readonly long Dropped;

        public SlotTallyReport(DenoiseFamily family, string engineId, bool helper, SlotTallyReason reason,
            long buffers, long judged, long late, long missed, long dipsBegan,
            double averageMsPer10, double worstSecondMsPer10, long seconds, double periodSeconds, long periods, long dropped,
            double partSecondMsPer10 = -1)
        {
            PartSecondMsPer10 = partSecondMsPer10;
            Family = family; EngineId = engineId ?? ""; Helper = helper; Reason = reason;
            Buffers = buffers; Judged = judged; Late = late; Missed = missed; DipsBegan = dipsBegan;
            AverageMsPer10 = averageMsPer10; WorstSecondMsPer10 = worstSecondMsPer10; Seconds = seconds;
            PeriodSeconds = periodSeconds; Periods = periods; Dropped = dropped;
        }

        /// <summary>One line for the diagnostic log.</summary>
        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            string head = Reason switch
            {
                SlotTallyReason.Minute => " for the last minute",
                SlotTallyReason.Stopped => " until the slot stopped processing",
                SlotTallyReason.Changed => " until the slot changed engine",
                SlotTallyReason.FeedEnded => " until PC audio went off",
                _ => "",
            };
            if (Reason == SlotTallyReason.Session)
            {
                if (Periods == 0) return Family + " slot, session total at PC audio off: ran no engine";
                head = string.Format(c, " session total at PC audio off, {0} period(s), the last engine {1}", Periods, EngineId);
            }
            else head = string.Format(c, " {0}{1}{2}", EngineId, Helper ? " (helper)" : "", head);
            string unit = Reason == SlotTallyReason.Session ? "judged (buffers in this process, helper frames for a helper engine)"
                : Helper ? "helper frames" : "buffers";
            string s = string.Format(c, "{0} slot,{1} ({2:F1} s): {3} buffers processed; {4} late of {5} {6} judged by the warning's rule (its grace after a start or gap, and warm-ups, not judged)",
                Family, head, PeriodSeconds, Buffers, Late, Judged, unit);
            if (Helper || Reason == SlotTallyReason.Session)
                s += string.Format(c, "; {0} buffers with a helper frame missed at its deadline; {1} delivery dip(s) began", Missed, DipsBegan);
            if (AverageMsPer10 < 0) s += "; no processing time measured";
            else
            {
                s += string.Format(c, "; processing {0:F2} ms per 10 ms of audio on average", AverageMsPer10);
                if (Seconds > 0)
                    s += string.Format(c, ", the worst whole second {0:F2} ms (of {1} whole second(s) of audio)", WorstSecondMsPer10, Seconds);
                else if (PartSecondMsPer10 >= 0)
                    s += string.Format(c, " (less than a second of audio, so no worst second; {0:F2} ms over that part-second)", PartSecondMsPer10);
                else s += " (no whole second of audio)";
            }
            if (Dropped > 0) s += string.Format(c, "; {0} earlier tally line(s) were lost because the reader fell behind", Dropped);
            return s;
        }
    }

    /// <summary>
    /// A receive slot's running tally (#1229): one line a minute while the slot
    /// runs an engine, one when it stops or changes engine or PC audio goes
    /// off, and a session total at PC audio off. Fed by the audio thread with
    /// facts it already has (the keep-up rule's inputs) and the time it already
    /// read; it reads no clock, starts no timer, and allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> The 2026-11-19 night bench asked whether Premium CW keeps
    /// up in stereo, and the answer had to be inferred: the keep-up and
    /// delivery lines are written only on a change, so a 30-minute run gave a
    /// handful of change lines and nothing to count. With this, a run reads
    /// as numbers per minute.
    /// </para>
    /// <para>
    /// <b>Threads.</b> One writer, the receive audio thread; reports go into a
    /// small ring made in the constructor, and a reader on another thread
    /// takes them (<see cref="TryTake"/>, readers serialised by a lock). If the
    /// reader falls a whole ring behind, a report is dropped and counted, never
    /// overwritten mid-read.
    /// </para>
    /// </remarks>
    public sealed class SlotTally
    {
        /// <summary>A period's length: one minute.</summary>
        public const double PeriodSeconds = 60;

        /// <summary>
        /// How long the slot must process nothing before its period is closed as
        /// stopped: one second, so a slot that pauses for a buffer or two (a
        /// short buffer, a pass-through) does not write a pair of lines each time.
        /// </summary>
        public const double StopDebounceSeconds = 1;

        private const int Capacity = 16;

        private readonly DenoiseFamily _family;
        private readonly long _periodTicks, _debounceTicks, _ticksPerSecond;
        private readonly SlotTallyReport[] _ring = new SlotTallyReport[Capacity];
        private long _head, _tail, _dropped;
        private readonly object _readLock = new();

        private Acc _period, _session;
        private bool _open;
        private string _engine = "";
        private bool _helper;
        private long _periodStart, _idleSince;
        private long _sessionPeriods;
        private double _sessionSeconds;

        /// <param name="family">The slot.</param>
        /// <param name="ticksPerSecond">The clock's ticks per second (the stopwatch's, in the app).</param>
        /// <param name="periodTicks">A period's length in ticks; one minute when not given (tests shorten it).</param>
        public SlotTally(DenoiseFamily family, long ticksPerSecond, long periodTicks = 0)
        {
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            _family = family;
            _ticksPerSecond = ticksPerSecond;
            _periodTicks = periodTicks > 0 ? periodTicks : (long)(PeriodSeconds * ticksPerSecond);
            _debounceTicks = (long)(StopDebounceSeconds * ticksPerSecond);
            _period.Reset(); _session.Reset();
        }

        /// <summary>The engine whose period is open, or "" when none is.</summary>
        public string EngineId => _open ? _engine : "";

        /// <summary>
        /// One buffer the slot processed with <paramref name="engineId"/>.
        /// </summary>
        /// <param name="now">The time the audio thread already read for this buffer.</param>
        /// <param name="judged">Buffers (in this process) or helper frames (a helper engine) told to the warning's rule; 0 when none was judged.</param>
        /// <param name="late">Of those, late by the warning's rule.</param>
        /// <param name="missed">A helper frame missed its deadline in this buffer.</param>
        /// <param name="workTicks">Processing time this buffer accounted for, in clock ticks.</param>
        /// <param name="audioTicks">The audio that processing covered, in clock ticks; 0 when nothing was timed.</param>
        /// <param name="dipBegan">A delivery dip began with this buffer: counted in the same update, so it lands in this buffer's period (repair 1).</param>
        /// <returns>True when a report was queued.</returns>
        public bool Buffer(string engineId, bool helper, long now, int judged, int late, bool missed, double workTicks, double audioTicks,
            bool dipBegan = false)
        {
            engineId ??= "";
            bool published = false;
            if (_open && !string.Equals(engineId, _engine, StringComparison.Ordinal))
                published = Close(SlotTallyReason.Changed, now);
            if (!_open) Open(engineId, helper, now);
            _helper = helper;
            _idleSince = 0;
            _period.Add(judged, late, missed, workTicks, audioTicks, _ticksPerSecond);
            if (dipBegan) _period.Dips++;
            return Tick(now) | published;
        }

        /// <summary>The slot processed nothing in this buffer.</summary>
        /// <returns>True when a report was queued.</returns>
        public bool Idle(long now)
        {
            if (!_open) return false;
            if (_idleSince == 0) { _idleSince = now == 0 ? 1 : now; return false; }
            if (now - _idleSince < _debounceTicks) return false;
            return Close(SlotTallyReason.Stopped, _idleSince);
        }

        /// <summary>A buffer in which the open period's engine was neither counted nor stopped (a switch or warm-up): only the minute is checked.</summary>
        /// <returns>True when a report was queued.</returns>
        public bool Tick(long now)
        {
            if (!_open || now - _periodStart < _periodTicks) return false;
            string engine = _engine; bool helper = _helper;
            bool published = Close(SlotTallyReason.Minute, now);
            Open(engine, helper, now);
            return published;
        }

        /// <summary>
        /// PC audio went off: close the open period, and queue the session's
        /// total (one line even when the slot ran nothing). The session starts
        /// again from nothing.
        /// </summary>
        public void FeedEnded(long now)
        {
            if (_open) Close(SlotTallyReason.FeedEnded, _idleSince != 0 ? _idleSince : now);
            Publish(_session.Report(_family, _engine, _session.HelperSeen, SlotTallyReason.Session, _sessionSeconds, _sessionPeriods, 0));
            _session.Reset();
            _sessionPeriods = 0;
            _sessionSeconds = 0;
        }

        /// <summary>The oldest report not yet taken. Any thread but the audio thread; readers are serialised.</summary>
        public bool TryTake(out SlotTallyReport report)
        {
            lock (_readLock)
            {
                long tail = _tail;
                if (tail == Volatile.Read(ref _head)) { report = default; return false; }
                report = _ring[tail % Capacity];
                Volatile.Write(ref _tail, tail + 1);
                return true;
            }
        }

        private void Open(string engineId, bool helper, long now)
        {
            _open = true;
            _engine = engineId;
            _helper = helper;
            _periodStart = now;
            _idleSince = 0;
            _period.Reset();
        }

        private bool Close(SlotTallyReason reason, long end)
        {
            _open = false;
            double seconds = Math.Max(0, end - _periodStart) / (double)_ticksPerSecond;
            var report = _period.Report(_family, _engine, _helper, reason, seconds, 1, 0);
            _session.Fold(ref _period, _helper);
            _sessionPeriods++;
            _sessionSeconds += seconds;
            _period.Reset();
            return Publish(report);
        }

        private bool Publish(in SlotTallyReport report)
        {
            long head = _head;
            if (head - Volatile.Read(ref _tail) >= Capacity) { _dropped++; return false; }
            var r = report;
            if (_dropped > 0)
            {
                r = new SlotTallyReport(r.Family, r.EngineId, r.Helper, r.Reason, r.Buffers, r.Judged, r.Late, r.Missed, r.DipsBegan,
                    r.AverageMsPer10, r.WorstSecondMsPer10, r.Seconds, r.PeriodSeconds, r.Periods, _dropped, r.PartSecondMsPer10);
                _dropped = 0;
            }
            _ring[head % Capacity] = r;
            Volatile.Write(ref _head, head + 1);
            return true;
        }

        /// <summary>
        /// The counts for one period (or a session), with the whole-second worst.
        /// </summary>
        /// <remarks>
        /// <b>The policy for seconds (repair 1).</b> Seconds of audio are counted
        /// from each period's start and never span periods. A buffer's audio is
        /// spread at its own uniform cost across every second boundary it
        /// crosses, so a helper batch of several seconds completes several
        /// seconds and leaves its remainder in the next; this is constant time
        /// however long the batch. A period's trailing part-second is reported
        /// apart and never enters a whole-second maximum, in the period or the
        /// session.
        /// </remarks>
        private struct Acc
        {
            public long Buffers, Judged, Late, Missed, Dips, Seconds;
            public double Work, Audio, SecondWork, SecondAudio, Worst;
            public bool HelperSeen;

            public void Reset()
            {
                Buffers = Judged = Late = Missed = Dips = Seconds = 0;
                Work = Audio = SecondWork = SecondAudio = 0;
                Worst = -1;
                HelperSeen = false;
            }

            public void Add(int judged, int late, bool missed, double work, double audio, long ticksPerSecond)
            {
                Buffers++;
                Judged += judged;
                Late += late;
                if (missed) Missed++;
                if (audio <= 0) return;
                Work += work; Audio += audio;
                double rate = work / audio;          // work per tick of audio, uniform across this buffer
                double second = ticksPerSecond;
                double slack = second * 1e-9;       // rounding in tick arithmetic, never a real fraction
                double need = second - SecondAudio;
                if (audio < need - slack)
                {
                    SecondWork += audio * rate; SecondAudio += audio;
                    return;
                }
                // This buffer finishes the second already begun...
                CompleteSecond((SecondWork + need * rate) / second * 10.0);
                double left = audio - need;
                // ...then every whole second inside it, all at its own cost...
                long whole = (long)Math.Floor((left + slack) / second);
                if (whole > 0)
                {
                    Seconds += whole;
                    if (rate * 10.0 > Worst) Worst = rate * 10.0;
                    left -= whole * second;
                }
                // ...and the remainder begins the next.
                if (left < slack) left = 0;
                SecondWork = left * rate; SecondAudio = left;
            }

            private void CompleteSecond(double perTen)
            {
                if (perTen > Worst) Worst = perTen;
                Seconds++;
                SecondWork = SecondAudio = 0;
            }

            /// <summary>Folds a closed period into a session total: whole seconds only.</summary>
            public void Fold(ref Acc p, bool helper)
            {
                Buffers += p.Buffers; Judged += p.Judged; Late += p.Late; Missed += p.Missed; Dips += p.Dips;
                Work += p.Work; Audio += p.Audio;
                if (p.Seconds > 0 && p.Worst > Worst) Worst = p.Worst;
                Seconds += p.Seconds;
                if (helper) HelperSeen = true;
            }

            public SlotTallyReport Report(DenoiseFamily family, string engine, bool helper, SlotTallyReason reason, double periodSeconds, long periods, long dropped)
            {
                double average = Audio > 0 ? Work / Audio * 10.0 : -1;
                double part = SecondAudio > 0 ? SecondWork / SecondAudio * 10.0 : -1;
                return new SlotTallyReport(family, engine, helper, reason, Buffers, Judged, Late, Missed, Dips,
                    average, Seconds > 0 ? Worst : -1, Seconds, periodSeconds, periods, dropped, part);
            }
        }
    }
}
