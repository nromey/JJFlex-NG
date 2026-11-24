#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.SmartLink;

namespace Radios
{
    /// <summary>
    /// FlexBase, SmartLink method attempts: the radio layer's half of the
    /// method walk (the tiers plan, section 4; Track B). The walk itself, and
    /// every rule about what follows which end, is <see cref="SmartLinkLeg"/>;
    /// one attempt and its proof is <see cref="SmartLinkAttempt"/>. This file
    /// is only how those reach a real radio: the request through the brokering
    /// session, FlexLib's connect, the open, the teardown, and the proof's two
    /// inputs (the qualified-UDP sighting and the command connection's fall).
    /// </summary>
    /// <remarks>
    /// <para><b>What an attempt may touch.</b> Its own connect request, its own
    /// command connection and its own VITA socket. Never the account session
    /// (it survives across attempts), never another GUI client, never a radio
    /// setting. Its teardown says goodbye for THIS client's own registration
    /// and closes this rig's own socket, the same teardown the open has always
    /// used when it fails part way.</para>
    /// <para>A separate file so the hub's connect regions carry only the
    /// calls.</para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>How long one attempt waits for SmartLink to answer its connect request.</summary>
        public const int SmartLinkHandleWaitMs = 5000;

        /// <summary>The handle wait in use: the constant above. A field so a test need not wait five seconds for an answer that is not coming.</summary>
        internal int SmartLinkHandleWaitMsInUse = SmartLinkHandleWaitMs;

        /// <summary>
        /// The proof rule's numbers in use. The ruled defaults; a field so the
        /// bench can be answered by a change of value.
        /// </summary>
        internal SmartLinkProofRules SmartLinkProofRulesInUse = SmartLinkProofRules.Default;

        private SmartLinkLeg? _smartLinkLeg;
        private Radio? _smartLinkLegRadio;
        private SmartLinkLegWorld? _smartLinkLegWorld;
        /// <summary>Set around a dial that is a reconnect (<see cref="RetryConnect"/>), so its plan tries the last proven method first.</summary>
        private bool _smartLinkDialIsReconnect;
        /// <summary>"Connected to X", owed by an auto-connect until its SmartLink connection is proven.</summary>
        private string? _connectedSentenceOwedAtProof;

        /// <summary>The SmartLink leg this rig last planned, or null. For the walk above and for tests.</summary>
        internal SmartLinkLeg? SmartLinkLegInFlight => _smartLinkLeg;

        /// <summary>The SmartLink method this rig's leg attempted last, or null when it has dialled none.</summary>
        public SmartLinkMethod? SmartLinkLegMethod => _smartLinkLeg?.Current?.Method;

        /// <summary>True when the operator cancelled during this rig's SmartLink leg. A cancel is never walked on.</summary>
        public bool SmartLinkLegCancelled => _smartLinkLeg?.Cancelled == true;

        /// <summary>
        /// The method of the last SmartLink attempt this rig made, proven or
        /// not; null when it made none. What a failed connect's report reads,
        /// rather than the radio's own punch flag.
        /// </summary>
        internal SmartLinkMethod? LastSmartLinkAttemptMethod => _smartLinkLeg?.Current?.Method;

        // ── What was proven, for the next reconnect ─────────────────────────

        private static readonly ConcurrentDictionary<string, SmartLinkMethod> _provenSmartLinkMethods =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The SmartLink method that last PROVED itself for this radio in this
        /// run of the app, or null. A forced path teaches nothing, so a
        /// connect made from "Connect over SmartLink" is never remembered.
        /// </summary>
        internal static SmartLinkMethod? LastProvenSmartLinkMethod(string? serial) =>
            !string.IsNullOrEmpty(serial) && _provenSmartLinkMethods.TryGetValue(serial, out var m) ? m : null;

        internal static void RememberProvenSmartLinkMethod(string serial, SmartLinkMethod method, bool forcedPath)
        {
            if (string.IsNullOrEmpty(serial) || forcedPath) return;
            _provenSmartLinkMethods[serial] = method;
        }

        internal static void ForgetProvenSmartLinkMethodsForTests() => _provenSmartLinkMethods.Clear();

        // ── Planning ────────────────────────────────────────────────────────

