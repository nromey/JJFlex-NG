#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.SmartLink
{
    /// <summary>What follows one method attempt's end.</summary>
    public enum SmartLinkLegNext
    {
        /// <summary>The attempt is proven: the leg succeeded.</summary>
        Done = 0,
        /// <summary>A method failure with another permitted method left: try it.</summary>
        NextMethod = 1,
        /// <summary>The SmartLink leg is over; the connect walk decides what is next.</summary>
        EndLeg = 2,
        /// <summary>The operator cancelled. Nothing is walked on.</summary>
        Stop = 3,
    }

    /// <summary>How an attempt's connect request was answered.</summary>
    public enum SmartLinkHandleAnswerKind
    {
        /// <summary>SmartLink said the radio is ready and gave a handle.</summary>
        Arrived = 0,
        /// <summary>No answer within the wait.</summary>
        NoAnswer = 1,
        /// <summary>An answer with no handle in it.</summary>
        Empty = 2,
        /// <summary>The operator cancelled during the wait.</summary>
        Cancelled = 3,
        /// <summary>Nothing was sent (the brokering session is gone, or an earlier request is unsettled).</summary>
        NotSent = 4,
    }

    /// <summary>The answer to an attempt's connect request.</summary>
    public readonly record struct SmartLinkHandleAnswer(SmartLinkHandleAnswerKind Kind, string Detail)
    {
        public static readonly SmartLinkHandleAnswer Arrived = new(SmartLinkHandleAnswerKind.Arrived, "");
    }

    /// <summary>How the open ended, as the method walk needs to know it.</summary>
    public enum SmartLinkOpenAnswerKind
    {
        /// <summary>The radio opened: its setup data arrived and it took our station name.</summary>
        Completed = 0,
        /// <summary>The operator cancelled.</summary>
        Cancelled = 1,
        /// <summary>The command connection fell during the open.</summary>
        ConnectionLost = 2,
        /// <summary>The open was stopped because its attempt failed its proof.</summary>
        StoppedByAttempt = 3,
        /// <summary>The open failed for a reason that is not the transport's (no slice, setup stalled, station name).</summary>
        Failed = 4,
    }

    /// <summary>The open's answer, with the plain reason when it failed.</summary>
    public readonly record struct SmartLinkOpenAnswer(SmartLinkOpenAnswerKind Kind, string Detail);

    /// <summary>
    /// The rules of the method walk (the tiers plan, section 4), as pure
    /// functions so each can be read and tested alone.
    /// </summary>
    public static class SmartLinkAttemptRules
    {
        /// <summary>
        /// Whether an end is a METHOD failure: the path itself did not carry
        /// the connection, so another method may. FlexLib's connect returning
        /// false (the TCP or TLS stage), a terminal loss of the command
        /// connection before proof, and no qualified UDP within the window.
        /// </summary>
        public static bool IsMethodFailure(SmartLinkAttemptEnd end) =>
            end == SmartLinkAttemptEnd.ConnectFailed
            || end == SmartLinkAttemptEnd.CommandConnectionLost
            || end == SmartLinkAttemptEnd.NoQualifiedUdp;

        /// <summary>
        /// What follows an attempt's end. A handle that did not arrive is
        /// NEVER a method failure: it ends the SmartLink leg. So a fallback to
        /// another method only ever follows an attempt that received its
        /// handle, and no request of ours is still unanswered when the next
        /// one is sent. An open that failed for a reason other than transport
        /// ends the leg too. A cancel stops everything.
        /// </summary>
        public static SmartLinkLegNext After(SmartLinkAttemptEnd end, bool hasNextMethod)
        {
            switch (end)
            {
                case SmartLinkAttemptEnd.Proven:
                    return SmartLinkLegNext.Done;
                case SmartLinkAttemptEnd.Cancelled:
                    return SmartLinkLegNext.Stop;
                case SmartLinkAttemptEnd.ConnectFailed:
                case SmartLinkAttemptEnd.CommandConnectionLost:
                case SmartLinkAttemptEnd.NoQualifiedUdp:
                    return hasNextMethod ? SmartLinkLegNext.NextMethod : SmartLinkLegNext.EndLeg;
                default:
                    return SmartLinkLegNext.EndLeg;
            }
        }

        /// <summary>
        /// The order for a reconnect (plan section 4, "Reconnects"): the
        /// method that last proved itself for this radio first, then the
        /// policy's order. It only reorders among the methods the CURRENT
        /// policy permits; a remembered method the policy no longer lists is
        /// not tried, and nothing is ever added.
        /// </summary>
        public static IReadOnlyList<SmartLinkMethod> ReconnectOrder(IReadOnlyList<SmartLinkMethod> permitted, SmartLinkMethod? lastProven)
        {
            if (permitted == null) return Array.Empty<SmartLinkMethod>();
            if (lastProven is not SmartLinkMethod first || !permitted.Contains(first) || permitted[0] == first)
                return permitted;
            var order = new List<SmartLinkMethod>(permitted.Count) { first };
            order.AddRange(permitted.Where(m => m != first));
            return order;
        }
    }

    /// <summary>
    /// Which SmartLink connect requests are outstanding, per account and
    /// radio, for this run of the app.
    /// </summary>
    /// <remarks>
    /// <para><b>What this can and cannot do.</b> SmartLink's "ready" answer
    /// carries the radio's serial and no request identifier, and no wire
    /// command cancels a connect request. So "cancel and settle" means: stop
    /// listening for that request, record it settled, and never send a second
    /// request for the same radio while one is unsettled. That is what this
    /// ledger enforces.</para>
    /// <para><b>What stays unguaranteed.</b> After a request went unanswered
    /// and was given up, a later request for the SAME radio may be answered by
    /// the first one's late "ready". Nothing on the wire tells them apart. The
    /// ledger cannot prevent that; it remembers the unanswered request so the
    /// later attempt can say, in the trace and its record, that its answer may
    /// belong to the earlier request.</para>
    /// </remarks>
    public static class SmartLinkRequestLedger
    {
        /// <summary>How long after an unanswered request a later answer for the same radio is marked as possibly its.</summary>
        public const int LateAnswerWatchMs = 60_000;

        private sealed class Entry
        {
            public bool Outstanding;
            public long UnansweredAtMs = -1;
        }

        private static readonly object _gate = new();
        private static readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        private static string Key(string? account, string? serial) => (account ?? "") + "\n" + (serial ?? "");

        /// <summary>
        /// Open a request for this account and radio. False when one is still
        /// outstanding: the caller must not send, and must settle the earlier
        /// one first.
        /// </summary>
        public static bool TryBegin(string? account, string? serial)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(Key(account, serial), out var e)) _entries[Key(account, serial)] = e = new Entry();
                if (e.Outstanding) return false;
                e.Outstanding = true;
                return true;
            }
        }

        /// <summary>
        /// The request is settled. <paramref name="answered"/> is false when it
        /// was given up with no answer, which is remembered.
        /// </summary>
        public static void Settle(string? account, string? serial, bool answered, long nowMs)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(Key(account, serial), out var e)) return;
                e.Outstanding = false;
                e.UnansweredAtMs = answered ? -1 : nowMs;
            }
        }

        /// <summary>
        /// Milliseconds since a request for this account and radio went
        /// unanswered, when that is recent enough for its late answer to be
        /// taken for a new request's; otherwise null.
        /// </summary>
        public static long? RecentUnansweredAgeMs(string? account, string? serial, long nowMs)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(Key(account, serial), out var e) || e.UnansweredAtMs < 0) return null;
                long age = nowMs - e.UnansweredAtMs;
                return age >= 0 && age < LateAnswerWatchMs ? age : null;
            }
        }

        public static bool IsOutstanding(string? account, string? serial)
        {
            lock (_gate) return _entries.TryGetValue(Key(account, serial), out var e) && e.Outstanding;
        }

        /// <summary>For tests: forget everything.</summary>
        internal static void ResetForTests()
        {
            lock (_gate) _entries.Clear();
        }
    }

    /// <summary>
    /// What the method walk needs done, by whoever owns the radio. The app's
    /// is <c>FlexBase</c>; a test's is a fake, which is how every attempt end
    /// and the step after it are driven without a radio.
    /// </summary>
    public interface ISmartLinkLegWorld
    {
        /// <summary>The clock, in milliseconds.</summary>
        long NowMs { get; }
        void Sleep(int ms);
        /// <summary>The operator asked to stop (Escape). Read at every wait.</summary>
        bool OperatorCancelled { get; }
        /// <summary>The radio's newest VITA socket right now; a new attempt's floor.</summary>
        int VitaSocketGeneration { get; }
        /// <summary>
        /// Whether the account's tier and the radio's connection choice STILL
        /// allow this method, read afresh. A method the policy stopped
        /// allowing since the leg was planned is never sent.
        /// </summary>
        bool StillPermitted(SmartLinkMethod method);
        /// <summary>Point the radio at this method and return the attempt's line for the record (its ports or punch port).</summary>
        SmartLinkAttemptRecord Prepare(SmartLinkAttempt attempt);
        /// <summary>Send this attempt's connect request and wait for its answer, under <see cref="SmartLinkAttempt.RequestToken"/>.</summary>
        SmartLinkHandleAnswer RequestHandle(SmartLinkAttempt attempt);
        /// <summary>
        /// FlexLib's connect for an attempt that FOLLOWS another on the same
        /// radio object (the next method), so it is made the way a retry is:
        /// a new station attempt, then the connect. On true the world has
        /// already told the attempt its transport
        /// (<see cref="SmartLinkAttempt.NoteTransportUp"/>).
        /// </summary>
        bool ConnectTransport(SmartLinkAttempt attempt);
        /// <summary>The open, up to but not including its commit. It must stop when the attempt fails.</summary>
        SmartLinkOpenAnswer Open(SmartLinkAttempt attempt);
        /// <summary>
        /// The open completed and the proof is not in yet: the wait for the
        /// radio's audio and data has begun. Called at most once per attempt,
        /// and not at all when the data arrived before the open finished.
        /// </summary>
        void ProofWaitBegan(SmartLinkAttempt attempt);
        /// <summary>The attempt is proven: finish the open (what must not start before proof).</summary>
        void CommitOpen(SmartLinkAttempt attempt);
        /// <summary>
        /// Close everything this attempt owns: its command connection and its
        /// VITA socket, saying goodbye for our own client first. Never another
        /// client, never a setting. Runs before the next method starts.
        /// </summary>
        void Teardown(SmartLinkAttempt attempt, string why);
        /// <summary>Say that one method failed and the next is being tried. Once per move.</summary>
        void AnnounceMove(SmartLinkMethod from, SmartLinkMethod to);
        /// <summary>Keep the record (the diagnostic reads it).</summary>
        void Store(SmartLinkConnectRecord record);
        /// <summary>The method of the attempt in flight, or null when it failed. The winner's stays.</summary>
        void MethodInFlight(SmartLinkMethod? method);
        /// <summary>An attempt was proven: remember its method for this radio's next reconnect.</summary>
        void Proven(SmartLinkAttempt attempt, SmartLinkConnectRecord record);
        void Trace(string line);
    }

    /// <summary>
    /// One SmartLink leg of a connect: the methods the policy allowed, in
    /// order, and the walk through them. The account session and the connect
    /// walk survive across attempts; each attempt owns only its own pieces
    /// (<see cref="SmartLinkAttempt"/>), and they are torn down before the
    /// next method starts. The tiers plan, section 4.
    /// </summary>
    /// <remarks>
    /// <para>Two stages, because the app runs them in two places. The CONNECT
    /// stage sends the request and makes the transport; a method that fails
    /// there (FlexLib's connect returned false) is followed at once by the
    /// next. The OPEN stage opens the radio and waits for proof; a method that
    /// fails there (no qualified UDP in the window, or the command connection
    /// lost) is torn down and followed by the next method's connect stage and
    /// open. Either way the move is said once, and nothing moves on after a
    /// cancel, after a handle that never arrived, or after an open that failed
    /// for a reason that is not the transport's.</para>
    /// </remarks>
    public sealed class SmartLinkLeg
    {
        private readonly ISmartLinkLegWorld _world;
        private readonly SmartLinkProofRules _rules;
        private readonly List<SmartLinkMethod> _methods;
        private SmartLinkConnectRecord _record;
        private int _index;
        private SmartLinkAttempt? _current;
        /// <summary>What must stay said about the current attempt's answer, whatever it ends as (a possibly late answer).</summary>
        private string _answerNote = "";

        public const int ProofPollMs = 25;

        public SmartLinkLeg(SmartLinkConnectRecord record, IReadOnlyList<SmartLinkMethod> order, ISmartLinkLegWorld world,
            SmartLinkProofRules? rules = null)
        {
            _record = record ?? throw new ArgumentNullException(nameof(record));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _rules = rules ?? SmartLinkProofRules.Default;
            _methods = new List<SmartLinkMethod>(order ?? Array.Empty<SmartLinkMethod>());
        }

        /// <summary>The record as it stands; every change has been stored.</summary>
        public SmartLinkConnectRecord Record => _record;
        public IReadOnlyList<SmartLinkMethod> Methods => _methods;
        /// <summary>The method being attempted (or last attempted); null for an empty plan.</summary>
        public SmartLinkMethod? CurrentMethod => _index < _methods.Count ? _methods[_index] : null;
        public bool HasNextMethod => _index < _methods.Count - 1;
        public SmartLinkAttempt? Current => _current;
        /// <summary>The operator cancelled during this leg. Never walked on, by this leg or the walk above it.</summary>
        public bool Cancelled { get; private set; }
        /// <summary>The leg ended without a proven attempt and will try nothing more.</summary>
        public bool Ended { get; private set; }
        /// <summary>How the newest attempt ended (Pending before any).</summary>
        public SmartLinkAttemptEnd LastEnd => _current?.End ?? SmartLinkAttemptEnd.Pending;
        /// <summary>
        /// True when the newest attempt's transport came up and the leg has
        /// not been decided: its open (and proof) is owed. Still true when
        /// that attempt ended by itself in between (its connection fell), so
        /// whoever runs the open stage gets to move on to the next method.
        /// </summary>
        public bool OwesOpen => _current is { TransportUp: true, IsProven: false } && !Ended && !Cancelled;
        /// <summary>
        /// Whether a failure with this end would be followed by another
        /// method, so whoever reports the failure knows the walk will speak.
        /// </summary>
        public bool WillMoveOnAfter(SmartLinkAttemptEnd end) =>
            !Cancelled && !_world.OperatorCancelled
            && SmartLinkAttemptRules.After(end, HasNextMethod) == SmartLinkLegNext.NextMethod
            // ...and the next method is still allowed: one the policy has
            // stopped allowing ends the leg, with no move to announce.
            && _world.StillPermitted(_methods[_index + 1]);

        /// <summary>The plain reason a SmartLink answered with no handle. One home: the leg and the radio layer both say it from here.</summary>
        public const string NoHandleInAnswer = "SmartLink returned no usable connection details";

        /// <summary>An attempt's detail, with the note about its answer kept on the end.</summary>
        private string Detail(string detail) =>
            string.IsNullOrEmpty(_answerNote) ? detail : detail + ". " + _answerNote;

        /// <summary>
        /// Send the current method's connect request and wait for its handle.
        /// True when the handle arrived. False ends the leg: a request that is
        /// not answered is never a reason to try another method.
        /// </summary>
        public bool DialCurrent()
        {
            if (Ended || Cancelled || _index >= _methods.Count) return false;
            var method = _methods[_index];

            if (_world.OperatorCancelled)
            {
                Cancelled = true;
                Ended = true;
                _world.Trace($"SmartLink leg: cancelled before {method} was sent; nothing more is tried");
                return false;
            }

            var attempt = new SmartLinkAttempt(_record.ConnectId, _record.Attempts.Count + 1, method,
                _world.VitaSocketGeneration, () => _world.NowMs, _rules);
            _current = attempt;
            _answerNote = "";

            _record = _record.WithAttempt(_world.Prepare(attempt));
            _world.Store(_record);

            var answer = _world.RequestHandle(attempt);
            switch (answer.Kind)
            {
                case SmartLinkHandleAnswerKind.Arrived:
                    attempt.SettleRequest(handleArrived: true);
                    // A note on an answer that arrived (it may have been meant
                    // for an earlier request) stays on this attempt's detail
                    // through every later state, its final end included.
                    _answerNote = answer.Detail ?? "";
                    _record = _record
                        .WithLastAttempt(a => a with
                        {
                            HandleArrived = true,
                            End = SmartLinkAttemptEnd.Dialled,
                            EndDetail = Detail("SmartLink supplied connection details; radio setup and incoming data have not yet confirmed a working connection"),
                        })
                        .WithMethodUsed(method);
                    _world.Store(_record);
                    _world.MethodInFlight(method);
                    return true;

                case SmartLinkHandleAnswerKind.Cancelled:
                    attempt.SettleRequest(handleArrived: false);
                    return EndAttempt(attempt, SmartLinkAttemptEnd.Cancelled, "cancelled while waiting for SmartLink to answer");

                case SmartLinkHandleAnswerKind.Empty:
                    attempt.SettleRequest(handleArrived: false);
                    return EndAttempt(attempt, SmartLinkAttemptEnd.NullHandle,
                        string.IsNullOrEmpty(answer.Detail) ? NoHandleInAnswer : answer.Detail);

                case SmartLinkHandleAnswerKind.NotSent:
                    attempt.SettleRequest(handleArrived: false);
                    return EndAttempt(attempt, SmartLinkAttemptEnd.NotSent,
                        string.IsNullOrEmpty(answer.Detail) ? "the connect request was not sent" : answer.Detail);

                default:
                    attempt.SettleRequest(handleArrived: false);
                    return EndAttempt(attempt, SmartLinkAttemptEnd.NoHandle,
                        string.IsNullOrEmpty(answer.Detail) ? "SmartLink did not return connection details in time" : answer.Detail);
            }
        }

        private static SmartLinkAttemptRecord NotSentLine(SmartLinkMethod method) =>
            method == SmartLinkMethod.HolePunch
                ? SmartLinkAttemptRecord.ForPunch(0, "none")
                : new SmartLinkAttemptRecord(method, -1, -1, 0, "none", false, null, null, SmartLinkAttemptEnd.Pending, "", 0);

        /// <summary>
        /// The connect stage: dial the current method if it is not dialled
        /// yet, make its transport, and on a method failure move to the next
        /// method and repeat. True when a transport is up and its open is
        /// owed. False when the leg is over (or cancelled).
        /// </summary>
        public bool RunConnectStage()
        {
            while (true)
            {
                if (Ended || Cancelled) return false;
                var attempt = _current;
                bool dialled = attempt != null && attempt.HandleArrived && !attempt.IsOver && !attempt.TransportUp;
                if (!dialled)
                {
                    if (!DialCurrent()) return false;
                    attempt = _current!;
                }

                if (_world.OperatorCancelled)
                    return EndAttempt(attempt!, SmartLinkAttemptEnd.Cancelled, "cancelled before the connection to the radio was made");

                bool up = _world.ConnectTransport(attempt!);
                if (AfterTransport(attempt!, up, out bool tryNext)) return true;
                if (!tryNext) return false;
            }
        }

        /// <summary>
        /// For an owner that makes the FIRST attempt's transport itself (the
        /// app's connect does, between the dial and here): take its result,
        /// and on a method failure carry on through the remaining methods.
        /// True when a transport is up and its open is owed.
        /// <paramref name="movedOn"/> is true when that transport belongs to a
        /// LATER method than the one the owner connected.
        /// </summary>
        public bool ContinueAfterTransport(bool up, out bool movedOn)
        {
            movedOn = false;
            var attempt = _current;
            if (attempt == null || Ended || Cancelled) return false;
            if (AfterTransport(attempt, up, out bool tryNext)) return true;
            if (!tryNext) return false;
            movedOn = true;
            return RunConnectStage();
        }

        /// <summary>
        /// One attempt's transport result. True: it is up and its open is
        /// owed. Otherwise <paramref name="tryNext"/> says whether the move to
        /// the next method has been made (and said).
        /// </summary>
        private bool AfterTransport(SmartLinkAttempt attempt, bool up, out bool tryNext)
        {
            tryNext = false;
            if (up && !attempt.IsOver)
            {
                if (!_world.OperatorCancelled) return true;
                _world.Teardown(attempt, "cancelled as the connection to the radio came up");
                EndAttempt(attempt, SmartLinkAttemptEnd.Cancelled, "cancelled as the connection to the radio came up");
                return false;
            }

            if (_world.OperatorCancelled)
            {
                _world.Teardown(attempt, "cancelled while connecting to the radio");
                EndAttempt(attempt, SmartLinkAttemptEnd.Cancelled, "cancelled while connecting to the radio");
                return false;
            }

            // FlexLib's connect returned false (or the connection fell at
            // once): a method failure. Its pieces go before anything else
            // starts.
            EndIfUndecided(attempt, SmartLinkAttemptEnd.ConnectFailed, "The TCP connection or TLS handshake could not be completed");
            _world.Teardown(attempt, "the connection to the radio could not be made");
            tryNext = MoveOnAfter(attempt);
            return false;
        }

        /// <summary>
        /// The open stage: open the radio on the current attempt and wait for
        /// its proof. A method failure here is torn down and followed by the
        /// next method's connect stage and open. True only when an attempt is
        /// PROVEN: the radio opened, and its qualified UDP arrived on that
        /// attempt's own socket and connection.
        /// </summary>
        public bool RunOpenStage()
        {
            while (true)
            {
                if (Ended || Cancelled) return false;
                var attempt = _current;
                if (attempt == null || !attempt.TransportUp || attempt.IsProven) return false;

                // An attempt that ended by itself between the stages (its
                // connection fell) has no open to run: it goes straight to
                // what follows its end.
                bool ranOpen = !attempt.IsOver;
                var open = ranOpen
                    ? _world.Open(attempt)
                    : new SmartLinkOpenAnswer(SmartLinkOpenAnswerKind.ConnectionLost, attempt.EndDetail);
                switch (open.Kind)
                {
                    case SmartLinkOpenAnswerKind.Completed:
                        attempt.NoteOpenCompleted();
                        if (!attempt.IsOver)
                        {
                            _record = _record.WithLastAttempt(a => a with { OpenSucceeded = true });
                            _world.Store(_record);
                            _world.ProofWaitBegan(attempt);
                        }
                        WaitForProof(attempt);
                        break;
                    case SmartLinkOpenAnswerKind.Cancelled:
                        EndIfUndecided(attempt, SmartLinkAttemptEnd.Cancelled, "cancelled while the radio was opening");
                        break;
                    case SmartLinkOpenAnswerKind.ConnectionLost:
                        if (!attempt.IsOver && !attempt.NoteCommandConnectionLost(attempt.Connection))
                            EndIfUndecided(attempt, SmartLinkAttemptEnd.CommandConnectionLost,
                                "the connection to the radio was made, then lost while the radio was opening");
                        break;
                    case SmartLinkOpenAnswerKind.StoppedByAttempt:
                        // The attempt failed and stopped its open; its end is
                        // already set. If it is not, the open misreported:
                        // read the clock once, then call it what it is.
                        attempt.Poll();
                        EndIfUndecided(attempt, SmartLinkAttemptEnd.OpenFailed,
                            string.IsNullOrEmpty(open.Detail) ? "the open stopped without a reason" : open.Detail);
                        break;
                    default:
                        EndIfUndecided(attempt, SmartLinkAttemptEnd.OpenFailed,
                            string.IsNullOrEmpty(open.Detail) ? "the radio did not finish opening" : open.Detail);
                        break;
                }

                if (attempt.IsProven)
                {
                    _record = _record
                        .WithLastAttempt(a => a with
                        {
                            OpenSucceeded = true,
                            QualifiedUdpAfter = attempt.QualifiedUdpAfter,
                            End = SmartLinkAttemptEnd.Proven,
                            EndDetail = Detail(attempt.EndDetail),
                        })
                        .WithMethodUsed(attempt.Method);
                    _world.Store(_record);
                    _world.MethodInFlight(attempt.Method);
                    _world.Proven(attempt, _record);
                    _world.CommitOpen(attempt);
                    return true;
                }

                // Not proven. A method failure (and a cancel) leaves nothing of
                // the attempt behind; an open that failed for its own reasons
                // is left as the open left it, for the walk above to retire.
                var end = attempt.End;
                if (SmartLinkAttemptRules.IsMethodFailure(end) || end == SmartLinkAttemptEnd.Cancelled)
                    _world.Teardown(attempt, attempt.EndDetail);
                bool opened = open.Kind == SmartLinkOpenAnswerKind.Completed;
                bool? openResult = !ranOpen ? (bool?)null : opened;
                if (!MoveOnAfter(attempt, openResult)) return false;

                // The next method: its own request, its own transport, and
                // then round again for its open.
                if (!RunConnectStage()) return false;
            }
        }

        /// <summary>Give the attempt this end unless it already has one (an attempt's first end stands).</summary>
        private static void EndIfUndecided(SmartLinkAttempt attempt, SmartLinkAttemptEnd end, string detail)
        {
            if (!attempt.IsOver) attempt.TryEnd(end, detail);
        }

        private void WaitForProof(SmartLinkAttempt attempt)
        {
            while (!attempt.IsOver)
            {
                if (_world.OperatorCancelled)
                {
                    EndIfUndecided(attempt, SmartLinkAttemptEnd.Cancelled, "cancelled while waiting for the radio's audio and data");
                    return;
                }
                attempt.Poll();
                if (!attempt.IsOver) _world.Sleep(ProofPollMs);
            }
        }

        /// <summary>
        /// File the attempt's end, and decide what follows. True only when the
        /// next method is to be tried, and then the move has been said and the
        /// index advanced.
        /// </summary>
        private bool MoveOnAfter(SmartLinkAttempt attempt, bool? openResult = null)
        {
            var end = attempt.End;
            _record = _record
                .WithLastAttempt(a => a with
                {
                    OpenSucceeded = openResult ?? a.OpenSucceeded,
                    QualifiedUdpAfter = attempt.QualifiedUdpAfter ?? a.QualifiedUdpAfter,
                    End = end,
                    EndDetail = Detail(attempt.EndDetail),
                })
                .WithMethodUsed(null);
            _world.Store(_record);
            _world.MethodInFlight(null);

            // An operator cancel wins over whatever the attempt ended as: the
            // operator asked to stop, and nothing is walked on.
            if (end == SmartLinkAttemptEnd.Cancelled || _world.OperatorCancelled)
            {
                Cancelled = true;
                Ended = true;
                _world.Trace($"SmartLink leg: {attempt} and the operator cancelled; nothing more is tried");
                return false;
            }

            var next = SmartLinkAttemptRules.After(end, HasNextMethod);
            if (next != SmartLinkLegNext.NextMethod)
            {
                Ended = true;
                _world.Trace($"SmartLink leg: {attempt} ({attempt.EndDetail}); the SmartLink leg ends here"
                    + (HasNextMethod ? ", because this end is not a failure of the method" : ", with no method left"));
                return false;
            }

            var from = _methods[_index];
            var to = _methods[_index + 1];

            // The ceiling is read again before every later method: a tier or a
            // connection choice changed mid-walk takes effect on the next
            // attempt. A method it no longer allows is never sent, and is not
            // announced as being tried.
            if (!_world.StillPermitted(to))
            {
                _record = _record.WithAttempt(NotSentLine(to) with
                {
                    End = SmartLinkAttemptEnd.NotSent,
                    EndDetail = "The current policy check did not permit this method",
                });
                _world.Store(_record);
                Ended = true;
                _world.Trace($"SmartLink leg: {attempt} ({attempt.EndDetail}); {to} is no longer allowed, so the SmartLink leg ends here");
                return false;
            }

            _world.Trace($"SmartLink leg: {attempt} ({attempt.EndDetail}); trying {to}");
            _world.AnnounceMove(from, to);
            _index++;
            return true;
        }

        /// <summary>An end that finishes the leg: file it, and report false.</summary>
        private bool EndAttempt(SmartLinkAttempt attempt, SmartLinkAttemptEnd end, string detail)
        {
            EndIfUndecided(attempt, end, detail);
            MoveOnAfter(attempt);
            // MoveOnAfter never moves on from these ends; make the leg's state plain.
            Ended = true;
            return false;
        }

        /// <summary>
        /// The connect is being abandoned from outside (the rig is being
        /// disconnected or disposed) with an attempt still undecided: give it
        /// its end so the record does not say "still going" for ever.
        /// </summary>
        public void Abandon(bool operatorCancelled, string why)
        {
            var attempt = _current;
            if (attempt == null || attempt.IsOver) { Ended = true; return; }
            attempt.TryEnd(operatorCancelled ? SmartLinkAttemptEnd.Cancelled : SmartLinkAttemptEnd.OpenFailed, why);
            _record = _record
                .WithLastAttempt(a => a with { End = attempt.End, EndDetail = Detail(attempt.EndDetail) })
                .WithMethodUsed(null);
            _world.Store(_record);
            _world.MethodInFlight(null);
            Cancelled |= operatorCancelled;
            Ended = true;
        }
    }
}
