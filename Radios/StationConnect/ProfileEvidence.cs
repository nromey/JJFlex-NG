using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.StationConnect
{
    /// <summary>
    /// Where a property notification came from. FlexLib raises the same
    /// PropertyChanged for a status message the radio sent and for a value
    /// our own setter assigned a moment ago; the design (section 3) counts
    /// the latter as a fourth provenance, not another radio report.
    /// </summary>
    public enum ObservationProvenance
    {
        /// <summary>Parsed from a status message the radio sent.</summary>
        RadioReported,

        /// <summary>Raised synchronously by our own setter on the thread that
        /// called it. Proves nothing about the radio.</summary>
        LocalEcho,
    }

    /// <summary>One observed profile inventory (list) for a type.</summary>
    public sealed class InventoryObservation
    {
        public InventoryObservation(IReadOnlyList<string> names, ObservationProvenance provenance, long sequence, int attemptGeneration, long atMs)
        {
            Names = names ?? Array.Empty<string>();
            Provenance = provenance;
            Sequence = sequence;
            AttemptGeneration = attemptGeneration;
            AtMs = atMs;
        }
        public IReadOnlyList<string> Names { get; }
        public ObservationProvenance Provenance { get; }
        public long Sequence { get; }
        public int AttemptGeneration { get; }
        public long AtMs { get; }
        public bool Contains(string name) =>
            !string.IsNullOrEmpty(name) && Names.Any(n => string.Equals(n, name, StringComparison.Ordinal));
    }

    /// <summary>One observed selection (current profile name) for a type.</summary>
    public sealed class SelectionObservation
    {
        public SelectionObservation(string name, ObservationProvenance provenance, long sequence, int attemptGeneration, long atMs)
        {
            Name = name ?? "";
            Provenance = provenance;
            Sequence = sequence;
            AttemptGeneration = attemptGeneration;
            AtMs = atMs;
        }
        public string Name { get; }
        public ObservationProvenance Provenance { get; }
        public long Sequence { get; }
        public int AttemptGeneration { get; }
        public long AtMs { get; }
    }

    /// <summary>One observed autosave value, with provenance.</summary>
    public sealed class AutosaveObservation
    {
        public AutosaveObservation(bool on, ObservationProvenance provenance, long sequence, int attemptGeneration)
        {
            On = on;
            Provenance = provenance;
            Sequence = sequence;
            AttemptGeneration = attemptGeneration;
        }
        public bool On { get; }
        public ObservationProvenance Provenance { get; }
        public long Sequence { get; }
        public int AttemptGeneration { get; }
    }

    /// <summary>An immutable copy of the profile evidence for one attempt.</summary>
    public sealed class ProfileEvidenceSnapshot
    {
        public ProfileEvidenceSnapshot(
            InventoryObservation globalList, SelectionObservation globalSelection,
            AutosaveObservation autosave, long persistenceLoadedAtSequence,
            long radioEndBoundaryAtSequence, string radioEndBoundaryToken,
            long sequence, int attemptGeneration, long txChainGeneration,
            SelectionObservation txSelection = null, SelectionObservation micSelection = null)
        {
            GlobalList = globalList;
            GlobalSelection = globalSelection;
            TxSelection = txSelection;
            MicSelection = micSelection;
            Autosave = autosave;
            PersistenceLoadedAtSequence = persistenceLoadedAtSequence;
            RadioEndBoundaryAtSequence = radioEndBoundaryAtSequence;
            RadioEndBoundaryToken = radioEndBoundaryToken ?? "";
            Sequence = sequence;
            AttemptGeneration = attemptGeneration;
            TxChainGeneration = txChainGeneration;
        }

        /// <summary>The latest global inventory, or null when none has been observed this attempt.</summary>
        public InventoryObservation GlobalList { get; }

        /// <summary>The latest global selection, or null.</summary>
        public SelectionObservation GlobalSelection { get; }

        /// <summary>The latest transmit-profile selection, or null.</summary>
        public SelectionObservation TxSelection { get; }

        /// <summary>The latest microphone-profile selection, or null.</summary>
        public SelectionObservation MicSelection { get; }

        /// <summary>The latest selection observation for a type, or null.</summary>
        public SelectionObservation SelectionOf(ProfileTypes type)
        {
            switch (type)
            {
                case ProfileTypes.global: return GlobalSelection;
                case ProfileTypes.tx: return TxSelection;
                case ProfileTypes.mic: return MicSelection;
                default: return null;
            }
        }

        /// <summary>The latest RADIO-REPORTED selection for a type, or null
        /// when none has been reported (a local echo is not a report).</summary>
        public SelectionObservation ReportedSelectionOf(ProfileTypes type)
        {
            var s = SelectionOf(type);
            return s != null && s.Provenance == ObservationProvenance.RadioReported ? s : null;
        }

        /// <summary>The latest autosave observation, or null when the radio has
        /// said nothing and we have set nothing.</summary>
        public AutosaveObservation Autosave { get; }

        /// <summary>Sequence at which FlexLib's PersistenceLoaded became true, or
        /// 0. A transport milestone (UDP port reply, WAN registration); kept
        /// as evidence only so a policy can be told to ignore it.</summary>
        public long PersistenceLoadedAtSequence { get; }

        /// <summary>Sequence at which a radio-origin end boundary was observed,
        /// or 0. <b>Nothing in the application emits one today.</b> This is
        /// the hook bench question B fills: if the radio has a distinct end
        /// message, the handler that recognises it calls
        /// <see cref="ProfileEvidenceLog.RadioEndBoundary"/> and the completion
        /// policy is taught to require it.</summary>
        public long RadioEndBoundaryAtSequence { get; }
        public string RadioEndBoundaryToken { get; }

        public long Sequence { get; }
        public int AttemptGeneration { get; }

        /// <summary>Increments on every RADIO-REPORTED transmit-chain change
        /// this attempt. A capture taken at one generation is stale at the
        /// next; the deferred apply revalidates against it.</summary>
        public long TxChainGeneration { get; }

        /// <summary>The latest RADIO-REPORTED autosave value, or null.</summary>
        public bool? RadioReportedAutosave =>
            Autosave != null && Autosave.Provenance == ObservationProvenance.RadioReported ? Autosave.On : (bool?)null;
    }

    /// <summary>
    /// Profile-related observations for the current attempt, each stamped
    /// with provenance, a sequence number and the attempt generation.
    /// Fed by the production radio property handler; read by the coordinator
    /// and the completion policy.
    /// </summary>
    public sealed class ProfileEvidenceLog
    {
        private readonly object _lock = new object();
        private readonly IStationClock _clock;
        private InventoryObservation _globalList;
        private SelectionObservation _globalSelection;
        private SelectionObservation _txSelection;
        private SelectionObservation _micSelection;
        private AutosaveObservation _autosave;
        private long _persistenceLoadedAt;
        private long _endBoundaryAt;
        private string _endBoundaryToken = "";
        private long _sequence;
        private int _attemptGeneration;
        private long _txChainGeneration;

        public ProfileEvidenceLog(IStationClock clock)
        {
            _clock = clock ?? MonotonicStationClock.Instance;
        }

        public event Action Changed;

        public void Reset(int attemptGeneration)
        {
            lock (_lock)
            {
                _globalList = null;
                _globalSelection = null;
                _txSelection = null;
                _micSelection = null;
                _autosave = null;
                _persistenceLoadedAt = 0;
                _endBoundaryAt = 0;
                _endBoundaryToken = "";
                _attemptGeneration = attemptGeneration;
                _txChainGeneration = 0;
            }
            Changed?.Invoke();
        }

        public void GlobalListObserved(IReadOnlyList<string> names, ObservationProvenance provenance, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                _globalList = new InventoryObservation(names?.ToList(), provenance, _sequence, attemptGeneration, _clock.NowMs);
            }
            Changed?.Invoke();
        }

        public void GlobalSelectionObserved(string name, ObservationProvenance provenance, int attemptGeneration) =>
            SelectionObserved(ProfileTypes.global, name, provenance, attemptGeneration);

        /// <summary>A selection observation for one type (global, tx or mic),
        /// with provenance. The post-station phase confirms a sent selection
        /// by a RADIO-REPORTED observation after the send.</summary>
        public void SelectionObserved(ProfileTypes type, string name, ObservationProvenance provenance, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                var obs = new SelectionObservation(name, provenance, _sequence, attemptGeneration, _clock.NowMs);
                switch (type)
                {
                    case ProfileTypes.global: _globalSelection = obs; break;
                    case ProfileTypes.tx: _txSelection = obs; break;
                    case ProfileTypes.mic: _micSelection = obs; break;
                    default: return;
                }
            }
            Changed?.Invoke();
        }

        public void AutosaveObserved(bool on, ObservationProvenance provenance, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                _autosave = new AutosaveObservation(on, provenance, _sequence, attemptGeneration);
            }
            Changed?.Invoke();
        }

        public void PersistenceLoadedObserved(int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                if (_persistenceLoadedAt == 0) _persistenceLoadedAt = _sequence;
            }
            Changed?.Invoke();
        }

        /// <summary>A radio-origin "this load has finished" boundary. No
        /// production handler calls this yet; see
        /// <see cref="ProfileEvidenceSnapshot.RadioEndBoundaryAtSequence"/>.</summary>
        public void RadioEndBoundary(string token, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                _endBoundaryAt = _sequence;
                _endBoundaryToken = token ?? "";
            }
            Changed?.Invoke();
        }

        /// <summary>A radio-reported change to a transmit-chain field.</summary>
        public void TxChainReported(int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                _txChainGeneration++;
            }
            Changed?.Invoke();
        }

        public ProfileEvidenceSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new ProfileEvidenceSnapshot(
                    _globalList, _globalSelection, _autosave, _persistenceLoadedAt,
                    _endBoundaryAt, _endBoundaryToken, _sequence, _attemptGeneration, _txChainGeneration,
                    _txSelection, _micSelection);
            }
        }

        public long Sequence { get { lock (_lock) return _sequence; } }
    }
}
