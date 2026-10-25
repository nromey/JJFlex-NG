#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.StationConnect;

namespace Radios
{
    /// <summary>
    /// FlexBase, the multi-user side: our slices, other clients, and settling.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        // multi-user region
        #region multiUser
        private int initialFreeSlices = -1;
        private int _TotalNumSlices;
        /// <summary>
        /// Total panadapters and slices on the radio.
        /// </summary>
        /// <remarks>
        /// #584: read LIVE from the radio's slice list. It returned a field
        /// assigned once per connect and never again, so the Slice menu, which
        /// IS rebuilt on every slice add and release, re-read a frozen number
        /// and said "4" after the operator was down to 2. The field is kept
        /// only for the moment no radio object exists.
        /// </remarks>
        public int TotalNumSlices
        {
            get
            {
                var radio = theRadio;
                return radio?.SliceList?.Count ?? _TotalNumSlices;
            }
        }

        /// <summary>
        /// "2 of 4": the radio's slices in use over this model's ceiling, for
        /// the Slice menu's label and the About dialog. Null when there is no
        /// radio, or when the pair is impossible, so the caller says less
        /// rather than something wrong (#584: "4 of 0" was spoken with the
        /// same confidence as a correct reading).
        /// </summary>
        /// <remarks>
        /// The ceiling is <see cref="TotalMaxSlices"/>, the model table, NOT
        /// <see cref="MaxSlices"/>: FlexLib's value came back 0 from a real
        /// remote 6300 on 2026-10-10 and from the bench 8600 on 2026-09-20.
        /// </remarks>
        public string SliceOccupancy =>
            theRadio == null ? null : SliceOccupancyText(TotalNumSlices, TotalMaxSlices);

        /// <summary>The text of <see cref="SliceOccupancy"/>, or null when
        /// <paramref name="used"/> over <paramref name="max"/> cannot be true.</summary>
        public static string SliceOccupancyText(int used, int max) =>
            max <= 0 || used < 0 || used > max ? null : used + " of " + max;

        /// <summary>
        /// Maximum slices currently available (from FlexLib discovery — unreliable, may be 0).
        /// Prefer TotalMaxSlices for capacity checks.
        /// </summary>
        public int MaxSlices => theRadio?.MaxSlices ?? 0;

        /// <summary>
        /// Total maximum slices for this radio model (from hardware specs, always correct).
        /// FlexLib's MaxSlices reports available-remaining which can be 0 at startup
        /// when a profile loads all slots. This gives the true model capacity.
        /// </summary>
        public int TotalMaxSlices
        {
            get
            {
                string model = theRadio?.Model ?? string.Empty;
                return model switch
                {
                    "FLEX-6300" => 2,
                    "FLEX-6400" or "FLEX-6400M" => 2,
                    "FLEX-6500" => 4,
                    "FLEX-6600" or "FLEX-6600M" => 4,
                    "FLEX-6700" or "FLEX-6700R" => 8,
                    "FLEX-8400" or "FLEX-8400M" => 2,
                    "FLEX-8600" or "FLEX-8600M" => 4,
                    "AU-510" or "AU-510M" => 2,
                    "AU-520" or "AU-520M" => 4,
                    _ => theRadio?.MaxSlices > 0 ? theRadio.MaxSlices : 2 // safe fallback
                };
            }
        }

        internal List<Slice> mySlices = new List<Slice>();

        /// <summary>
        /// Tracks slice removals that have been enqueued but not yet processed.
        /// Prevents NewSlice() from seeing stale SliceList.Count on the UI thread
        /// before the queue thread processes the Close() call. (BUG-049 fix)
        /// </summary>
        private volatile int _pendingRemovals;

        /// <summary>
        /// number of Panadapters and slices for this radio instance.
        /// </summary>
        public int MyNumSlices
        {
            get {
                int rv;
                lock (mySlices)
                {
                    rv = mySlices.Count;
                }
                return rv;
            }
        }

        /// <summary>
        /// number of slices used by others.
        /// </summary>
        public int OtherNumSlices
        {
            get
            {
                return theRadio.SliceList.Count - MyNumSlices;
            }
        }

        // SliceState(int) and its SliceStates enum were deleted here (QB
        // Track L, 2026-08-07; Track J's find): zero callers, and the
        // positional mine/others/available classification lies under
        // MultiFlex, where our slices need not start at radio index 0.

        /// <summary>
        /// Add a pan and slice.
        /// </summary>
        private enum DiagnosticSlicePurpose { None, Listenback, Loopback, OffAir }
        public bool NewSlice() => NewSlice(null);

        private bool NewSlice(LoopbackSliceResources resources, DiagnosticSlicePurpose diagnosticPurpose = DiagnosticSlicePurpose.None)
        {
            // Use model-based TotalMaxSlices (always correct) instead of theRadio.MaxSlices
            // which reports available-remaining (can be 0 at startup when profile fills all slots).
            // Subtract _pendingRemovals to account for queued-but-not-yet-processed Close() calls.
            int effectiveCount = (theRadio?.SliceList.Count ?? 0) - _pendingRemovals;
            Tracing.TraceLine($"NewSlice: effective={effectiveCount} totalMax={TotalMaxSlices}", TraceLevel.Info);
            if (theRadio == null || effectiveCount >= TotalMaxSlices) return false;

            // Capture the current RX/TX slices by IDENTITY (radio index) —
            // the new slice may insert below them and shift their positions,
            // so a stored position replayed after the add can land on the
            // wrong slice. (QB Track J)
            int myRXSliceIndex = VFOToSliceIndex(RXVFO);
            int myTXSliceIndex = VFOToSliceIndex(TXVFO);
            resources?.ExpectCreationReply();
            mySliceAdded = false; // need to know when slice added.
            // #117: an operator-requested layout is provisional and gets a
            // receipt. A check's temporary listener is not their request.
            if (diagnosticPurpose == DiagnosticSlicePurpose.None) NoteOperatorChangedSliceSet();
            else if (!SuppressSpeech && diagnosticPurpose == DiagnosticSlicePurpose.Listenback)
                ScreenReaderOutput.Speak(Lexicon.Get("audio.iq.adding_listener"), VerbosityLevel.Terse);
            // Loopback and off-air share this setup. Their one purpose-specific
            // announcement belongs to the confirmed start line, after the
            // ears exist; a progress line here was rescued after keying.
            q.Enqueue((FunctionDel)(() =>
            {
                if (resources == null) theRadio.RequestPanafall();
                else theRadio.SendReplyCommand((sequence, code, reply) => resources.CreatedPanReply(code, reply),
                    "display panafall create x=100 y=100");
                if (await(() =>
                {
                    // await both slice and panadapter.
                    if (resources != null)
                    {
                        lock (mySlices) return resources.Capture(mySlices, theRadio.PanadapterList);
                    }
                    return mySliceAdded & (MyNumPanadapters == MyNumSlices);
                }, 3000))
                {
                    if (resources?.Slice != null && diagnosticPurpose != DiagnosticSlicePurpose.None)
                        lock (_sliceSettleLock) _diagnosticSlices[resources.Slice] = diagnosticPurpose;
                    // restore VFOs by identity.
                    int rxVfo = SliceIndexToVFO(myRXSliceIndex);
                    if (rxVfo >= 0) RXVFO = rxVfo;
                    if (CanTransmit)
                    {
                        int txVfo = SliceIndexToVFO(myTXSliceIndex);
                        if (txVfo >= 0) TXVFO = txVfo;
                    }
                }
                else
                {
                    Tracing.TraceLine("NewSlice:counts don't match", TraceLevel.Error);
                }
            }));
            return true;
        }

