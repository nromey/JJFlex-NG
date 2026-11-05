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
            return best?.Hz ?? LibFreqtoLong(slice.Freq);
        }

        /// <summary>Each slice's own echo guard (#266, made per slice in repair round 3).</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Slice, FrequencyEchoGuard> _freqEcho = new();

        private FrequencyEchoGuard EchoGuardFor(Slice slice) =>
            _freqEcho.GetValue(slice, s => new FrequencyEchoGuard("slice " + (s.Letter ?? s.Index.ToString())));

        /// <summary>
        /// What a cache should hold for <paramref name="slice"/> when the slice
        /// reports <paramref name="reported"/>: the report, unless it is the
        /// stale echo of a value our own pending tune replaced, in which case
        /// the pending value.
        /// </summary>
        private ulong ObservedOrPending(Slice slice, ulong reported)
        {
            var guard = EchoGuardFor(slice);
            if (guard.Accept(reported)) return reported;
            return guard.TryPending(out ulong pending) ? pending : reported;
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
            slice.Freq = mhz;
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