        /// <summary>
        /// Make the planned methods this rig's SmartLink leg. Called by the
        /// dial once the policy has allowed at least one method.
        /// </summary>
        private SmartLinkLeg BeginSmartLinkLeg(Radio r, IWanSessionOwner session, Func<RadioConfig> profileNow,
            RadioPortReport plannedReport, SmartLinkConnectRecord record, IReadOnlyList<SmartLinkMethod> order)
        {
            // One leg at a time on a rig: an earlier one is finished with first.
            AbandonSmartLinkLeg("a new SmartLink leg is planned on this rig");
            var world = new SmartLinkLegWorld(this, r, session, profileNow, plannedReport);
            var leg = new SmartLinkLeg(record, order, world, SmartLinkProofRulesInUse);
            _smartLinkLeg = leg;
            _smartLinkLegRadio = r;
            _smartLinkLegWorld = world;
            return leg;
        }

        /// <summary>
        /// An earlier leg on this rig is finished with, whatever state it was
        /// in: an attempt still undecided gets its end (and its request, if
        /// unanswered, is no longer listened for) before a new dial plans its
        /// own. A reconnect or retry therefore never sends a request for a
        /// radio while this rig still has one unsettled.
        /// </summary>
        /// <param name="connectIsOver">
        /// True when the whole connect is being given up (the rig disconnected,
        /// or the walk leaving SmartLink for another path), so a "Connected"
        /// an auto-connect is still owed is forgotten. False for a retry of the
        /// same connect: the sentence is still owed, and is said when the retry
        /// is proven. It used to be dropped at every new dial, so an
        /// auto-connect that succeeded on its second pass never said it.
        /// </param>
        private void AbandonSmartLinkLeg(string why, bool connectIsOver = false)
        {
            if (connectIsOver) _connectedSentenceOwedAtProof = null;
            var leg = _smartLinkLeg;
            if (leg == null) return;
            _smartLinkLegWorld?.Unwatch();
            var attempt = leg.Current;
            if (attempt != null && !attempt.IsOver)
            {
                Tracing.TraceLine($"SmartLink leg: {attempt} was still undecided; ending it: {why}", TraceLevel.Info);
                leg.Abandon(_cancelRequested, why);
            }
            _smartLinkLeg = null;
            _smartLinkLegRadio = null;
            _smartLinkLegWorld = null;
        }

        /// <summary>
        /// SmartLink answered an attempt's request: keep the executed broker
        /// result (this radio object, this handle, the account whose session
        /// brokered it), never the selected account or a later list lookup.
        /// The transport that follows is attributed from it.
        /// </summary>
        private void RememberAnsweredSmartLinkRoute(Radio r, string handle, IWanSessionOwner session)
        {
            rememberCommandRoute(r, handle, session.AccountId);
        }

        // ── Ending a transport that is still being established ─────────────

        /// <summary>
        /// One attempt's transport while FlexLib is still establishing it (the
        /// TCP connect and the TLS handshake, each of which can wait fifteen
        /// seconds, twice over when TLS 1.3 is refused). It names the attempt
        /// and the radio object the establishment belongs to, so ending it can
        /// only ever end THAT one.
        /// </summary>
        private sealed class TransportEstablishment
        {
            public TransportEstablishment(SmartLinkAttempt attempt, Radio radio) { Attempt = attempt; Radio = radio; }
            public readonly SmartLinkAttempt Attempt;
            public readonly Radio Radio;
            /// <summary>FlexLib's connect has returned. Nothing may end this establishment after that.</summary>
            public bool Finished;
            /// <summary>It was ended from outside while it was still waiting.</summary>
            public bool Aborted;
            public string AbortedBecause = "";
            public CancellationTokenRegistration AttemptEnded;
        }

        private TransportEstablishment? _establishing;

        /// <summary>
        /// FlexLib's connect is about to be called for this attempt. From here
        /// until <see cref="EndTransportEstablishment"/>, the operator's Escape
        /// and the attempt's own end both stop the wait.
        /// </summary>
        private void BeginTransportEstablishment(SmartLinkAttempt attempt, Radio radio)
        {
            var establishment = new TransportEstablishment(attempt, radio);
            Volatile.Write(ref _establishing, establishment);
            // The attempt ended from outside (a new dial, the rig disconnecting).
            establishment.AttemptEnded = attempt.EndedToken.Register(
                () => QueueAbortOfTransportEstablishment(establishment, "its attempt ended"));
            // Escape pressed a moment before this began is not lost.
            if (_cancelRequested) QueueAbortOfTransportEstablishment(establishment, "the operator had already cancelled");
        }

