using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    public partial class FlexBase
    {
        // ── A cached frequency and the slice it belongs to, as one value ──────
        //
        // #642, repair round 2 (Codex's re-check). The receive and transmit
        // frequency caches were bare numbers, and which slice each described
        // was inferred elsewhere: the receive cache from "this slice is
        // Active", the transmit cache from a separate _txCacheSlice written
        // just before it. Selecting a slice moves RXVFO at once and queues
        // Active; FlexLib raises the slice's Active BEFORE the radio's
        // ActiveSlice, and only the latter refreshed the receive cache. In
        // between, the newly selected slice was Active while the cache still
        // held the old slice's frequency, and F, the title and the notice
        // before a door keys read one slice's letter with another's number.
        //
        // So each cache is now ONE immutable value carrying both the frequency
        // and the slice it was written for, published by a single reference
        // write. A reader takes it once and believes the number only for that
        // very slice; for any other slice it reads the slice's own frequency.
        // Optimistic tuning writes keep their value with the slice they were
        // written to, so a step still reads back at once.

        // Repair round 3 (Codex's second re-check, ruled by Noel 2026-11-04 to
        // be fixed in this round). Three more gaps in the same core:
        //
        // - A queued tune resolved its slice when it RAN (VFOToSlice(RXVFO)
        //   inside the closure), so a step pressed on A landed on B selected
        //   meanwhile. Every tune is now bound to the slice it was pressed on
        //   (WriteTuneTo), and a slice gone by then gets nothing.
        // - The echo guard armed against the raw receive cache, which during a
        //   selection still held the OLD slice's number, so the new slice's own
        //   stale report rewound a step. The guard is now one per slice
        //   (EchoGuardFor), armed against that slice's own value, and every
        //   refresh of a cache from a slice's own number asks it
        //   (ObservedOrPending), including the ActiveSlice refresh.
        // - The receive and transmit caches could hold different optimistic
        //   values for ONE slice, so F and the title disagreed. A tune now
        //   publishes the slice's one new value to every cache that describes
        //   it (PublishTuning), and a reader takes the newer of the two when
        //   both describe the slice it asks about (ValueOf), so one slice has
        //   one number by construction.

        // Repair round 4 (Codex's third re-check):
        //
        // - FlexLib's Freq setter raises the slice's Freq notification itself,
        //   synchronously, after SENDING and before any reply. The guard took
        //   that as the radio's confirmation, disarmed as the queue drained,
        //   and the radio's first delayed report then rewound a fully sent run
        //   of steps. WriteTuneTo now marks its own write (LocalTuneWrite), the
        //   handler never settles the guard on it, and a refresh from the
        //   slice's own number (ObservedOrPending) only reads the pending
        //   value, never settles: the vendor's number is our own send by then.
        //   Only a report from the radio confirms.
        // - A slice's pending value is its value even when neither cache
        //   describes it any more (ValueOf falls back to the guard's pending
        //   value before the slice's own number), so returning to a slice with
        //   a step still on its way does not lose the step.
        // - The panadapter's goto and a slice copy tune through the same
        //   setters and the same WriteTuneTo, so they are bound to the
        //   operator's selection at the press and checked for the slice's
        //   lifetime at the write.

        private static long _cacheSeq;

        /// <summary>A frequency and the slice it belongs to. Immutable.</summary>
        private sealed class SliceHz
        {
            public SliceHz(Slice slice, ulong hz)
            {
                Slice = slice;
                Hz = hz;
                Seq = System.Threading.Interlocked.Increment(ref _cacheSeq);
            }
            /// <summary>The slice this frequency is about; null when written with no slice to name.</summary>
            public Slice Slice { get; }
            public ulong Hz { get; }
            /// <summary>Publication order, so a reader can tell which of two caches is newer.</summary>
            public long Seq { get; }
        }

        /// <summary>The receive cache: the active slice's frequency, or an operator's write to the selected slice.</summary>
        private volatile SliceHz _rxCache;

        /// <summary>The transmit caches' frequency and the designated slice it was read from.</summary>
        private volatile SliceHz _txCache;

        /// <summary>The receive cache's number, whichever slice it belongs to. For the echo guard's "what it was".</summary>
        private ulong _RXFrequency => _rxCache?.Hz ?? 0UL;

        /// <summary>The transmit cache's number, whichever slice it belongs to.</summary>
        private ulong _TXFrequency => _txCache?.Hz ?? 0UL;

        /// <summary>The slice the transmit caches describe.</summary>
        private Slice _txCacheSlice => _txCache?.Slice;

        /// <summary>Publish the receive cache for <paramref name="slice"/>, number and identity together.</summary>
        private void SetRxCache(Slice slice, ulong hz) => _rxCache = new SliceHz(slice, hz);

        /// <summary>Publish the transmit cache for <paramref name="slice"/>, number and identity together.</summary>
        private void SetTxCache(Slice slice, ulong hz) => _txCache = new SliceHz(slice, hz);

        /// <summary>
        /// <paramref name="slice"/>'s one frequency: the newer of the two
        /// caches that belong to it and hold a number, otherwise the slice's
        /// own. Take each cache once and pass both, so a reading that names two
        /// slices reads both from the same pair.
        /// </summary>
        private ulong ValueOf(Slice slice, SliceHz rx, SliceHz tx)
        {
            SliceHz best = null;
            if (rx != null && rx.Hz != 0UL && ReferenceEquals(rx.Slice, slice)) best = rx;
            if (tx != null && tx.Hz != 0UL && ReferenceEquals(tx.Slice, slice) && (best == null || tx.Seq > best.Seq))
                best = tx;
            var guard = EchoGuardFor(slice);
            // Our last tune went out and nothing confirmed it in the give-up
            // window (repair round 4): the vendor's number is the truth now,
            // the radio's own last report or our send, not the cache.
            if (guard.SentAndGivenUp()) return LibFreqtoLong(slice.Freq);
            if (best != null) return best.Hz;
            // Neither cache describes this slice (repair round 4): a step still
            // on its way is its value, not the vendor's older number.
            return guard.TryPending(out ulong pending) ? pending : LibFreqtoLong(slice.Freq);
        }

        /// <summary>Each slice's own echo guard (#266, made per slice in repair round 3).</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Slice, FrequencyEchoGuard> _freqEcho = new();

        private FrequencyEchoGuard EchoGuardFor(Slice slice) =>
            _freqEcho.GetValue(slice, s => new FrequencyEchoGuard("slice " + (s.Letter ?? s.Index.ToString())));

        /// <summary>
        /// What a cache should hold for <paramref name="slice"/> when it is
        /// refreshed from the slice's own number <paramref name="reported"/>:
        /// our own tune still on its way while one is pending, otherwise the
        /// number. A refresh is not a report from the radio (repair round 4):
        /// once the queue has sent our write, the vendor's number IS our
        /// write, so a refresh never settles the guard. Only the radio's own
        /// Freq report does, in the slice handler.
        /// </summary>
        private ulong ObservedOrPending(Slice slice, ulong reported)
        {
            return EchoGuardFor(slice).TryPending(out ulong pending) ? pending : reported;
        }

        /// <summary>
        /// The slice our own queued tune is writing right now, on this thread
        /// (repair round 4). FlexLib's Freq setter raises the slice's Freq
        /// notification synchronously inside the write, so a notification for
        /// this slice on this thread is our send, not the radio's report.
        /// </summary>
        [System.ThreadStatic] private static Slice t_localTuneWrite;

        /// <summary>True while our own tune of <paramref name="slice"/> is raising its Freq notification.</summary>
        private static bool LocalTuneWrite(Slice slice) => slice != null && ReferenceEquals(t_localTuneWrite, slice);

        /// <summary>
        /// Our own send's Freq notification, judged without settling anything:
        /// it may be written into the caches only when it is the value now
        /// pending (the last write of a run) or nothing is pending.
        /// </summary>
        private bool LocalTuneShows(Slice slice, ulong echoed)
        {
            var guard = EchoGuardFor(slice);
            guard.Sent(echoed);
            return !guard.TryPending(out ulong pending) || pending == echoed;
        }

        /// <summary>
        /// A tune's optimistic value, published as the slice's ONE value: to
        /// the receive cache when this is the selected slice's tune or the
        /// receive cache already describes it, and to the transmit cache when
        /// the slice is designated for transmit.
        /// </summary>
        private void PublishTuning(Slice slice, ulong hz, bool selectedTune)
        {
            if (selectedTune || ReferenceEquals(_rxCache?.Slice, slice) || ReferenceEquals(SelectedSliceNow(), slice))
                SetRxCache(slice, hz);
            if (slice.IsTransmitSlice)
            {
                if (!ReferenceEquals(_txCacheSlice, slice)) NoteTransmitSliceAsFound(slice);
                SetTxCache(slice, hz);
            }
        }

        /// <summary>
        /// The queued half of a tune, bound to the slice it was pressed on.
        /// A slice no longer ours by the time it runs gets nothing, and no
        /// other slice gets it either.
        /// </summary>
        private void WriteTuneTo(Slice slice, double mhz, string what)
        {
            bool ours;
            var slices = mySlices;
            if (slices == null) ours = false;
            else lock (slices) ours = slices.Contains(slice);
            if (!ours)
            {
                Tracing.TraceLine(what + ": slice " + slice.Letter + " is gone, so its tune to " + mhz
                    + " was not sent to any slice", System.Diagnostics.TraceLevel.Info);
                return;
            }
            Slice outer = t_localTuneWrite;
            t_localTuneWrite = slice;
            try { slice.Freq = mhz; }
            finally { t_localTuneWrite = outer; }
        }

        /// <summary>The slice at RXVFO, under the roster's lock; null when there is none.</summary>
        private Slice SelectedSliceNow()
        {
            var slices = mySlices;
            if (slices == null) return null;
            lock (slices)
            {
                int rx = _RXVFO;
                return (rx >= 0 && rx < slices.Count) ? slices[rx] : null;
            }
        }
    }
}