        /// <summary>
        /// Remove a pan and slice by VFO position. Resolves the position to a
        /// Slice object immediately — the queued close targets that slice no
        /// matter how the list shifts before the queue runs. (QB Track J)
        /// </summary>
        /// <param name="id">VFO position</param>
        /// <returns>true if id valid and removable</returns>
        public bool RemoveSlice(int id)
        {
            return RemoveSlice(VFOToSlice(id));
        }

        /// <summary>
        /// Remove a pan and slice by identity. Refuses the active (RX) slice
        /// and the transmit slice.
        /// </summary>
        internal bool RemoveSlice(Slice slc)
        {
            if (slc == null) return false;
            // Can't remove the active or transmit slice.
            if ((slc == VFOToSlice(RXVFO)) | (CanTransmit & (slc == VFOToSlice(TXVFO)))) return false;

            Panadapter pan = slc.Panadapter;

            Tracing.TraceLine($"RemoveSlice:letter={slc.Letter} count={MyNumSlices}", TraceLevel.Info);
            mySliceRemoved = false;
            // #117: the release will succeed and will not survive disconnect.
            // Noted here, spoken once the set settles — so "release all extras"
            // still costs one receipt, not one per slice.
            NoteOperatorChangedSliceSet();
            _pendingRemovals++;
            q.Enqueue((FunctionDel)(() =>
            {
                slc.Close();
                pan.Close();
                _pendingRemovals--;
                if (!await(() => mySliceRemoved, 3000))
                {
                    Tracing.TraceLine("RemoveSlice:slice removal not confirmed within timeout", TraceLevel.Error);
                }
                else
                {
                    Tracing.TraceLine($"RemoveSlice:confirmed, new count={MyNumSlices}", TraceLevel.Info);
                }
            }));
            return true;
        }

        // ══ The slice vocabulary, and the provisional-change receipt ══
        //
        // Sprint 32 Track H, #58 and #117. Two separate problems that turn out
        // to share one trigger, so they share one debounce.
        //
        // #58 is the CW vocabulary Noel specified on 2026-08-19: a CENSUS of
        // "<used>/<total>" when the slice set changes, and "SL <letter> <mode>"
        // when the operator moves to a slice or changes its mode. Both formats
        // are approved copy and are not to be reworded.
        //
        // #117 is the persistence receipt. Releasing a slice succeeds, sounds
        // successful, and is silently discarded at disconnect, because THE
        // SLICES ARE NOT OURS: this application contains zero slice-creation
        // calls, and the radio restores its slice layout from its own global
        // profile on the next connect. Slice create and delete work correctly —
        // the defect is that nothing tells the operator the change is
        // provisional.
        //
        // The receipt is deliberately NOT a prompt at disconnect, for two
        // reasons that were both worked out before any code was written. First,
        // the disconnect moment is not the power-off moment: a networked radio
        // does not power down when a client leaves, and under MultiFlex another
        // operator may still be on it, so an automatic save can capture
        // somebody else's layout and overwrite a global profile with a state
        // this operator never chose. Second, an unsaved-changes prompt fires
        // whether or not anything meaningful changed, so operators learn to
        // dismiss it reflexively — and a prompt trained to be dismissed is
        // worse than no prompt, because it creates the belief that the operator
        // was asked.
        //
        // Notify where there is context; prompt only where there is a real
        // choice. This is the notification.

        /// <summary>
        /// Spoken after a provisional slice or setting change. The operator has
        /// just heard the change itself from whichever surface they used
        /// ("Slice D released, 3 active"), so this adds only what none of those
        /// surfaces could know.
        /// </summary>
        /// <remarks>
        /// <b>It must stand alone, and that is a requirement rather than a
        /// preference (#442).</b> It is queued, so an interrupt can flush it
        /// and the arbiter will SALVAGE it — re-speaking it up to
        /// <see cref="Speech.SpeechArbiter.MaxSalvages"/> times, as late as its
        /// length allows. Measured 2026-08-31: this sentence arrived 14.9
        /// seconds after its first emission, after a tune and an SWR reading.
        /// It read "This will not survive disconnect …" then, and "This"
        /// pointed at an announcement the operator had long moved past — a
        /// fragment with no referent, which is worse than silence because the
        /// operator tries to make sense of it. Written to name its own subject,
        /// a late arrival is merely redundant.
        /// <para>
        /// So: no bare demonstrative or pronoun opener here, ever.
        /// <c>ProvisionalReceiptStandsAloneTests</c> pins it.
        /// </para>
        /// </remarks>
        private static string ProvisionalSliceChangeReceipt =>
            Lexicon.Get("settings.slice.provisional_change_receipt");

        /// <summary>
        /// How long the slice set must be quiet before it counts as settled.
        /// One connect delivers several slices in a burst and
        /// <see cref="GetProfileInfo"/> can add more over the following seconds,
        /// so the timer RESTARTS on every change and fires only after the last
        /// one. That is what turns four arrivals into one census.
        /// </summary>
        private const int SliceSetSettleMs = 1500;