        /// <summary>
        /// FlexLib's connect has returned. True when this establishment was
        /// ended from outside while it waited; its result is then "not up",
        /// whatever FlexLib returned in the same instant.
        /// </summary>
        private bool EndTransportEstablishment(SmartLinkAttempt attempt)
        {
            var establishment = Volatile.Read(ref _establishing);
            if (establishment == null || !ReferenceEquals(establishment.Attempt, attempt)) return false;
            bool aborted;
            // The same lock the abort holds while it closes the transport: an
            // abort either ran completely before this line, or will not run.
            lock (establishment)
            {
                establishment.Finished = true;
                aborted = establishment.Aborted;
            }
            establishment.AttemptEnded.Dispose();
            Interlocked.CompareExchange(ref _establishing, null, establishment);
            return aborted;
        }

        /// <summary>
        /// Off the caller's thread: Escape arrives on the connecting window's
        /// own thread, which must stay free, and an attempt's end can arrive
        /// under that attempt's lock.
        /// </summary>
        private void QueueAbortOfTransportEstablishment(TransportEstablishment establishment, string why) =>
            ThreadPool.QueueUserWorkItem(_ => AbortTransportEstablishment(establishment, why));

        /// <summary>
        /// End an establishment that is still waiting, by closing ITS radio
        /// object's command connection: FlexLib's own disconnect, which cancels
        /// the TLS wrapper's TCP connect and handshake waits, so the blocked
        /// connect returns at once instead of running out its fifteen seconds.
        /// Marked as our own disconnect, so the removal it raises is not read
        /// as the radio leaving the network.
        /// </summary>
        /// <remarks>
        /// <para><b>It can only ever end the establishment it was given.</b> It
        /// acts on that establishment's own radio object, under that
        /// establishment's lock, and does nothing once that establishment has
        /// finished. So an abort that runs late, after the connect returned and
        /// the next attempt began, finds its own establishment finished and
        /// touches nothing: it can never close the next attempt's transport.</para>
        /// <para><b>Nothing is left running in the background.</b> The connect
        /// stays a synchronous call on the thread that made it; ending the wait
        /// makes that call return. The TLS wrapper refuses to mark a cancelled
        /// client connected, and FlexLib refuses to initialize a transport that
        /// is no longer the current one, so a cancelled establishment cannot
        /// complete later.</para>
        /// </remarks>
        /// <returns>True when a waiting establishment was ended.</returns>
        private bool AbortTransportEstablishment(TransportEstablishment establishment, string why)
        {
            lock (establishment)
            {
                if (establishment.Finished || establishment.Aborted) return false;
                establishment.Aborted = true;
                establishment.AbortedBecause = why;
                Tracing.TraceLine(
                    $"SmartLink leg: {establishment.Attempt}: ending its transport while it is still being established: {why}",
                    TraceLevel.Info);
                Interlocked.Increment(ref _selfDisconnects);
                try { establishment.Radio.Disconnect(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"SmartLink leg: {establishment.Attempt}: ending its transport: {ex.Message}", TraceLevel.Warning);
                }
                finally { Interlocked.Decrement(ref _selfDisconnects); }
            }
            return true;
        }

        /// <summary>
        /// The operator asked to stop (<see cref="RequestCancel"/>): if a
        /// SmartLink transport is being established right now, end that wait.
        /// A no-op otherwise. Never blocks the caller.
        /// </summary>
        private void AbortTransportEstablishmentOnCancel()
        {
            var establishment = Volatile.Read(ref _establishing);
            if (establishment != null) QueueAbortOfTransportEstablishment(establishment, "the operator cancelled");
        }

        // ── The first attempt's transport, made by Connect or RetryConnect ──

        /// <summary>
        /// Just before FlexLib's connect: the SmartLink attempt whose transport
        /// it is about to make, with its proof watch subscribed (the sighting
        /// is raised once, and the socket can exist before the connect
        /// returns). Null for a local radio or when nothing was dialled.
        /// </summary>
        private SmartLinkAttempt? WatchSmartLinkAttemptBeforeTransport(SmartLinkAttempt? continuing)
        {
            var leg = _smartLinkLeg;
            var world = _smartLinkLegWorld;
            if (leg == null || world == null || !ReferenceEquals(_smartLinkLegRadio, theRadio)) return null;
            var attempt = continuing ?? leg.Current;
            if (attempt == null || attempt.IsOver || !attempt.HandleArrived || attempt.TransportUp) return null;
            world.Watch(attempt);
            // From here FlexLib's connect can wait on TCP and TLS; Escape and
            // the attempt's own end both stop that wait.
            BeginTransportEstablishment(attempt, theRadio!);
            return attempt;
        }

