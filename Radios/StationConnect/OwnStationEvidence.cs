using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.StationConnect
{
    /// <summary>
    /// One slice this client owns, as FlexLib reported it READY. FlexLib
    /// raises SliceAdded only from <c>Slice.CheckReady</c>: full status
    /// received and the panadapter ready, or an explicitly headless slice
    /// (stream id 0). So an entry here has a resolved association or a
    /// declared absence of one; it never has an unresolved one.
    /// </summary>
    public sealed class OwnSlice
    {
        public OwnSlice(int index, string letter, uint handle, uint panadapterStreamId, long sequence, int attemptGeneration)
        {
            Index = index;
            Letter = letter ?? "";
            Handle = handle;
            PanadapterStreamId = panadapterStreamId;
            Sequence = sequence;
            AttemptGeneration = attemptGeneration;
        }

        /// <summary>The radio's slice index: the identity, stable for the slice's life.</summary>
        public int Index { get; }
        public string Letter { get; }
        public uint Handle { get; }
        public uint PanadapterStreamId { get; }
        public bool Headless => PanadapterStreamId == 0;

        /// <summary>The station-observation sequence number at which this
        /// slice was reported. A request armed at sequence N accepts only a
        /// slice reported after N.</summary>
        public long Sequence { get; }
        public int AttemptGeneration { get; }

        public override string ToString() => "slice " + Index + (string.IsNullOrEmpty(Letter) ? "" : " " + Letter)
            + (Headless ? " (headless)" : " pan 0x" + PanadapterStreamId.ToString("X")) + " @" + Sequence;
    }

    /// <summary>An immutable copy of this client's own station as last observed.</summary>
    public sealed class StationSnapshot
    {
        public StationSnapshot(IReadOnlyList<OwnSlice> slices, IReadOnlyList<uint> panadapters, long sequence, long generation, int attemptGeneration, long lastObservedAtMs)
        {
            Slices = slices ?? Array.Empty<OwnSlice>();
            Panadapters = panadapters ?? Array.Empty<uint>();
            Sequence = sequence;
            Generation = generation;
            AttemptGeneration = attemptGeneration;
            LastObservedAtMs = lastObservedAtMs;
        }

        public IReadOnlyList<OwnSlice> Slices { get; }
        public IReadOnlyList<uint> Panadapters { get; }

        /// <summary>Counts every own-station observation this attempt, added
        /// or removed. Never decreases.</summary>
        public long Sequence { get; }

        /// <summary>Increments when membership, ownership or materialization of
        /// the own station changes.</summary>
        public long Generation { get; }

        public int AttemptGeneration { get; }

        /// <summary>Monotonic time of the last own-station observation, or 0
        /// when there has been none this attempt.</summary>
        public long LastObservedAtMs { get; }

        public int OwnSliceCount => Slices.Count;

        /// <summary>The committed #563 predicate, fed from evidence: at least
        /// one own slice. A minimum-presence fact, never completion.</summary>
        public bool StationPresent => Slices.Count > 0;

        public bool HasSlice(int index) => Slices.Any(s => s.Index == index);

        /// <summary>The own slices reported strictly after <paramref name="sequence"/>
        /// whose index was not in <paramref name="before"/>: the identity test for
        /// "the resource my request produced", as opposed to a count increase.</summary>
        public IEnumerable<OwnSlice> NewSince(long sequence, StationSnapshot before) =>
            Slices.Where(s => s.Sequence > sequence && (before == null || !before.HasSlice(s.Index)));

        public override string ToString() =>
            "station gen " + Generation + " seq " + Sequence + " [" + string.Join(", ", Slices.Select(s => s.ToString())) + "]";
    }

    /// <summary>
    /// Own-slice and own-panadapter observations for the current attempt,
    /// fed by the production slice and panadapter handlers using the same
    /// handle-ownership rule they already apply.
    /// </summary>
    public sealed class StationTracker
    {
        private readonly object _lock = new object();
        private readonly Dictionary<int, OwnSlice> _slices = new Dictionary<int, OwnSlice>();
        private readonly HashSet<uint> _pans = new HashSet<uint>();
        private readonly IStationClock _clock;
        private long _sequence;
        private long _generation;
        private int _attemptGeneration;
        private long _lastObservedAtMs;

        public StationTracker(IStationClock clock)
        {
            _clock = clock ?? MonotonicStationClock.Instance;
        }

        public event Action Changed;

        public void Reset(int attemptGeneration)
        {
            lock (_lock)
            {
                _slices.Clear();
                _pans.Clear();
                _attemptGeneration = attemptGeneration;
                _generation++;
                _lastObservedAtMs = 0;
            }
            Changed?.Invoke();
        }

        /// <summary>An own slice FlexLib reported ready.</summary>
        public void OwnSliceAdded(int index, string letter, uint handle, uint panadapterStreamId, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                _slices[index] = new OwnSlice(index, letter, handle, panadapterStreamId, _sequence, attemptGeneration);
                _generation++;
                _lastObservedAtMs = _clock.NowMs;
            }
            Changed?.Invoke();
        }

        public void OwnSliceRemoved(int index, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                if (_slices.Remove(index)) _generation++;
                _lastObservedAtMs = _clock.NowMs;
            }
            Changed?.Invoke();
        }

        public void OwnPanadapterAdded(uint streamId, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                if (_pans.Add(streamId)) _generation++;
                _lastObservedAtMs = _clock.NowMs;
            }
            Changed?.Invoke();
        }

        public void OwnPanadapterRemoved(uint streamId, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                if (_pans.Remove(streamId)) _generation++;
                _lastObservedAtMs = _clock.NowMs;
            }
            Changed?.Invoke();
        }

        public StationSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new StationSnapshot(
                    _slices.Values.OrderBy(s => s.Index).ToList(),
                    _pans.OrderBy(p => p).ToList(),
                    _sequence, _generation, _attemptGeneration, _lastObservedAtMs);
            }
        }

        public long Sequence { get { lock (_lock) return _sequence; } }
    }
}
