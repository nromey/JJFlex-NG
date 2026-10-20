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
        /// when none has been reported this attempt. Stored SEPARATELY from
        /// the latest observation of any provenance: a local echo of our own
        /// setter after a radio report does not erase the report (Track G2
        /// re-review, step 4 — until then it did, and ReportedSelectionOf
        /// returned null the moment we sent anything).</summary>
        public SelectionObservation ReportedSelectionOf(ProfileTypes type)
        {
            switch (type)
            {
                case ProfileTypes.global: return GlobalReported;
                case ProfileTypes.tx: return TxReported;
                case ProfileTypes.mic: return MicReported;
                default: return null;
            }
        }

        /// <summary>The last radio-reported global selection, or null.</summary>
        public SelectionObservation GlobalReported { get; internal set; }

        /// <summary>The last radio-reported transmit selection, or null.</summary>
        public SelectionObservation TxReported { get; internal set; }

        /// <summary>The last radio-reported microphone selection, or null.</summary>
        public SelectionObservation MicReported { get; internal set; }

        /// <summary>The transmit-chain property names the radio has reported
        /// this attempt (FlexLib raises each unconditionally from the
        /// transmit status, so a name here is a genuine receipt). The live
        /// audio capture requires the whole set it snapshots; a missing one
        /// means the radio has not yet said what that field holds.</summary>
        public IReadOnlyCollection<string> TxChainFieldsReported { get; internal set; } = Array.Empty<string>();

        /// <summary>The latest autosave observation, or null when the radio has
        /// said nothing and we have set nothing.</summary>
        public AutosaveObservation Autosave { get; }

        /// <summary>Sequence at which FlexLib's PersistenceLoaded became true, or
        /// 0. A transport milestone (UDP port reply, WAN registration); kept
        /// as evidence only so a policy can be told to ignore it.</summary>
        public long PersistenceLoadedAtSequence { get; }

        /// <summary>
        /// Sequence at which the POST-CONNECT FENCE was answered, or 0. The
        /// fence is one reply-bearing no-op (<c>ping</c>) FlexBase sends on the
        /// command channel right after the vendor's Connect returns; the radio
        /// answers it only after handling every earlier command on that
        /// channel, the subscriptions among them — so every object the radio
        /// restores for a returning GUI client has been reported before this
        /// sequence. The materialization policy's boundary since #782; see
        /// <see cref="MaterializationEndsAtConnectFencePolicy"/>.
        /// </summary>
        public long ConnectFenceAtSequence { get; internal set; }

        /// <summary>Sequence at which a radio-origin end boundary was observed,
        /// or 0. <b>Nothing in the application emits one today</b>, and the
        /// production completion policy no longer waits for one: bench B found
        /// the radio re-reports its global LIST after a load instead
        /// (<see cref="LoadCompletionByGlobalListReportPolicy"/>, #639), which
        /// is read from <see cref="GlobalList"/> by sequence. This stays as the
        /// hook for a radio with a distinct end message: the handler that
        /// recognises it calls <see cref="ProfileEvidenceLog.RadioEndBoundary"/>
        /// and <see cref="LoadCompletionByRadioEndBoundaryPolicy"/> requires it.</summary>
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
        private SelectionObservation _globalReported;
        private SelectionObservation _txReported;
        private SelectionObservation _micReported;
        private readonly HashSet<string> _txChainFields = new HashSet<string>(StringComparer.Ordinal);
        private AutosaveObservation _autosave;
        private long _persistenceLoadedAt;
        private long _connectFenceAt;
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
                _globalReported = null;
                _txReported = null;
                _micReported = null;
                _txChainFields.Clear();
                _autosave = null;
                _persistenceLoadedAt = 0;
                _connectFenceAt = 0;
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
                bool reported = provenance == ObservationProvenance.RadioReported;
                switch (type)
                {
                    case ProfileTypes.global: _globalSelection = obs; if (reported) _globalReported = obs; break;
                    case ProfileTypes.tx: _txSelection = obs; if (reported) _txReported = obs; break;
                    case ProfileTypes.mic: _micSelection = obs; if (reported) _micReported = obs; break;
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

        /// <summary>The radio answered the post-connect fence for this attempt
        /// (see <see cref="ProfileEvidenceSnapshot.ConnectFenceAtSequence"/>).
        /// The first answer counts; a later one adds nothing.</summary>
        public void ConnectFenceObserved(int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                if (_connectFenceAt == 0) _connectFenceAt = _sequence;
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

        /// <summary>A radio-reported change to a transmit-chain field, named,
        /// so the capture can require the whole receipt set.</summary>
        public void TxChainReported(int attemptGeneration, string propertyName = null)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                _txChainGeneration++;
                if (!string.IsNullOrEmpty(propertyName)) _txChainFields.Add(propertyName);
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
                    _txSelection, _micSelection)
                {
                    GlobalReported = _globalReported,
                    TxReported = _txReported,
                    MicReported = _micReported,
                    TxChainFieldsReported = _txChainFields.ToList(),
                    ConnectFenceAtSequence = _connectFenceAt,
                };
            }
        }

        public long Sequence { get { lock (_lock) return _sequence; } }
    }
}