        /// <summary>
        /// Just after FlexLib's connect: tell the attempt its transport. For
        /// the attempt the connect itself dialled (<paramref name="continuation"/>
        /// false), a method failure walks on to the next method here, with its
        /// own request and its own transport, and <paramref name="movedOn"/>
        /// says the transport now up belongs to that later method (which has
        /// already done what a connect does after it comes up).
        /// </summary>
        private bool SmartLinkTransportResult(SmartLinkAttempt? attempt, bool up, bool continuation, out bool movedOn)
        {
            movedOn = false;
            if (attempt == null) return up;
            // An establishment that was ended while it waited is never "up".
            if (EndTransportEstablishment(attempt))
            {
                Tracing.TraceLine($"SmartLink leg: {attempt}: its transport was ended while it was being established; FlexLib's connect returned {up}", TraceLevel.Info);
                up = false;
            }
            var radio = theRadio;
            if (up && radio != null)
            {
                attempt.NoteTransportUp(radio.CurrentCommandConnection, radio.VitaSocketGeneration);
                Tracing.TraceLine(
                    $"SmartLink proof: {attempt}: transport up, socket#{attempt.SocketGeneration} (floor {attempt.SocketGenerationFloor}), " +
                    $"connection={(radio.CurrentCommandConnection?.Id.ToString("N").Substring(0, 8) ?? "none")}, " +
                    $"window {SmartLinkProofRulesInUse.WindowMs} ms",
                    TraceLevel.Info);
            }
            if (continuation) return up;
            var leg = _smartLinkLeg;
            return leg != null ? leg.ContinueAfterTransport(up, out movedOn) : up;
        }

        /// <summary>
        /// True when the last SmartLink leg on this rig ended at the request
        /// stage (no handle, an empty one, or nothing sent) rather than at the
        /// transport. What the failed-connect report needs to tell the two
        /// stories apart.
        /// </summary>
        private bool SmartLinkLegEndedAtTheRequest()
        {
            var end = _smartLinkLeg?.LastEnd;
            return end == null
                || end == SmartLinkAttemptEnd.Pending
                || end == SmartLinkAttemptEnd.NoHandle
                || end == SmartLinkAttemptEnd.NullHandle
                || end == SmartLinkAttemptEnd.NotSent;
        }

        // ── The open ────────────────────────────────────────────────────────

        /// <summary>The leg whose open (and proof) this Start owes, or null.</summary>
        private SmartLinkLeg? SmartLinkLegOwingAnOpen()
        {
            var leg = _smartLinkLeg;
            if (leg == null || !leg.OwesOpen) return null;
            return ReferenceEquals(_smartLinkLegRadio, theRadio) ? leg : null;
        }

        /// <summary>
        /// Start, for a SmartLink connect: the method walk's open stage. True
        /// only when an attempt is PROVEN. "Connected" is said after that, not
        /// before.
        /// </summary>
        private bool StartUnderSmartLinkLeg(SmartLinkLeg leg)
        {
            bool proven = leg.RunOpenStage();
            if (!proven) _smartLinkLegWorld?.Unwatch();
            if (proven)
            {
                // Said once, when it is true, and forgotten only then: a leg
                // that failed leaves it owed to the retry that follows.
                string? owed = _connectedSentenceOwedAtProof;
                _connectedSentenceOwedAtProof = null;
                // Queued, never interrupting: a move may have been said a
                // moment ago, and nothing is to be said over it.
                if (!string.IsNullOrEmpty(owed) && !SuppressSpeech)
                    ScreenReaderOutput.Speak(owed, VerbosityLevel.Critical, false);
                return true;
            }

            var end = leg.LastEnd;
            if (end == SmartLinkAttemptEnd.NoQualifiedUdp)
                LastStartFailureReason = Lexicon.Get("connect.start.no_audio_or_data");
            else if (end == SmartLinkAttemptEnd.CommandConnectionLost || end == SmartLinkAttemptEnd.ConnectFailed)
                LastStartFailureReason = Lexicon.Get("connect.start.connection_unconfirmed");
            else if (end == SmartLinkAttemptEnd.Cancelled)
                LastStartFailureReason = Lexicon.Get("connect.start.attempt_cancelled");
            else if (string.IsNullOrEmpty(LastStartFailureReason))
                LastStartFailureReason = leg.Current?.EndDetail;
            Tracing.TraceLine(
                $"start: the SmartLink leg did not open: {leg.Current} ({leg.Current?.EndDetail}); reason given: {LastStartFailureReason}",
                TraceLevel.Warning);
            return false;
        }

