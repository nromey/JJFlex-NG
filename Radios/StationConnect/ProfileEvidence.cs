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
            Ordinal = ObservationOrdinal.Next();
        }
        public IReadOnlyList<string> Names { get; }
        public ObservationProvenance Provenance { get; }
        public long Sequence { get; }
        public int AttemptGeneration { get; }
        public long AtMs { get; }

        /// <summary>The <see cref="ObservationOrdinal"/> at which this list was
        /// observed, for ordering against the station tracker's slices.</summary>
        public long Ordinal { get; }

        /// <summary>
        /// True when this radio-reported list is the ANSWER to a
        /// <c>profile global info</c> this application sent and had not yet
        /// had answered (<see cref="ProfileEvidenceLog.GlobalInventoryAsked"/>).
        /// The radio sends one list status per ask, so the oldest outstanding
        /// ask is taken as answered by the next radio-reported list. Such a
        /// list is inventory, not a report of anything the radio did on its
        /// own, and is never completion evidence (#639, Codex B1).
        /// </summary>
        public bool AnswersOurAsk { get; internal set; }

        /// <summary>
        /// True when this radio-reported list was taken as the answer to
        /// FlexLib's OWN connect-time <c>profile global info</c>, which
        /// <c>Radio.Connect</c> sends from <c>GetProfileLists()</c> without
        /// telling us (<see cref="ProfileEvidenceLog.FlexLibConnectInventoryAsked"/>).
        /// Like <see cref="AnswersOurAsk"/>, inventory and never completion
        /// evidence (#639, repair round two).
        /// </summary>
        public bool AnswersFlexLibConnectAsk { get; internal set; }
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

        /// <summary>
        /// The latest RADIO-REPORTED global inventory that was NOT taken as the
        /// answer to any recorded ask, or null. Two senders of
        /// <c>profile global info</c> exist and both are recorded: FlexLib's
        /// own connect-time ask (<see cref="ProfileEvidenceLog.FlexLibConnectInventoryAsked"/>,
        /// recorded by FlexBase immediately before the vendor's Connect) and
        /// ours (<see cref="ProfileEvidenceLog.GlobalInventoryAsked"/>). What
        /// is left is the radio's own list: the re-report it makes a few
        /// milliseconds after the last slice a global load restores. The
        /// completion policy reads this, never <see cref="GlobalList"/>, so a
        /// late answer to either ask cannot be mistaken for the load finishing
        /// (#639, Codex B1 and the round-one re-verify).
        /// </summary>
        public InventoryObservation UnsolicitedGlobalList { get; internal set; }

        /// <summary>How many <c>profile global info</c> asks of ours have gone
        /// out this attempt and not yet been taken as answered.</summary>
        public int OutstandingGlobalAsks { get; internal set; }

        /// <summary>How many of FlexLib's connect-time <c>profile global info</c>
        /// asks are recorded and not yet taken as answered (0 or 1 on a real
        /// connect: one vendor Connect per attempt).</summary>
        public int OutstandingFlexLibConnectAsks { get; internal set; }

        /// <summary>
        /// True when FlexLib's connect-time answer MAY still be on its way:
        /// its ask is outstanding, or an ask of ours that went out while it
        /// was outstanding is itself still unanswered. In the second case the
        /// list taken as FlexLib's answer may have been ours, since nothing in
        /// a list says which ask it answers. Noel ruled (2026-10-20 08:15)
        /// that a load sent while this is true never confirms (#639).
        /// </summary>
        public bool FlexLibConnectAnswerMayBeOutstanding { get; internal set; }

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
        private InventoryObservation _unsolicitedGlobalList;
        // Asks are taken as answered in the order they were made, as far as
        // the accounting can tell: FlexLib's connect-time ask first (it goes
        // out inside the vendor's Connect, before the station phase can ask
        // anything), then ours oldest first. Each of ours carries its own id,
        // so a failed send withdraws THAT ask and never a later one (Codex's
        // two-ask fault edge, round-one re-verify).
        private sealed class OurAsk
        {
            public long Id;
            /// <summary>Made while FlexLib's ask was outstanding (or while
            /// another such ask of ours was), so the list taken as FlexLib's
            /// answer may have been this one's.</summary>
            public bool MayCoverFlexLibsAnswer;
        }
        private readonly List<OurAsk> _ourAsks = new List<OurAsk>();
        private int _flexLibConnectAsks;
        private long _nextAskId;
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
                _unsolicitedGlobalList = null;
                _ourAsks.Clear();
                _flexLibConnectAsks = 0;
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

        /// <summary>
        /// This application is about to send <c>profile global info</c>. Call
        /// it BEFORE the command goes out, so the answer cannot arrive ahead of
        /// the record, and keep the returned id: if the send then fails, pass
        /// it to <see cref="GlobalInventoryAskWithdrawn"/>. A radio-reported
        /// global list is taken as the answer to the oldest outstanding ask
        /// and is never completion evidence. An ask the radio never answers
        /// therefore holds the completion policy closed for the attempt (the
        /// load's own list re-report is consumed as the answer), which is the
        /// fail-closed direction (#639, Codex B1). Returns 0, recording
        /// nothing, when the generation is not the current attempt's.
        /// </summary>
        public long GlobalInventoryAsked(int attemptGeneration)
        {
            long id;
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return 0;
                _sequence++;
                id = ++_nextAskId;
                _ourAsks.Add(new OurAsk
                {
                    Id = id,
                    MayCoverFlexLibsAnswer = _flexLibConnectAsks > 0 || _ourAsks.Any(a => a.MayCoverFlexLibsAnswer),
                });
            }
            Changed?.Invoke();
            return id;
        }

        /// <summary>The ask recorded by <see cref="GlobalInventoryAsked"/> under
        /// <paramref name="askId"/> never reached the radio (the send threw or
        /// the transport refused it). Removes that ask only; an ask already
        /// taken as answered, or an unknown id, changes nothing.</summary>
        public void GlobalInventoryAskWithdrawn(int attemptGeneration, long askId)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _ourAsks.RemoveAll(a => a.Id == askId);
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// FlexLib is about to send its OWN <c>profile global info</c>:
        /// <c>Radio.Connect</c> calls <c>GetProfileLists()</c> from
        /// <c>InitializeCommandConnection</c>, before the <c>sub</c> commands,
        /// and nothing tells us. FlexBase records it immediately before every
        /// live call to the vendor's Connect, after the attempt's reset, so
        /// the answer cannot arrive ahead of the record. Until a radio-reported
        /// list is taken as its answer, a list may be that answer and is not
        /// the radio's own; and a load sent while
        /// <see cref="ProfileEvidenceSnapshot.FlexLibConnectAnswerMayBeOutstanding"/>
        /// is true never confirms (Noel, 2026-10-20 08:15; every radio, 08:13).
        /// There is no withdrawal: FlexLib's ask has no reply we can see, and
        /// a vendor Connect that fails before sending it leaves no station
        /// phase to judge (#639, repair round two).
        /// </summary>
        public void FlexLibConnectInventoryAsked(int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                _flexLibConnectAsks++;
            }
            Changed?.Invoke();
        }

        public void GlobalListObserved(IReadOnlyList<string> names, ObservationProvenance provenance, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _sequence++;
                var obs = new InventoryObservation(names?.ToList(), provenance, _sequence, attemptGeneration, _clock.NowMs);
                _globalList = obs;
                if (provenance == ObservationProvenance.RadioReported)
                {
                    if (_flexLibConnectAsks > 0)
                    {
                        _flexLibConnectAsks--;
                        obs.AnswersFlexLibConnectAsk = true;
                    }
                    else if (_ourAsks.Count > 0)
                    {
                        _ourAsks.RemoveAt(0);
                        obs.AnswersOurAsk = true;
                    }
                    else
                    {
                        _unsolicitedGlobalList = obs;
                    }
                }
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
                    UnsolicitedGlobalList = _unsolicitedGlobalList,
                    OutstandingGlobalAsks = _ourAsks.Count,
                    OutstandingFlexLibConnectAsks = _flexLibConnectAsks,
                    FlexLibConnectAnswerMayBeOutstanding = _flexLibConnectAsks > 0 || _ourAsks.Any(a => a.MayCoverFlexLibsAnswer),
                };
            }
        }

        public long Sequence { get { lock (_lock) return _sequence; } }
    }
}