        private System.Threading.Timer _sliceSettleTimer;
        private readonly object _sliceSettleLock = new object();

        /// <summary>
        /// True while the slice set is churning. Suppresses the per-slice
        /// announcement, so a bulk replay produces the census and nothing else.
        /// This is what replaces the old ActiveSlice guard: the announcement is
        /// silenced because the EVENT is bulk, not because one slice was picked
        /// out of it as representative.
        /// </summary>
        private volatile bool _sliceSetChurning;

        /// <summary>
        /// Set when this application asked for the slice change, cleared when
        /// the receipt has been given. Slices arriving from the radio's own
        /// profile on connect must never produce the receipt — nothing is
        /// provisional about the layout the radio just restored.
        /// </summary>
        private volatile bool _operatorChangedSliceSet;
        private readonly Dictionary<Slice, DiagnosticSlicePurpose> _diagnosticSlices = new();
        private readonly List<(Slice Slice, bool Added, bool DiagnosticAtEvent)> _sliceSetEvents = new();

        /// <summary>
        /// #359 — one fact, two vocabularies, 60 to 94 ms apart. On a connect
        /// the census spoke "4 slices out of 4 used, slice A USB" and the full
        /// connect sentence then spoke "Connected to FLEX-8600 … 4 slices.
        /// Slice A …" effectively simultaneously. The connect sentence is the
        /// one an operator should hear (it carries model, link and slice state
        /// as one thought), so the census's SPOKEN half defers to it: armed on
        /// each power-on, consumed by the first slice-settle. The CW half is
        /// untouched — a separate channel with its own audience and its own
        /// on/off switches.
        ///
        /// The conditional that must not be flattened: when the connect
        /// sentence falls back to connection-only (slices had not populated at
        /// its 1.5 s mark), the census is the ONLY slice announcement there
        /// will be and must speak —
        /// <see cref="ConnectSentenceFellBackToConnectionOnly"/> releases it.
        /// That release can never arrive after the settle it needs to
        /// un-suppress: the fallback is taken only when no slice had arrived
        /// by the sentence's decision point, and no slice arrivals means no
        /// settle timer has been started yet.
        ///
        /// Every race here fails toward SPEAKING (a duplicate), never toward
        /// silence — a settle that beats the arming simply speaks as before.
        /// </summary>
        private volatile bool _connectSentenceOwnsFirstCensus;

        /// <summary>
        /// Arm the first-census suppression for a connect (#359). Called by
        /// the UI's power-on path immediately before it schedules the full
        /// connect sentence, so the two can never both describe the slice set.
        /// </summary>
        public void SuppressFirstSpokenCensusForConnect()
        {
            _connectSentenceOwnsFirstCensus = true;
        }

        /// <summary>
        /// The connect sentence fell back to connection-only (#359): the
        /// census it would have covered is now the only slice announcement
        /// there will be, so release it to speak.
        /// </summary>
        public void ConnectSentenceFellBackToConnectionOnly()
        {
            _connectSentenceOwnsFirstCensus = false;
        }