        /// <summary>
        /// This open's own SmartLink attempt failed its proof while the open
        /// was still running: stop the open. The same teardown as a cancel
        /// during the open, and NOT the operator's cancel: it is recorded as
        /// the proof failure it is, and the method walk moves on from it.
        /// Always returns false, the open's answer.
        /// </summary>
        private bool StopOpenForFailedAttempt(SmartLinkAttempt attempt, string where)
        {
            var end = attempt.End;
            LastStartFailureReason = end == SmartLinkAttemptEnd.CommandConnectionLost
                ? Lexicon.Get("connect.start.connection_unconfirmed")
                : Lexicon.Get("connect.start.no_audio_or_data");
            Tracing.TraceLine($"start: stopped {where} because {attempt} failed its proof ({attempt.EndDetail})", TraceLevel.Warning);
            ConnectionProfiler.Current?.RecordEvent("start_attempt_failed", new Dictionary<string, object>
            {
                { "end", end.ToString() },
                { "where", where },
                { "msSinceStartBegin", Environment.TickCount64 - _startBeginTickCount },
            });
            string why = "SmartLink attempt " + attempt.Sequence + " failed its proof";
            releaseGuiClientRegistration(why);
            teardownDisconnect(why);
            return false;
        }

        /// <summary>
        /// Whether the method walk will try another method after this open's
        /// connection was lost. When it will, the walk's own sentence is the
        /// one thing said, and the open raises no error box of its own.
        /// </summary>
        private bool SmartLinkLegWillMoveOnAfterLoss(SmartLinkAttempt? attempt)
        {
            var leg = _smartLinkLeg;
            return attempt != null && leg != null && ReferenceEquals(leg.Current, attempt)
                && leg.WillMoveOnAfter(SmartLinkAttemptEnd.CommandConnectionLost);
        }

        // ── Between paths ───────────────────────────────────────────────────

        /// <summary>
        /// The connect walk is leaving a path whose open failed for another
        /// path: close the session this rig still holds open, if any, first,
        /// so the radio never holds two sessions with this program's one
        /// client identity. The same close a Yes to the tier offer uses
        /// (<see cref="RetireSessionBeforeReplan"/>): our own goodbye and our
        /// own socket, never the operator's Disconnect, never a setting.
        /// </summary>
        public bool RetireSessionBeforeNextLeg(string where)
        {
            AbandonSmartLinkLeg(where + ": the walk is moving to another path", connectIsOver: true);
            return RetireSessionBeforeReplan(where);
        }

        // ── The world the method walk acts through ──────────────────────────

        private sealed class SmartLinkLegWorld : ISmartLinkLegWorld
        {
            private readonly FlexBase _rig;
            private readonly Radio _radio;
            private readonly IWanSessionOwner _session;
            private readonly Func<RadioConfig> _profileNow;
            private readonly RadioPortReport _plannedReport;
            private int _punchPort;
            private Action? _unwatch;

            public SmartLinkLegWorld(FlexBase rig, Radio radio, IWanSessionOwner session, Func<RadioConfig> profileNow,
                RadioPortReport plannedReport)
            {
                _rig = rig;
                _radio = radio;
                _session = session;
                _profileNow = profileNow;
                _plannedReport = plannedReport;
            }

            public long NowMs => Environment.TickCount64;
            public void Sleep(int ms) => Thread.Sleep(ms);
            public bool OperatorCancelled => _rig._cancelRequested;
            public int VitaSocketGeneration => _radio.VitaSocketGeneration;
            public void Trace(string line) => Tracing.TraceLine(line, TraceLevel.Info);
            public void Store(SmartLinkConnectRecord record) => SmartLinkConnectRecords.Store(record);
            public void MethodInFlight(SmartLinkMethod? method) => _rig.SmartLinkMethodInUse = method;

            /// <summary>
            /// What the radio advertises now: the newest list's report when one
            /// has described this radio object, else the plan's. The permission
            /// for a later method and the pair it dials are both read from
            /// this, so a list refreshed mid-walk cannot permit by one report
            /// and dial by another.
            /// </summary>
            private RadioPortReport ReportNow() =>
                _radio.AdvertisedPorts != null ? RadioPortReport.FromAdvertised(_radio.AdvertisedPorts) : _plannedReport;

