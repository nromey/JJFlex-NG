using Flex.Smoothlake.FlexLib;

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

        /// <summary>A frequency and the slice it belongs to. Immutable.</summary>
        private sealed class SliceHz
        {
            public SliceHz(Slice slice, ulong hz) { Slice = slice; Hz = hz; }
            /// <summary>The slice this frequency is about; null when written with no slice to name.</summary>
            public Slice Slice { get; }
            public ulong Hz { get; }
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
        /// <paramref name="cache"/>'s number when it belongs to
        /// <paramref name="slice"/> and holds one, otherwise the slice's own.
        /// </summary>
        private ulong FrequencyOf(Slice slice, SliceHz cache) =>
            cache != null && cache.Hz != 0UL && ReferenceEquals(cache.Slice, slice)
                ? cache.Hz
                : LibFreqtoLong(slice.Freq);

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