        /// <summary>
        /// Note that the slice set has changed and (re)start the settle timer.
        /// Called from the slice added and removed handlers.
        /// </summary>
        private void NoteSliceSetChanged(Slice slice, bool added)
        {
            _sliceSetChurning = true;
            lock (_sliceSettleLock)
            {
                _sliceSetEvents.Add((slice, added, !added && _diagnosticSlices.ContainsKey(slice)));
                if (_sliceSettleTimer == null)
                {
                    _sliceSettleTimer = new System.Threading.Timer(
                        _ => OnSliceSetSettled(), null,
                        SliceSetSettleMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    try
                    {
                        _sliceSettleTimer.Change(
                            SliceSetSettleMs, System.Threading.Timeout.Infinite);
                    }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private void OnSliceSetSettled()
        {
            _sliceSetChurning = false;
            bool operatorDid = _operatorChangedSliceSet;
            _operatorChangedSliceSet = false;
            bool diagnosticOnly;
            lock (_sliceSettleLock)
            {
                diagnosticOnly = _sliceSetEvents.Count > 0 && _sliceSetEvents.TrueForAll(e =>
                    e.DiagnosticAtEvent || (e.Added && _diagnosticSlices.ContainsKey(e.Slice)));
                foreach (var e in _sliceSetEvents)
                    if (!e.Added) _diagnosticSlices.Remove(e.Slice);
                _sliceSetEvents.Clear();
            }

            // Slices also disappear on the way out. Saying anything then is
            // noise at best and a crash at worst.
            if (Disconnecting || theRadio == null) return;

            if (diagnosticOnly && !operatorDid)
            {
                Tracing.TraceLine("Slice census suppressed: diagnostic listening slice change", TraceLevel.Info);
                return;
            }

            AnnounceSliceCensus(operatorInitiated: operatorDid);
            if (operatorDid) SpeakProvisionalSliceChangeReceipt();

            // Once the connect's replay has settled, say whether the radio
            // put the operator back where they left off (#226). After the
            // census on purpose: "here is where you are — and last time you
            // were on …" reads as one thought.
            AnnounceLastPlaceIfDifferent();
        }

        /// <summary>
        /// The census: "SL &lt;used&gt;/&lt;total&gt;" in CW. Three slices open on
        /// a four-slice radio sends "SL 3/4"; a full radio sends "SL 4/4".
        /// </summary>
        /// <remarks>
        /// Used-over-total rather than a bare free count, recorded so it is not
        /// re-litigated: THE DENOMINATOR VARIES BY MODEL — two slices on a 6300
        /// or 8400, four on a 6600 or 8600, eight on a 6700. "One free" means
        /// something very different on a 6700 than on an 8600 and forces the
        /// operator to remember which radio they are on to interpret it. "3/4"
        /// carries both numbers in one token, makes "4/4" read unmistakably as
        /// full, and leaves the free count trivially derivable.
        ///
        /// The total comes from <c>Radio.MaxSlices</c>, the radio's own report
        /// of its ceiling — NOT from <c>AvailableSlices</c>, which is remaining
        /// capacity and is the wrong number for a denominator. The one guard is
        /// for a radio that has not yet answered with its ceiling: rather than
        /// send "3/0" we fall back to <see cref="TotalMaxSlices"/>, the model
        /// table, which is right for every model listed there.
        ///
        /// The numerator matches the denominator's scope. MaxSlices is a
        /// property of the RADIO, so the count of slices in use has to be the
        /// radio's too — under MultiFlex a fraction mixing "mine" over "the
        /// radio's" would be incoherent.
        /// </remarks>
        internal void AnnounceSliceCensus(bool operatorInitiated = false)
        {
            var radio = theRadio;
            if (radio == null) return;

            int total = radio.MaxSlices > 0 ? radio.MaxSlices : TotalMaxSlices;
            int used = radio.SliceList?.Count ?? 0;
            if (total <= 0) return;

            // #359 — the first settle after a connect belongs to the full
            // connect sentence; consume the arm here, once, whatever happens
            // next. An operator-initiated change always speaks — their own
            // action's receipt must never be swallowed by a connect that
            // happens to still hold the arm.
            bool censusOwedToConnectSentence = _connectSentenceOwnsFirstCensus;
            if (censusOwedToConnectSentence) _connectSentenceOwnsFirstCensus = false;
            bool suppressSpoken = censusOwedToConnectSentence && !operatorInitiated;

            Tracing.TraceLine(
                $"AnnounceSliceCensus:{used}/{total} (radio.MaxSlices={radio.MaxSlices} "
                + $"model={TotalMaxSlices} mine={MyNumSlices} "
                + $"spokenSuppressed={suppressSpoken} operatorInitiated={operatorInitiated})",
                TraceLevel.Info);

            // SPOKEN CENSUS ADDED 2026-08-20 BY NOEL. Until now this fact
            // existed ONLY in Morse, and CW notifications are off by default —
            // so an operator who had not turned them on was never told their
            // slice count at all. The speech half is therefore NOT gated on the
            // CW settings; it stands on its own and the CW below is the extra.
            //
            // Two wordings, his: the fuller form is Chatty, and Terse gets the
            // compressed one. Same fact, and the level decides how much of the
            // sentence you have to listen to.
            //
            // Terse level (not Critical): the enum documents Terse as value
            // changes and band/mode, which is exactly what a slice count is.
            // THE ACTIVE SLICE RIDES ALONG, 2026-08-20, Noel. It says where you
            // have landed: the count alone tells you how many slices exist and
            // nothing about which one you are on or what it is doing.
            //
            // Read from the radio's own `active` slice status rather than
            // inferred, because a global profile can restore a non-A slice as
            // active — so this is real information, not a constant.
            //
            // WHAT THIS DOES NOT SOLVE, recorded so it is not miscredited later:
            // #59 was four slices on connect with slice D in FM, and slice A —
            // USB — was the active one. So an active-slice announcement would
            // have said "SL A USB" and the FM on D would have stayed exactly as
            // hidden as it was. The non-active slices remain unannounced, and
            // that gap is still open.
            Slice active = null;
            lock (mySlices)
                foreach (var s in mySlices) if (s != null && s.Active) { active = s; break; }

            string letter = active?.Letter;
            string mode = active?.DemodMode;
            bool haveActive = !string.IsNullOrEmpty(letter) && !string.IsNullOrEmpty(mode);

            if (!SuppressSpeech && !suppressSpoken)
            {
                // Chatty gets the fuller sentence AND the active slice; Terse
                // gets the count alone. A transient no-active-slice state during
                // connect drops the clause silently rather than announcing an
                // absence nobody asked about.
                string spoken;
                if (ScreenReaderOutput.CurrentVerbosity >= VerbosityLevel.Chatty)
                {
                    spoken = (used == 1 ? Lexicon.Get("settings.slice.census_chatty_one",
                        ("used", used), ("total", total)) : Lexicon.Get("settings.slice.census_chatty_many",
                        ("used", used), ("total", total)));
                    if (haveActive)
                        spoken += Lexicon.Get("settings.slice.census_active_suffix",
                            ("letter", letter), ("mode", mode));
                }
                else
                {
                    spoken = (total == 1 ? Lexicon.Get("settings.slice.census_terse_one",
                        ("used", used), ("total", total)) : Lexicon.Get("settings.slice.census_terse_many",
                        ("used", used), ("total", total)));
                }
                ScreenReaderOutput.Speak(spoken, VerbosityLevel.Terse);
            }

            if (!ScreenReaderOutput.CwNotificationsEnabled) return;
            if (!ScreenReaderOutput.CwModeAnnounceEnabled) return;
            if (ScreenReaderOutput.PlayCwText == null) return;
            // SendCwText rather than the delegate directly (#153): it is the one
            // point where CW text reaches the notifier, so it is where the
            // repeat history is recorded. Calling the delegate would still make
            // the sound and would silently leave this message unrepeatable.
            //
            // "SL" PREFIX ADDED 2026-08-20 BY NOEL. A bare "4/4" is a fraction
            // with no subject: you hear it and have to already know what was
            // being counted. Prefixing makes the census self-describing, and it
            // matches AnnounceSliceIdentity's "SL A USB" so both halves of the
            // slice vocabulary open the same way. Two characters buys a message
            // that stands on its own.
            //
            // The active slice is appended as a SECOND "SL" group rather than
            // running on — "SL 4/4 SL A USB" reads as two facts, where
            // "SL 4/4 A USB" would read as one confused one.
            //
            // Sent as ONE string, not two calls: SendCwText records a repeat
            // history entry per call (#153), so two calls would mean pressing
            // Ctrl+J, Alt+E twice to hear back a single connect. One string, one
            // entry, whole summary.
            string cw = $"SL {used}/{total}";
            if (haveActive) cw += $" SL {letter} {mode}";
            _ = ScreenReaderOutput.SendCwText(cw);
        }

        /// <summary>
        /// "SL &lt;letter&gt; &lt;mode&gt;" in CW — an identity plus a state, for
        /// a single slice the operator just arrived on or just re-moded.
        /// Silent during a bulk change, where the census speaks instead.
        /// </summary>
        // A slice the application made active on its own, to undo a diagnostic
        // arrangement, arrives without its CW identity: the operator did not
        // move, and the letter would sound like they had. Expires so a missing
        // echo cannot swallow a later real arrival on the same slice.
        private Slice _silentActivation;
        private long _silentActivationUntil;

        private void SilenceNextActivation(Slice s)
        {
            System.Threading.Volatile.Write(ref _silentActivationUntil, Environment.TickCount64 + 3000);
            System.Threading.Volatile.Write(ref _silentActivation, s);
        }

        private void AnnounceSliceIdentity(Slice s)
        {
            if (s == null) return;
            if (ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref _silentActivation, null, s), s) &&
                Environment.TickCount64 < System.Threading.Volatile.Read(ref _silentActivationUntil))
            {
                Tracing.TraceLine($"Slice {s.Index} identity not played: the application returned to it", TraceLevel.Info);
                return;
            }
            if (_sliceSetChurning) return;
            if (!ScreenReaderOutput.CwNotificationsEnabled) return;
            if (!ScreenReaderOutput.CwModeAnnounceEnabled) return;
            var play = ScreenReaderOutput.PlayCwText;
            if (play == null) return;

            string letter = s.Letter;
            string mode = s.DemodMode;
            if (string.IsNullOrEmpty(letter) || string.IsNullOrEmpty(mode)) return;

            // See AnnounceSliceCensus — SendCwText is what puts this in the
            // repeat history (#153).
            _ = ScreenReaderOutput.SendCwText($"SL {letter} {mode}");
        }

        /// <summary>
        /// Say once, after a settled operator-initiated slice change, that the
        /// change is provisional. Queued rather than interrupting: the surface
        /// the operator used has already spoken the change itself, and this is
        /// the second half of that sentence, not a replacement for it.
        /// </summary>
        private void SpeakProvisionalSliceChangeReceipt()
        {
            // A slice receipt also satisfies the once-per-epoch settings
            // receipt (#225): the sentence is identical, and hearing it twice
            // in one epoch teaches the operator to stop hearing it at all.
            // Set even when speech is suppressed — suppression is a caller
            // choice about audio, not evidence the epoch went untold.
            _settingsReceiptGivenThisEpoch = true;

            if (SuppressSpeech) return;
            ScreenReaderOutput.Speak(
                ProvisionalSliceChangeReceipt,
                Speech.SpeechIntent.Queue,
                VerbosityLevel.Terse,
                subject: Speech.SpeechSubject.ProvisionalReceipt);
        }

        /// <summary>
        /// Record that THIS application asked for the slice set to change, so
        /// the settle handler knows to give the persistence receipt. Called by
        /// <see cref="NewSlice"/> and <see cref="RemoveSlice(Slice)"/> — never
        /// by the arrival handlers, which also fire for the radio's own
        /// profile restore on connect.
        /// </summary>
        private void NoteOperatorChangedSliceSet()
        {
            _operatorChangedSliceSet = true;

            // Sprint 33 Track K. The same trigger, recorded a second time with a
            // different lifetime: _operatorChangedSliceSet is consumed by the
            // next settled census and cleared, while this one has to last until
            // the operator disconnects, because that is when the save offer asks
            // whether there was anything to offer about. Set here rather than in
            // the arrival handlers for exactly the reason the flag above is —
            // slices restored by the radio's own profile on connect are not a
            // change this operator made, and must never make the offer appear.
            OperatorChangedStationThisSession = true;
        }

        // ══ The receipt, widened past slices (Sprint 35 Track I, #225) ══
        //
        // Sprint 32 Track H built the receipt for slice changes and it is
        // right: releasing a slice sounds successful and is silently discarded
        // at the next connect, because the radio replays its global profile.
        // But slices were ONE CLASS out of many. Tune power, RF power, TX
        // settings, filter cuts, antenna selection — everything else the
        // radio restores from that profile — changed with no receipt at all.
        // Noel hit exactly this: he set tune power for a dummy-load test,
        // was never told the change was provisional, and later could not
        // reconstruct what had happened to it.
        //
        // The mechanism is the slice receipt's, extended, with two deliberate
        // differences:
        //
        // 1. ONE RECEIPT PER UNSAVED-CHANGES EPOCH, not one per settled
        //    change. Slice changes are rare acts; setting changes are the
        //    fabric of operating. A sentence spoken after every settled
        //    adjustment would become wallpaper within a session, and the
        //    receipt's own design notes say why that is worse than silence: a
        //    notification trained to be ignored creates the belief that the
        //    operator was told. The fact it carries ("unsaved changes do not
        //    survive disconnect") does not change between utterances, so it is
        //    said once — and said again only after a profile save starts a new
        //    epoch, because from then on there are new unsaved changes to be
        //    honest about. The slice receipt keeps its approved per-change
        //    behaviour; when it speaks, it also satisfies this epoch, so the
        //    two can never say the same sentence twice in a row.
        //
        // 2. AN EXPLICIT APP-INITIATED SUPPRESSION SCOPE. The slice receipt
        //    distinguishes operator from app by call site (the verbs note, the
        //    arrival handlers do not). Setting changes have no such split —
        //    the same public setter serves the operator's hotkey and the
        //    dummy-load mode's mechanical zeroing. Callers that change
        //    settings on the operator's behalf wrap the change in
        //    <see cref="AppInitiatedSettingChanges"/> so the machinery's own
        //    acts never produce a receipt claiming the operator did something.
        //
        // WHAT IS DELIBERATELY NOT INSTRUMENTED, so the next reader does not
        // "complete" the sweep and break it:
        //   - Frequency and mode. Tuning is the operating act a radio is FOR,
        //     and the come-back-where-you-left-it work (#226) owns that story.
        //   - Mutes, volumes, gains and pans (audio gain, headphone, lineout,
        //     monitor levels). Ephemeral listening state; receipting volume
        //     would fire on the first knob touch of every session.
        //   - Momentary verbs: Transmit, TxTune, record and playback.
        //   - CW keyer settings (KeyerSpeed, SidetonePitch, SidetoneGain,
        //     BreakinDelay): these write cfgData as well as the radio — the
        //     app itself persists and reapplies them, so "this will not
        //     survive disconnect" would be FALSE for them.
        //   - REM ON and network settings: radio NVRAM, genuinely persistent
        //     across disconnect. The receipt would be a lie there too.

        /// <summary>
        /// True while a settings change is the application's own act rather
        /// than the operator's. Depth-counted so nested scopes compose.
        /// </summary>
        private int _appInitiatedSettingDepth;

        private sealed class AppInitiatedSettingScope : IDisposable
        {
            private FlexBase _owner;
            public AppInitiatedSettingScope(FlexBase owner)
            {
                _owner = owner;
                System.Threading.Interlocked.Increment(ref owner._appInitiatedSettingDepth);
            }
            public void Dispose()
            {
                var o = System.Threading.Interlocked.Exchange(ref _owner, null);
                if (o != null)
                    System.Threading.Interlocked.Decrement(ref o._appInitiatedSettingDepth);
            }
        }

        /// <summary>
        /// Declare that setting changes made until the returned scope is
        /// disposed are the APPLICATION's doing, not the operator's, so they
        /// must not produce the provisional-change receipt or arm the
        /// disconnect save offer. Use around mechanical set-and-restore
        /// pairs (dummy-load zeroing, the Audio Check's power cap). Never
        /// wrap a change the operator individually asked for.
        /// </summary>
        public IDisposable AppInitiatedSettingChanges() => new AppInitiatedSettingScope(this);

        /// <summary>
        /// The operator changed a radio-persisted setting through one of this
        /// class's operator-facing setters; the argument is the setter's own
        /// name for it ("TXAntennaName", "XmitPower", "TunePower", "Vox", ...).
        /// Never raised for a write made inside
        /// <see cref="AppInitiatedSettingChanges"/>, nor while disconnected.
        /// Raised on whatever thread ran the setter. Added for the hold after
        /// a reflected-power cut (#571).
        /// </summary>
        public event Action<string> OperatorChangedRadioSetting;

        /// <summary>
        /// The APPLICATION wrote a radio-persisted setting on the operator's
        /// behalf, inside <see cref="AppInitiatedSettingChanges"/>: the
        /// setter's own name for it and the value written, as a string. Raised
        /// for the setters the hold after a reflected-power cut snapshots
        /// (TX antenna, RF power, tune power), so the radio's later ECHO of
        /// that write is not read as the operator's repair (#571, Astra B2).
        /// Raised whether or not the rig is connected — the write itself may
        /// go nowhere, but a subscriber comparing baselines needs to know it
        /// was made. Raised on whatever thread ran the setter.
        /// </summary>
        public event Action<string, string> AppInitiatedRadioSettingWritten;

        /// <summary>
        /// True while a write made inside <see cref="AppInitiatedSettingChanges"/>
        /// is nonetheless THE OPERATOR'S OWN CHOICE, carried out by the app:
        /// the Transmit menu's Dummy Load Mode item is the case (#571, Opus N1
        /// and R1). The app scope keeps the receipt and the save offer quiet,
        /// as it must; this inner scope says the change is still the
        /// operator's, so the hold after a reflected-power cut lifts on it,
        /// exactly as it lifts on the operator turning the power knob. The
        /// COMMAND declares it, never the setter, so the Audio Check's own
        /// dummy-load engage, which writes the same setter, is still the
        /// app's. Depth-counted like its parent.
        /// </summary>
        private int _operatorChoseSettingDepth;

        private sealed class OperatorChoseSettingScope : IDisposable
        {
            private FlexBase _owner;
            public OperatorChoseSettingScope(FlexBase owner)
            {
                _owner = owner;
                System.Threading.Interlocked.Increment(ref owner._operatorChoseSettingDepth);
            }
            public void Dispose()
            {
                var o = System.Threading.Interlocked.Exchange(ref _owner, null);
                if (o != null)
                    System.Threading.Interlocked.Decrement(ref o._operatorChoseSettingDepth);
            }
        }

        /// <summary>
        /// Declare that the setting changes made until the returned scope is
        /// disposed are the OPERATOR's choice, carried out by the application,
        /// even where they sit inside <see cref="AppInitiatedSettingChanges"/>.
        /// A refused press after a reflected-power cut tells the operator to
        /// change the power; Dummy Load Mode is one of the ways they do that,
        /// and it must count (Noel, 2026-10-21 20:47). Open it at the
        /// operator's COMMAND (the menu item), never inside a setter: the Audio
        /// Check writes the same <see cref="DummyLoadMode"/> setter for its
        /// own reasons, and that engage, like its power restores, must NOT be
        /// inside this scope.
        /// </summary>
        public IDisposable OperatorChoseThisChange() => new OperatorChoseSettingScope(this);

        /// <summary>
        /// One of this class's operator-facing setters wrote a radio-persisted
        /// setting: tell whoever is listening WHO wrote it. A write the
        /// operator commanded and the app carried out
        /// (<see cref="OperatorChoseThisChange"/> in force) raises
        /// <see cref="OperatorChangedRadioSetting"/> and nothing else; the
        /// application's own writes (<see cref="AppInitiatedSettingChanges"/>
        /// in force) go to <see cref="AppInitiatedRadioSettingWritten"/>; the
        /// operator's direct writes go through
        /// <see cref="NoteOperatorChangedRadioSetting"/> exactly as before.
        /// The three snapshot setters call this; every other setter still
        /// calls the operator hook directly and is unchanged.
        /// </summary>
        internal void NoteRadioSettingWritten(string what, string value)
        {
            if (_operatorChoseSettingDepth > 0)
            {
                // The operator's own command, carried out by the app: the hold
                // lifts on it (the lift signal), and the receipt and save offer
                // stay quiet (the app scope's job, unchanged).
                try { OperatorChangedRadioSetting?.Invoke(what); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("OperatorChangedRadioSetting subscriber threw: " + ex.Message, TraceLevel.Warning);
                }
                return;
            }
            if (_appInitiatedSettingDepth > 0)
            {
                try { AppInitiatedRadioSettingWritten?.Invoke(what, value ?? ""); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("AppInitiatedRadioSettingWritten subscriber threw: " + ex.Message, TraceLevel.Warning);
                }
                return;
            }
            NoteOperatorChangedRadioSetting(what);
        }

        /// <summary>
        /// How long the settings surface must be quiet before a batch counts
        /// as settled. A Settings-dialog Apply or a preset application lands
        /// several values in quick succession; the timer restarts on each, so
        /// the batch produces at most one receipt, after its last member.
        /// </summary>
        private const int SettingsReceiptSettleMs = 2500;

        private System.Threading.Timer _settingsSettleTimer;
        private readonly object _settingsSettleLock = new object();

        /// <summary>
        /// True once the provisional-change receipt (slice or settings) has
        /// been given for the current unsaved-changes epoch. An epoch starts
        /// at connect and again after each successful profile save.
        /// </summary>
        private volatile bool _settingsReceiptGivenThisEpoch;

        /// <summary>
        /// Record that the operator changed a radio-persisted setting through
        /// one of this class's operator-facing setters. Arms the disconnect
        /// save offer and (once per unsaved-changes epoch) the spoken receipt.
        /// The <paramref name="what"/> string is for the trace only.
        /// </summary>
        internal void NoteOperatorChangedRadioSetting(string what)
        {
            if (_appInitiatedSettingDepth > 0) return;
            if (!IsConnected || Disconnecting || theRadio == null) return;

            Tracing.TraceLine("NoteOperatorChangedRadioSetting:" + what, TraceLevel.Verbose);

            // The same fact, offered to anyone who needs it at the moment it
            // happens rather than at disconnect (#571): the hold after a
            // reflected-power cut lifts on the OPERATOR's change to the
            // antenna or the power, and this is the one place that already
            // knows the operator's writes from the app's own. Raised after the
            // guards above on purpose, so an app-initiated write — the hold's
            // own VOX switch among them — never reaches a subscriber.
            try { OperatorChangedRadioSetting?.Invoke(what); }
            catch (Exception ex)
            {
                Tracing.TraceLine("OperatorChangedRadioSetting subscriber threw: " + ex.Message, TraceLevel.Warning);
            }

            // Same widened meaning as the doc on the property: any
            // radio-persisted change this operator made, not just slices.
            OperatorChangedStationThisSession = true;

            if (_settingsReceiptGivenThisEpoch) return;
            lock (_settingsSettleLock)
            {
                if (_settingsSettleTimer == null)
                {
                    _settingsSettleTimer = new System.Threading.Timer(
                        _ => OnRadioSettingChangesSettled(), null,
                        SettingsReceiptSettleMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    try
                    {
                        _settingsSettleTimer.Change(
                            SettingsReceiptSettleMs, System.Threading.Timeout.Infinite);
                    }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private void OnRadioSettingChangesSettled()
        {
            if (Disconnecting || theRadio == null) return;
            if (_settingsReceiptGivenThisEpoch) return;
            _settingsReceiptGivenThisEpoch = true;

            if (SuppressSpeech) return;
            // The same sentence, and the same lexicon line, as the slice
            // receipt — one vocabulary for one fact, which is also why the
            // sentence says "changes to the radio" rather than naming slices:
            // this path fires for antennas, filters, VOX and the noise
            // reduction family too. Queued for the same reason: the surface the
            // operator used has already announced the value itself; this is the
            // second half of that thought.
            ScreenReaderOutput.Speak(
                ProvisionalSliceChangeReceipt,
                Speech.SpeechIntent.Queue,
                VerbosityLevel.Terse,
                subject: Speech.SpeechSubject.ProvisionalReceipt);
        }

        // ══ Where you left this radio (Sprint 35 Track I, #226) ══
        //
        // A normal transceiver comes back on the frequency it was switched
        // off on. A Flex comes back wherever its global profile says, so an
        // operator who tuned all evening and disconnected returns to a place
        // they left hours before the end of the session — silently, and for a
        // blind operator with nothing on screen to glance at, undetectably.
        // The connect announcement recites where you ARE; nothing says that
        // this is not where you WERE.
        //
        // This is the honest first version, and deliberately no more: JJ Flex
        // REMEMBERS the operator's place (active slice frequency and mode,
        // per radio, in the serial-keyed per-radio config) and TELLS them on
        // connect when the radio has put them somewhere else. It does not
        // retune. Restoring automatically is a real MultiFlex hazard — a
        // reconnect must never yank a slice another operator is using, and
        // #117 established the slices are not ours — so restore waits for the
        // station-state ownership audit. Telling costs nothing, is right on
        // every path, and covers the complaint most of the times it bites.
        //
        // WHEN TO SNAPSHOT, and why not only at disconnect: the sessions
        // where the place matters most are the ones that END BADLY — a
        // network drop at a remote site, a crash. So the place is recorded on
        // a quiet-period debounce while operating (a tuning sweep costs one
        // write after the hand stops, not one per click) and flushed once
        // more on a clean disconnect. A hard drop loses at most the last few
        // seconds of tuning.
        //
        // Per-operator by construction: the record lives in this install's
        // per-radio config, so two operators sharing one radio under
        // MultiFlex each come back to their own place, which no radio-side
        // profile could ever give them. The cost, accepted: an operator who
        // used SmartSDR in between gets told about their last JJ Flex
        // session, not their SmartSDR one.

        /// <summary>Quiet period after the last frequency or mode change
        /// before the place is written to disk.</summary>
        private const int PlaceWriteQuietMs = 10000;

        private readonly object _placeLock = new object();
        private System.Threading.Timer _placeWriteTimer;
        private ulong _currentPlaceFreqHz;
        private string _currentPlaceMode;
        private string _currentPlaceLetter;

        // The stored place from the LAST session, stashed on first use this
        // session — BEFORE this session's own recording can overwrite it.
        private bool _lastPlaceStashLoaded;
        private bool _lastPlaceStashKnown;
        private ulong _lastPlaceStashFreqHz;
        private string _lastPlaceStashMode;
        private string _lastPlaceStashLetter;
        private volatile bool _lastPlaceAnnounced;

        private string PlaceRadioId => theRadio?.Serial ?? _connectedSerial;

        /// <summary>
        /// Load the previous session's place once, before any recording this
        /// session touches the stored value. Safe to call repeatedly.
        /// </summary>
        private void EnsureLastPlaceStashLoaded()
        {
            if (_lastPlaceStashLoaded) return;
            lock (_placeLock)
            {
                if (_lastPlaceStashLoaded) return;
                string id = PlaceRadioId;
                if (string.IsNullOrEmpty(id)) return;
                try
                {
                    var cfg = RadioConfig.LoadForRadio(id);
                    _lastPlaceStashKnown = cfg.LastPlaceKnown;
                    _lastPlaceStashFreqHz = cfg.LastPlaceFrequencyHz;
                    _lastPlaceStashMode = cfg.LastPlaceMode;
                    _lastPlaceStashLetter = cfg.LastPlaceSliceLetter;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("EnsureLastPlaceStashLoaded:" + ex.Message,
                        TraceLevel.Warning);
                    _lastPlaceStashKnown = false;
                }
                _lastPlaceStashLoaded = true;
            }
        }

        /// <summary>
        /// Note where the operator's active slice is. Called from the slice
        /// property handlers on frequency, mode and active changes — which
        /// also fire for the radio's own profile replay on connect, and that
        /// is CORRECT here: unlike the receipt, this records where the
        /// operator IS, however they got there. The stash of the previous
        /// session is loaded first, so the replay cannot overwrite the one
        /// thing the connect announcement needs.
        /// </summary>
        private void NoteOperatorPlace(Slice s)
        {
            if (s == null || Disconnecting) return;
            string letter = s.Letter;
            string mode = s.DemodMode;
            ulong freqHz = LibFreqtoLong(s.Freq);
            if (freqHz == 0 || string.IsNullOrEmpty(mode)) return;

            EnsureLastPlaceStashLoaded();

            lock (_placeLock)
            {
                _currentPlaceFreqHz = freqHz;
                _currentPlaceMode = mode;
                _currentPlaceLetter = letter ?? "";

                if (_placeWriteTimer == null)
                {
                    _placeWriteTimer = new System.Threading.Timer(
                        _ => FlushOperatorPlace(), null,
                        PlaceWriteQuietMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    try
                    {
                        _placeWriteTimer.Change(
                            PlaceWriteQuietMs, System.Threading.Timeout.Infinite);
                    }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        /// <summary>
        /// Write the in-memory place to the per-radio config. Runs on the
        /// debounce timer and once more from <see cref="Disconnect"/>.
        /// RecordLastPlace skips unchanged values, so calling this freely
        /// costs nothing.
        /// </summary>
        private void FlushOperatorPlace()
        {
            ulong freqHz;
            string mode, letter, id;
            lock (_placeLock)
            {
                freqHz = _currentPlaceFreqHz;
                mode = _currentPlaceMode;
                letter = _currentPlaceLetter;
                id = PlaceRadioId;
            }
            if (freqHz == 0 || string.IsNullOrEmpty(mode) || string.IsNullOrEmpty(id)) return;
            RadioConfig.RecordLastPlace(id, freqHz, mode, letter);
            // The whole layout rides on the same debounce (Track G2, case 2).
            try { RecordOwnStationLayout("place flush"); }
            catch (Exception ex) { Tracing.TraceLine("RecordOwnStationLayout: " + ex.Message, TraceLevel.Warning); }
        }

        /// <summary>
        /// Once per connection, after the first settled census: if the radio
        /// has put the operator somewhere other than where they left it, say
        /// where they were. Queued after the census, so the pair reads as
        /// "here is where you are — and last time you were on …". Silence
        /// when they ARE where they left off, and on a radio never seen
        /// before: an announcement that fires every connect regardless
        /// teaches the operator to stop hearing it.
        /// </summary>
        private void AnnounceLastPlaceIfDifferent()
        {
            if (_lastPlaceAnnounced) return;
            _lastPlaceAnnounced = true;

            EnsureLastPlaceStashLoaded();
            if (!_lastPlaceStashKnown) return;
            if (SuppressSpeech) return;

            // Compare against the operator's active slice as it stands now.
            Slice active = null;
            lock (mySlices)
                foreach (var s in mySlices) if (s != null && s.Active) { active = s; break; }
            if (active == null) return;

            ulong hereHz = LibFreqtoLong(active.Freq);
            string hereMode = active.DemodMode ?? "";
            if (hereHz == _lastPlaceStashFreqHz
                && string.Equals(hereMode, _lastPlaceStashMode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            double mhz = _lastPlaceStashFreqHz / 1_000_000.0;
            ScreenReaderOutput.Speak(
                Lexicon.Get("connect.last_place",
                    ("freq", mhz.ToString("0.000###")),
                    ("mode", _lastPlaceStashMode)),
                Speech.SpeechIntent.Queue,
                VerbosityLevel.Terse);
        }

        /// <summary>
        /// true if can transmit (currently unused)
        /// </summary>
        public bool CanTransmit { get; internal set; }

        /// <summary>
        /// True when the LIVE roster shows our handle and no other (#577).
        /// </summary>
        /// <remarks>
        /// <para>Until 2026-09-21 this was an auto-property assigned once, in
        /// <c>guiClientAdded</c>, from <c>GuiClients.Count == 1</c> at the
        /// instant our own client was added, and nothing recomputed it: another
        /// client joining or leaving never touched it. It is now computed from
        /// the roster tracker on every read — membership and identity, never
        /// count alone.</para>
        /// <para>False when the verdict is Unknown as well as when others are
        /// present; callers that speak should use
        /// <see cref="OtherOperatorPresence"/> so uncertainty is named rather
        /// than announced as a person. This Boolean does NOT consult the
        /// bench-supplied authority policy; automatic shared writes go through
        /// <see cref="RosterJudgementForAutomaticWrite"/>, which does.</para>
        /// </remarks>
        public bool OnlyStation => OtherOperatorPresence == RosterVerdict.OnlyUs;

        public delegate void NoSliceErrorDel(object sender, string msg);
        /// <summary>
        /// No slice allocated to this instance.
        /// </summary>
        public event NoSliceErrorDel NoSliceError;
        private void raiseNoSliceError(string msg)
        {
            Tracing.TraceLine("raiseNoSliceError:" + msg, TraceLevel.Error);
            if (NoSliceError != null)
            {
                NoSliceError(this, msg);
            }
        }

        private bool _LocalPTT;
        /// <summary>
        /// True if local PTT, can only be set to true.
        /// </summary>
        public bool LocalPTT
        {
            get { return _LocalPTT; }
            set
            {
                if ((value != _LocalPTT) & value)
                {
                    _LocalPTT = value;
                    q.Enqueue((FunctionDel)(() => { theRadio.SetLocalPttForGuiClient(); }));
                }
            }
        }
        #endregion
    }
}