            public bool StillPermitted(SmartLinkMethod method)
            {
                try
                {
                    var account = AccountWhoseTierGoverns(_session.AccountId, AccountManager.Accounts, _rig._currentAccount);
                    var report = ReportNow();
                    var input = new SmartLinkPolicyInput(
                        account?.ConnectionMode ?? SmartLinkConnectionMode.ManualPortForwardOnly,
                        account?.TierChosen ?? false,
                        _profileNow().ConnectionPreference,
                        _rig.SmartLinkPathForced,
                        report);
                    var decision = SmartLinkMethodPolicy.Decide(input);
                    bool allowed = decision.Methods.Contains(method);
                    if (!allowed)
                        Tracing.TraceLine($"SmartLink leg: {method} is no longer allowed ({decision.Describe()}); it is not sent", TraceLevel.Warning);
                    return allowed;
                }
                catch (Exception ex)
                {
                    // Not being able to read the ceiling is not permission.
                    Tracing.TraceLine($"SmartLink leg: could not re-read the policy for {method}: {ex.Message}; it is not sent", TraceLevel.Warning);
                    return false;
                }
            }

            public SmartLinkAttemptRecord Prepare(SmartLinkAttempt attempt)
            {
                var r = _radio;
                var method = attempt.Method;
                SmartLinkAttemptRecord line;
                string source = "none";
                _punchPort = 0;
                if (method == SmartLinkMethod.HolePunch)
                {
                    // Picking the punch port is the CLIENT's job, as the
                    // reference client does: a fresh random port per attempt,
                    // so a stale NAT mapping from an earlier one cannot
                    // collide. A per-radio fixed port (Settings, Radios) wins
                    // when set. The account's saved listen port is never a
                    // punch port (decision 11).
                    int fixedPort = _profileNow().FixedHolePunchPort;
                    if (fixedPort > 0) { _punchPort = fixedPort; source = "radioProfile"; }
                    else { _punchPort = System.Random.Shared.Next(25000, 65000); source = "random"; }
                    // FlexLib's connect reads RequiresHolePunch to choose the
                    // transport, so a punch must set it. Written only when it
                    // differs, which is only "Hole punch always" on a radio
                    // that advertises ports. The radio's own report is kept in
                    // AdvertisedPorts (item 29), and every reader that asks "is
                    // this session punched?" reads the method actually used.
                    if (!r.RequiresHolePunch) r.RequiresHolePunch = true;
                    // The value FlexLib actually reads at connect time.
                    r.NegotiatedHolePunchPort = _punchPort;
                    line = SmartLinkAttemptRecord.ForPunch(_punchPort, source);
                }
                else
                {
                    // A ports method dials the pair it names, which is not
                    // always the one FlexLib's parse kept (a UPnP pair beside
                    // a manual one).
                    var pair = ReportNow().PairFor(method);
                    if (r.PublicTlsPort != pair.TlsPort) r.PublicTlsPort = pair.TlsPort;
                    if (r.PublicUdpPort != pair.UdpPort) r.PublicUdpPort = pair.UdpPort;
                    if (r.RequiresHolePunch) r.RequiresHolePunch = false;
                    line = SmartLinkAttemptRecord.ForPorts(method, pair);
                }

                // What actually happened, so the UI can report the real port.
                _rig.LastHolePunchPort = _punchPort;
                Tracing.TraceLine(
                    $"sendRemoteConnect: attempt {attempt.Sequence}, {line.Describe()}, of the plan",
                    TraceLevel.Info);
                ConnectionProfiler.Current?.RecordEvent("hole_punch_port_selected", new Dictionary<string, object>
                {
                    { "method", method.ToString() },
                    { "attempt", attempt.Sequence },
                    { "holePunchPort", _punchPort },
                    { "portSource", source },
                });
                return line;
            }

            public SmartLinkHandleAnswer RequestHandle(SmartLinkAttempt attempt)
            {
                var r = _radio;
                string account = _session.AccountId ?? "";
                if (!_session.IsConnected)
                {
                    return new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.NotSent,
                        "the SmartLink session that reaches this radio is no longer connected, and no other account's session is used");
                }
                if (!SmartLinkRequestLedger.TryBegin(account, r.Serial))
                {
                    Tracing.TraceLine($"sendRemoteConnect: NOT SENT, an earlier connect request for {r.Serial} is still unsettled", TraceLevel.Warning);
                    return new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.NotSent,
                        "an earlier connect request to this radio had not been settled");
                }

                long? earlierUnanswered = SmartLinkRequestLedger.RecentUnansweredAgeMs(account, r.Serial, NowMs);
                bool answered = false;
                try
                {
                    // The request carries this attempt's own token: giving the
                    // request up stops the session listening for its answer.
                    var task = _session.ConnectToRadio(r.Serial, _punchPort, attempt.RequestToken);
                    int waitMs = _rig.SmartLinkHandleWaitMsInUse;
                    long deadline = NowMs + waitMs;
                    while (!task.Wait(25))
                    {
                        if (OperatorCancelled)
                        {
                            attempt.SettleRequest(handleArrived: false);
                            return new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.Cancelled, "");
                        }
                        if (NowMs >= deadline)
                        {
                            attempt.SettleRequest(handleArrived: false);
                            Tracing.TraceLine("sendRemoteConnect:Radio not ready for connect (timeout).", TraceLevel.Error);
                            ConnectionProfiler.Current?.RecordEvent("send_remote_connect_timeout", new Dictionary<string, object>
                            {
                                { "serial", r.Serial },
                            });
                            return new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.NoAnswer,
                                $"SmartLink did not return connection details within {Math.Max(1, waitMs / 1000)} seconds");
                        }
                    }

                    string? handle = task.Result;
                    if (string.IsNullOrEmpty(handle))
                    {
                        attempt.SettleRequest(handleArrived: false);
                        Tracing.TraceLine("sendRemoteConnect:Radio connect returned null handle.", TraceLevel.Error);
                        return new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.Empty, SmartLinkLeg.NoHandleInAnswer);
                    }

                    answered = true;
                    r.WANConnectionHandle = handle;
                    _rig.RememberAnsweredSmartLinkRoute(r, handle, _session);

                    // Refresh the per-radio profile stub on every answered WAN
                    // connect: keeps the nickname current and guarantees every
                    // radio ever connected shows up in the Settings per-radio
                    // picker with zero setup. Load/Save never throw.
                    string observedNickname = r.Nickname;
                    RadioConfig.Update(r.Serial, profileStub =>
                    {
                        if (!string.IsNullOrEmpty(observedNickname))
                            profileStub.Nickname = observedNickname;
                    });
                    ConnectionProfiler.Current?.RecordEvent("wan_connect_ready", new Dictionary<string, object>
                    {
                        { "serial", r.Serial },
                    });

                    if (earlierUnanswered is long age)
                    {
                        // Unguaranteed, and said so: "ready" names a radio, not
                        // a request, so this answer may be the earlier request's.
                        Tracing.TraceLine(
                            $"sendRemoteConnect: a connect request for {r.Serial} went unanswered {age} ms ago; this answer may belong to it, and nothing on the wire can tell",
                            TraceLevel.Warning);
                        return new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.Arrived,
                            $"An earlier request to this radio went unanswered {age / 1000} seconds before this reply; the reply may belong to that earlier request");
                    }
                    return SmartLinkHandleAnswer.Arrived;
                }
                catch (Exception ex)
                {
                    attempt.SettleRequest(handleArrived: false);
                    Tracing.TraceLine($"sendRemoteConnect: the connect request failed: {ex.Message}", TraceLevel.Error);
                    return new SmartLinkHandleAnswer(SmartLinkHandleAnswerKind.Empty, "the connect request could not be completed");
                }
                finally
                {
                    SmartLinkRequestLedger.Settle(account, r.Serial, answered, NowMs);
                }
            }

            public bool ConnectTransport(SmartLinkAttempt attempt) => _rig.retryConnectCore(attempt);

            public SmartLinkOpenAnswer Open(SmartLinkAttempt attempt)
            {
                bool opened = _rig.startOpenPhase(attempt, out var kind);
                return new SmartLinkOpenAnswer(opened ? SmartLinkOpenAnswerKind.Completed : kind, _rig.LastStartFailureReason ?? "");
            }

            public void ProofWaitBegan(SmartLinkAttempt attempt)
            {
                // For the connecting window: the open is done, and what is
                // being waited for now is the radio's audio and data.
                ConnectionProfiler.Current?.RecordEvent("smartlink_proof_wait", new Dictionary<string, object>
                {
                    { "method", attempt.Method.ToString() },
                    { "attempt", attempt.Sequence },
                    { "windowMs", _rig.SmartLinkProofRulesInUse.WindowMs },
                });
            }

            public void CommitOpen(SmartLinkAttempt attempt)
            {
                Unwatch();
                _rig.startCommit();
            }

            public void Teardown(SmartLinkAttempt attempt, string why)
            {
                Unwatch();
                var radio = _radio;
                if (!ReferenceEquals(_rig.theRadio, radio))
                {
                    Tracing.TraceLine($"SmartLink leg: {attempt}: this rig has moved to another radio object; nothing is torn down through it", TraceLevel.Info);
                    return;
                }
                bool live;
                try { live = radio.Connected; } catch { live = false; }
                bool socketLive = attempt.SocketGeneration != 0 && radio.IsCurrentVitaSocket(attempt.SocketGeneration);
                if (!live && !socketLive)
                {
                    Tracing.TraceLine($"SmartLink leg: {attempt}: nothing left to tear down (no command connection, no VITA socket)", TraceLevel.Info);
                    return;
                }
                string reason = $"SmartLink attempt {attempt.Sequence} ({attempt.Method}) torn down: {why}";
                _rig.releaseGuiClientRegistration(reason);
                _rig.teardownDisconnect(reason);
                if (attempt.SocketGeneration != 0 && radio.IsCurrentVitaSocket(attempt.SocketGeneration))
                    Tracing.TraceLine($"SmartLink leg: {attempt}: its VITA socket is STILL live after the teardown", TraceLevel.Error);
            }

            public void AnnounceMove(SmartLinkMethod from, SmartLinkMethod to)
            {
                ConnectionProfiler.Current?.RecordEvent("smartlink_method_move", new Dictionary<string, object>
                {
                    { "from", from.ToString() },
                    { "to", to.ToString() },
                });
                if (_rig.SuppressSpeech) return;
                ConnectWalkAnnouncer.SayMethodMove(from, to);
            }

            public void Proven(SmartLinkAttempt attempt, SmartLinkConnectRecord record)
            {
                RememberProvenSmartLinkMethod(record.Serial, attempt.Method, record.ForcedPath);
                Tracing.TraceLine(
                    $"SmartLink proof: {attempt}: PROVEN, qualified UDP {(long)(attempt.QualifiedUdpAfter?.TotalMilliseconds ?? -1)} ms after its socket was made"
                    + (record.ForcedPath ? "; a forced path, so nothing is remembered for the next reconnect" : ""),
                    TraceLevel.Info);
                ConnectionProfiler.Current?.RecordEvent("smartlink_attempt_proven", new Dictionary<string, object>
                {
                    { "method", attempt.Method.ToString() },
                    { "attempt", attempt.Sequence },
                    { "qualifiedUdpMs", (long)(attempt.QualifiedUdpAfter?.TotalMilliseconds ?? -1) },
                });
            }

            /// <summary>
            /// Subscribe this attempt to the two things that decide its proof:
            /// the radio's qualified-UDP sighting, and a fall of the command
            /// connection. Both carry their own identity, and the attempt
            /// refuses anything that is not its own.
            /// </summary>
            public void Watch(SmartLinkAttempt attempt)
            {
                Unwatch();
                var radio = _radio;
                Action<QualifiedUdpObservation> onUdp = obs =>
                {
                    var verdict = attempt.NoteSighting(obs.SocketGeneration, obs.Connection, obs.SinceSocketCreated,
                        radio.IsCurrentVitaSocket(obs.SocketGeneration));
                    Tracing.TraceLine(
                        $"SmartLink proof: {attempt}: sighting on socket#{obs.SocketGeneration} under connection " +
                        $"{(obs.Connection?.Id.ToString("N").Substring(0, 8) ?? "none")}, {(long)obs.SinceSocketCreated.TotalMilliseconds} ms after the socket was made: {verdict}",
                        TraceLevel.Info);
                };
                Action<CommandConnectionChanged> onConnection = report =>
                {
                    if (report.State != CommandConnectionState.Disconnected || !report.WasConnected) return;
                    if (attempt.NoteCommandConnectionLost(report.Connection))
                        Tracing.TraceLine($"SmartLink proof: {attempt}: its command connection fell before it was proven", TraceLevel.Warning);
                };
                radio.QualifiedUdpObserved += onUdp;
                radio.CommandConnectionChanged += onConnection;
                _unwatch = () =>
                {
                    radio.QualifiedUdpObserved -= onUdp;
                    radio.CommandConnectionChanged -= onConnection;
                };
            }

            public void Unwatch()
            {
                var unwatch = Interlocked.Exchange(ref _unwatch, null);
                try { unwatch?.Invoke(); }
                catch (Exception ex) { Tracing.TraceLine($"SmartLink proof: unwatch: {ex.Message}", TraceLevel.Warning); }
            }
        }
    }
}
