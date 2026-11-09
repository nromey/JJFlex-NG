#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the WAN (SmartLink) side, with its one presence intake.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        // WAN routines.
        #region WAN
        // The rig-wide connect latch (radios, wanListReceived and
        // _serverListThisCall, with ConnectListLatched and ConnectListSettled)
        // was removed in the 2026-11-06 integration pass: after #713's
        // per-call latches (ConnectLatch) no product code read it. Each call
        // waits on its own latch, and a registration query reads its own
        // call's server push.

        /// <summary>
        /// How a list reached <see cref="wanRadioListReceivedHandler"/>. Named
        /// at every call site, so a list is labelled where it is received and
        /// not reconstructed later from a flag something else can set.
        /// </summary>
        private enum WanListArrival
        {
            /// <summary>The server pushed it, through the coordinator's
            /// attributed event. The only arrival that is an answer.</summary>
            ServerPush,

            /// <summary>A held session's cached copy, replayed by
            /// <see cref="ConnectToSmartLink"/> so a new instance's bookkeeping
            /// catches up. The same data a server once sent, but nobody spoke
            /// during this call.</summary>
            ReplayOfHeldCopy,
        }

        /// <summary>
        /// Serializes list intake across sessions: with one held session per
        /// account (#259), pushes arrive concurrently on N SmartLink receive
        /// threads, and the merge below mutates shared state (myRadioList,
        /// the WAN handle map) that was only ever poked by one thread before.
        /// </summary>
        private static readonly object _wanIntakeLock = new object();

        /// <summary>
        /// The coordinator key for the account this instance's connect flow is
        /// working with — must match what <c>ConnectToSmartLink</c> passes to
        /// <c>EnsureSessionForAccount</c>, so the list-received latch is only
        /// satisfied by the account actually being waited on.
        /// </summary>

        /// <summary>
        /// One <see cref="ConnectToSmartLink"/> call's own wait: the account it
        /// asked, whether a list for that account has reached the intake since
        /// the call began, that list, and the server's own push if one came.
        /// </summary>
        /// <remarks>
        /// <para><b>Per call, never per rig (#713, repair round 2).</b> Round 1
        /// scoped the rig-wide latch to a query's account for the length of the
        /// query, so a connect running at the same time on the same rig stopped
        /// recognising its own account's list and finished on the query's; and
        /// two queries finishing out of order restored each other's key. Each
        /// call now owns one of these, registered for its duration; the intake
        /// writes every open latch whose account matches the list, and each call
        /// reads only its own. There is no rig-wide latch any more (removed in
        /// the 2026-11-06 integration pass: nothing read it).</para>
        /// <para>Every field is read and written under <c>_wanIntakeLock</c>.</para>
        /// </remarks>
        private sealed class ConnectLatch
        {
            public ConnectLatch(string account) { Account = account; }
            public readonly string Account;
            /// <summary>The call's session and its list revision when the call began; set where the latch opens.</summary>
            public string StartSessionId = "";
            public long StartRevision;
            public bool Received;
            public List<Radio> Radios;
            public SmartLinkRegistrationEvidence.AccountList? ServerPush;
        }

        /// <summary>
        /// THE rule for what completes a waiting call (#613, #713 and #798,
        /// repair round 4): a list the SERVER pushed, for the call's account
        /// (checked by the caller), on the call's own session, NEWER than the
        /// list that session held when the call began. A replay of a held copy
        /// never completes a waiting call, whoever replays it: it is not the
        /// server speaking during the call, and taking it lost the answer the
        /// server had already sent (Codex #713 B3, #613 B3-R3b). An older or
        /// equal revision is a list the call already had at its start, which
        /// it answers from itself (<see cref="TheListWasFoundAtStart"/>).
        /// </summary>
        internal static bool ListAnswersTheCall(bool serverPush,
            string startSessionId, long startRevision, string listSessionId, long listRevision)
            => serverPush
               && string.Equals(startSessionId ?? "", listSessionId ?? "", StringComparison.Ordinal)
               && listRevision > startRevision;

        /// <summary>
        /// Write <paramref name="lst"/> into every call waiting here that it
        /// answers, by <see cref="ListAnswersTheCall"/>, and return what each
        /// held before, so a list that stops being current while it is taken
        /// can be put back (<see cref="PutBackWaitingCalls"/>). Caller holds
        /// <c>_wanIntakeLock</c>.
        /// </summary>
        private List<(ConnectLatch latch, bool received, List<Radio> radios, SmartLinkRegistrationEvidence.AccountList? push)>
            AnswerWaitingCalls(string accountId, IReadOnlyList<Radio> lst, bool serverPush, WanListProvenance provenance)
        {
            var before = new List<(ConnectLatch latch, bool received, List<Radio> radios, SmartLinkRegistrationEvidence.AccountList? push)>();
            foreach (var open in _openLatches)
            {
                if (!string.Equals(open.Account, accountId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ListAnswersTheCall(serverPush,
                        open.StartSessionId, open.StartRevision, provenance.SessionId, provenance.ListRevision)) continue;
                before.Add((open, open.Received, open.Radios, open.ServerPush));
                open.Received = true;
                open.Radios = lst.ToList();
                // ListAnswersTheCall has required a server push.
                open.ServerPush = new SmartLinkRegistrationEvidence.AccountList(
                    accountId,
                    lst.Select(r => r.Serial).Where(x => !string.IsNullOrEmpty(x)).ToList(),
                    SmartLinkRegistrationEvidence.ListSource.ServerPushThisCall);
            }
            return before;
        }

        /// <summary>Undo <see cref="AnswerWaitingCalls"/>. Caller holds <c>_wanIntakeLock</c>.</summary>
        private static void PutBackWaitingCalls(
            List<(ConnectLatch latch, bool received, List<Radio> radios, SmartLinkRegistrationEvidence.AccountList? push)> before)
        {
            foreach (var (open, received, openRadios, push) in before)
            {
                open.Received = received;
                open.Radios = openRadios;
                open.ServerPush = push;
            }
        }

        /// <summary>
        /// A push, offered to the calls waiting on a rig that is NOT the
        /// display intake (#798, repair round 5, Codex B4). The one intake
        /// still does all the display work (rows, events, the ghost sweep);
        /// this only answers waiting calls, by the same rule, with the same
        /// check-write-recheck the intake uses. Without it a call on a rig
        /// that lost the intake mid-call never heard its own session's push:
        /// a connect that had always succeeded ended ConnectFailed after ten
        /// seconds, and setupRemote answered that by cycling the session.
        /// </summary>
        private void OfferPushToWaitingCalls(Radios.SmartLink.SessionRadioListEventArgs e)
        {
            if (e?.Radios == null || e.Session == null) return;
            var provenance = new WanListProvenance(e.Session, e.SessionId, e.ConnectionGeneration, e.ListRevision);
            lock (_wanIntakeLock)
            {
                if (_openLatches.Count == 0) return;
                if (!ListStillCurrent(provenance)) return;
                var before = AnswerWaitingCalls(e.AccountId ?? "", e.Radios, serverPush: true, provenance);
                if (before.Count == 0) return;
                if (!ListStillCurrent(provenance))
                {
                    PutBackWaitingCalls(before);
                    return;
                }
                Tracing.TraceLine(
                    $"presence: {e.AccountId}'s list ({provenance}) answered {before.Count} waiting call(s) on a rig that is not the intake",
                    TraceLevel.Info);
            }
        }

        /// <summary>The calls waiting now. Guarded by <c>_wanIntakeLock</c>.</summary>
        private readonly List<ConnectLatch> _openLatches = new List<ConnectLatch>();

        /// <summary>
        /// Whether this call's latch is set, read the way the connect must
        /// read it: never inside an intake, and never blocking. The intake
        /// writes the latch and re-validates it as one critical section, and
        /// a latch it later takes back must never have been seen; an intake in
        /// progress reads as "not yet". It never blocks, because the intake can
        /// be raising events into the picker, which marshals to the UI thread
        /// and waits for it.
        /// </summary>
        private bool LatchReceived(ConnectLatch latch)
        {
            if (!System.Threading.Monitor.TryEnter(_wanIntakeLock)) return false;
            try { return latch.Received; }
            finally { System.Threading.Monitor.Exit(_wanIntakeLock); }
        }

        /// <summary>
        /// Adapter from the coordinator's attributed list event. Replaces the
        /// old subscription to FlexLib's STATIC WanRadioRadioListRecieved,
        /// which could not say whose list had arrived — fatal once more than
        /// one account's session is open, because the ghost sweep treats a
        /// list as the full truth about its account and account A's list says
        /// nothing about account B's radios.
        /// </summary>
        private void sessionRadioListReceivedHandler(object sender, Radios.SmartLink.SessionRadioListEventArgs e)
        {
            wanRadioListReceivedHandler(e.AccountId ?? "", e.Radios, WanListArrival.ServerPush,
                new WanListProvenance(e.Session, e.SessionId, e.ConnectionGeneration, e.ListRevision));
        }

        /// <summary>
        /// Which list a batch of WAN rows came from: the session, and the
        /// connection generation the list was born on. Carried into the
        /// intake with every list, whichever way it arrived, and kept per
        /// account in <see cref="_wanRowsFrom"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Two decisions read it (#619, Sol's review of L5).</b> The
        /// intake asks <see cref="Radios.SmartLink.IWanSessionOwner.ListIsCurrent"/>
        /// with the generation at the moment it consumes a push, because the
        /// owner forwards outside its lock and a push can arrive here after
        /// the next connection's has. And the connect flow's shortcut — "this
        /// rig's own rows may answer" — compares the rows' provenance with
        /// the session's current list, because a rig that was not the intake
        /// when the live connection's list arrived still holds the rows it
        /// took on an earlier connection, and the session's list being live
        /// says nothing about THIS rig's rows.</para>
        /// </remarks>
        /// <para><b>And a third, since Track L8: every consumer of the
        /// intake's <see cref="RadioFound"/>.</b> The sighting carries this
        /// record out (<see cref="RigData.FromWanList"/>), and a consumer that
        /// treats the sighting as live asks <see cref="RigData.StillCurrent"/>
        /// at its own decision. Internal rather than private only so the
        /// public sighting can hold it.</para>
        internal readonly record struct WanListProvenance(
            Radios.SmartLink.IWanSessionOwner Session, string SessionId, long ConnectionGeneration, long ListRevision = 0)
        {
            /// <summary>
            /// This is the very list <paramref name="snapshot"/> describes:
            /// same session, same connection, same list revision. The
            /// connection alone is not enough, because one connection can
            /// carry more than one list (#613, repair round 4, Codex B3-R3a).
            /// </summary>
            public bool Matches(Radios.SmartLink.SessionRadioListSnapshot snapshot) =>
                string.Equals(SessionId, snapshot.SessionId, StringComparison.Ordinal)
                && ConnectionGeneration == snapshot.ConnectionGeneration
                && ListRevision == snapshot.ListRevision;

            public override string ToString() => $"session {SessionId} connection {ConnectionGeneration} list {ListRevision}";
        }

        /// <summary>
        /// Per account, the list this rig's WAN rows for that account were
        /// last taken from. Written by the intake under <see cref="_wanIntakeLock"/>
        /// for every list it processes, push or replay; read by the connect
        /// flow before it lets those rows answer in place of the server.
        /// </summary>
        private readonly Dictionary<string, WanListProvenance> _wanRowsFrom =
            new Dictionary<string, WanListProvenance>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this rig's WAN rows for <paramref name="accountEmail"/>
        /// came from exactly the list <paramref name="snapshot"/> describes.
        /// False when this rig has never taken a list for the account — rows
        /// that arrived by another route were not proven by any list.
        /// </summary>
        private bool WanRowsCameFrom(string accountEmail, Radios.SmartLink.SessionRadioListSnapshot snapshot)
        {
            lock (_wanIntakeLock)
            {
                return _wanRowsFrom.TryGetValue(accountEmail ?? "", out var from) && from.Matches(snapshot);
            }
        }

        #region the one presence intake (#386)

        // ══ ONE INTAKE, NOT N ═══════════════════════════════════════════════
        //
        // The coordinator's SessionRadioListReceived is STATIC-lifetime state:
        // one event on an app-lifetime singleton. Subscribing an INSTANCE
        // handler to it makes the event a root, so every FlexBase that ever
        // engaged presence stays alive and keeps consuming pushes — one more
        // per picker open since #382 moved the switch-on point there.
        //
        // Why it has never been visible: every consumer downstream happens to
        // be idempotent (OnRadioRemoved re-asks availability, OnRadioFound
        // updates in place, AnnounceArrival is once-per-serial), so N handlers
        // do N times the work and produce one correct outcome. That is luck
        // held in place by three unrelated decisions, not a design.
        //
        // ── THE RULE ────────────────────────────────────────────────────────
        //
        // The coordinator sees exactly ONE delegate, for the life of the app:
        // the static dispatcher below. Behind it stands a list of the rigs that
        // have engaged and not yet been disposed, most recent last, and ONLY
        // THE LAST ONE consumes. So, stated as something to follow:
        //
        //   * engaging presence makes you the intake, and whoever was stops;
        //   * Dispose takes you out of the list wherever you sit, which hands
        //     the intake back to the rig you displaced if it is still alive,
        //     and to nobody if you were the only one;
        //   * therefore exactly one rig consumes at any moment, and never a
        //     disposed one.
        //
        // ── WHY A LIST AND NOT A SINGLE SLOT ────────────────────────────────
        //
        // Because engagements NEST. The picker's Test button opens
        // ConnectionTesterDialog while the picker is still open
        // (RigSelectorDialog.TestButton_Click), and the tester builds, connects
        // and disposes a FlexBase of its own for every test it runs. With a
        // single slot cleared on Dispose, finishing a test would leave the
        // intake empty with the picker still on screen — its rows silently stop
        // updating from presence pushes, which is the state #382 exists to
        // prevent, arrived at from the other side. Popping restores the
        // picker's rig instead. A test pins this; the single-slot version fails
        // it and passes everything else, which is how it was found.
        //
        // The list is ordered rather than a stack because disposal order is not
        // guaranteed to mirror engagement order, and a stack popped out of order
        // is worse than no stack at all.
        //
        // ── WHY NOT JUST UNSUBSCRIBE IN Dispose ─────────────────────────────
        //
        // That was the tempting fix and it is the wrong one, though NOT for the
        // reason #386 gave. The task said the cancelled-picker instance is never
        // disposed; it is — wpfSelectorProc disposes it and nulls RigControl,
        // and has since Sprint 11. What is true is:
        //
        //   * Dispose is a promise made by callers, and on THIS event a broken
        //     promise used to be unrecoverable: a subscribed instance is rooted
        //     by the event, so it is never collected and its finalizer never
        //     runs. There is no safety net behind the missed call.
        //   * And it cannot reach the case where two rigs are subscribed AT
        //     ONCE, which the nested tester above produces on every run — two
        //     rigs sweeping their own myRadioList and both raising the STATIC
        //     RadioFound/RadioRemoved into the open picker. Unsubscribing in
        //     Dispose tidies that up after the test; this prevents it during.
        //
        // The invariant here holds whether or not anybody calls Dispose. Dispose
        // only decides who inherits.

        private static readonly object _presenceIntakeGate = new object();

        /// <summary>
        /// Rigs that have engaged presence and not yet been disposed, in
        /// engagement order. The LAST one is the intake. Strong references on
        /// purpose: a weak one could be collected out from under an open picker
        /// and silently stop the rows updating, and the reference costs one rig
        /// per live engagement — where the old shape retained every rig that had
        /// ever engaged AND kept them all working.
        /// </summary>
        private static readonly List<FlexBase> _presenceEngaged = new List<FlexBase>();

        private static Radios.SmartLink.SmartLinkSessionCoordinator _presenceIntakeWiredTo;

        /// <summary>
        /// The instance currently consuming presence pushes, or null when none
        /// is. At most one, ever, and never a disposed one — that pair IS the
        /// fix for #386, so it is readable to the tests that pin it.
        /// </summary>
        internal static FlexBase PresenceIntake
        {
            get
            {
                lock (_presenceIntakeGate)
                {
                    return _presenceEngaged.Count == 0
                        ? null
                        : _presenceEngaged[_presenceEngaged.Count - 1];
                }
            }
        }

        /// <summary>
        /// The only handler the coordinator's list event ever holds. Reads the
        /// current intake under the gate and releases it before dispatching —
        /// the intake's own work takes <c>_wanIntakeLock</c> and can be slow.
        /// </summary>
        private static void presenceIntakeDispatch(object sender, Radios.SmartLink.SessionRadioListEventArgs e)
        {
            FlexBase target = PresenceIntake;

            // Calls wait on the rig that made them, and a rig can lose the
            // intake while its call waits. Every engaged rig other than the
            // intake has its waiting calls answered here; the intake answers
            // its own in the full intake below (#798, repair round 5).
            FlexBase[] others;
            lock (_presenceIntakeGate)
                others = _presenceEngaged.Where(r => !ReferenceEquals(r, target)).ToArray();
            foreach (var rig in others)
            {
                try { rig.OfferPushToWaitingCalls(e); }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"presenceIntakeDispatch: offering a list to a waiting call failed: {ex.Message}", TraceLevel.Warning);
                }
            }

            if (target == null)
            {
                // Not an error: no rig exists between a teardown and the next
                // picker. Traced because "the push arrived and nobody was
                // listening" is otherwise indistinguishable from "no push
                // arrived", and those need different investigations.
                //
                // But the STATIC bank still gets the fresh objects (#402).
                // The push that follows a disconnect — the one that removes
                // our own dead session from the server's gui_client data —
                // used to land in exactly this gap and vanish, so the next
                // connect dialled the banked SNAPSHOT taken mid-session:
                // theRadio arrived pre-loaded with a ghost of our own
                // previous client (station set, client_id empty), and the
                // dedupe renamed us to dodge ourselves ("station now will be
                // k5ner1", 2026-08-29 17:58 and 18:14). Refreshing the bank
                // is not "consuming" in #386's sense — no events, no
                // instance state, no ghost sweep — just the handle map kept
                // current so a rig-less gap cannot fossilise stale data.
                Tracing.TraceLine(
                    $"presenceIntakeDispatch: list from {e?.AccountId ?? "?"} with no intake — dropped (WAN bank still refreshed)",
                    TraceLevel.Info);
                if (e?.Radios != null)
                {
                    // The list this push came from, banked with every entry so
                    // a picker opened later can ask whether it is still
                    // current (#619, Sol's review of L11) — this is the path
                    // that fills the bank for a rig that never saw the push.
                    // And the intake's one question, asked here too: a push
                    // that is no longer the session's current knowledge when
                    // it reaches this line would overwrite the entries a
                    // current list wrote with older ones.
                    var provenance = new WanListProvenance(e.Session, e.SessionId, e.ConnectionGeneration, e.ListRevision);
                    if (!ListStillCurrent(provenance))
                    {
                        Tracing.TraceLine(
                            $"presenceIntakeDispatch: list from {e.AccountId ?? "?"} ({provenance}) is no longer that session's current knowledge — the WAN bank is not refreshed from it",
                            TraceLevel.Info);
                        return;
                    }
                    foreach (var r in e.Radios)
                    {
                        RememberWanRadio(r, e.AccountId, provenance);
                        // Counted, because the roster row now reads occupancy
                        // from this bank when no intake delivered it (#394) —
                        // this line is the proof the fact survived the drop.
                        int n;
                        lock (r.GuiClientsLockObj) n = r.GuiClients.Count;
                        Tracing.TraceLine(
                            $"occupancy[no-intake bank]: {r.Serial} stations={n}",
                            TraceLevel.Info);
                    }
                }
                return;
            }

            target.sessionRadioListReceivedHandler(sender, e);
        }

        /// <summary>
        /// Become the one instance that consumes presence pushes. Idempotent,
        /// and cheap enough to call on every engage.
        /// </summary>
        private void becomeThePresenceIntake()
        {
            // Read the coordinator OUTSIDE the gate: the property takes its own
            // initialisation lock, and nesting the two would be the only place
            // in this file where a lock order exists to get wrong.
            var coordinator = Radios.SmartLink.SmartLinkServices.Coordinator;
            string note = null;

            lock (_presenceIntakeGate)
            {
                // Keyed to the coordinator INSTANCE, not a bool. SmartLinkServices
                // .Override replaces the singleton (tests, and the explicit
                // bootstrap the coordinator's own notes anticipate), and a bool
                // would leave the dispatcher wired to an object nothing pushes
                // through any more — silence that looks exactly like a radio
                // being off.
                if (!ReferenceEquals(_presenceIntakeWiredTo, coordinator))
                {
                    if (_presenceIntakeWiredTo != null)
                        _presenceIntakeWiredTo.SessionRadioListReceived -= presenceIntakeDispatch;

                    coordinator.SessionRadioListReceived += presenceIntakeDispatch;
                    _presenceIntakeWiredTo = coordinator;
                    Tracing.TraceLine("presence intake: dispatcher wired to the session coordinator", TraceLevel.Info);
                }

                int at = _presenceEngaged.IndexOf(this);
                bool alreadyLast = at >= 0 && at == _presenceEngaged.Count - 1;
                if (!alreadyLast)
                {
                    // Re-engaging MOVES this rig to the end rather than adding a
                    // second entry, so the ordinary flow — the picker engages on
                    // open, ConnectToSmartLink engages again on the same rig —
                    // cannot leave a duplicate that a later Dispose only half
                    // removes.
                    if (at >= 0) _presenceEngaged.RemoveAt(at);
                    _presenceEngaged.Add(this);
                    note = "presence intake: taken by this rig — "
                         + _presenceEngaged.Count + " rig(s) engaged, only this one consumes";
                }
            }

            // Outside the gate: tracing writes to disk, and nothing here should
            // hold a lock the SmartLink receive thread wants across an I/O.
            if (note != null) Tracing.TraceLine(note, TraceLevel.Info);
        }

        /// <summary>
        /// Leave the engaged list. Called from Dispose BEFORE anything that can
        /// throw, so a teardown that fails half way still leaves a dead rig out
        /// of the push path.
        /// </summary>
        private void resignAsThePresenceIntake()
        {
            string note = null;

            lock (_presenceIntakeGate)
            {
                if (_presenceEngaged.Remove(this))
                {
                    note = _presenceEngaged.Count == 0
                        ? "presence intake: released by the rig being disposed — nobody is consuming pushes now"
                        : "presence intake: released by the rig being disposed — handed back to the rig it displaced";
                }
            }

            if (note != null) Tracing.TraceLine(note, TraceLevel.Info);
        }

        #endregion

        /// <summary>
        /// Where a list is inside the intake, for the suite's parking hook
        /// <see cref="WanIntakeStageReached"/>.
        /// </summary>
        internal enum WanIntakeStage
        {
            /// <summary>The replay has taken its snapshot of a held session
            /// and decided to replay it, and has not yet entered the intake.
            /// Outside the intake lock.</summary>
            ReplaySnapshotTaken,

            /// <summary>The intake has asked the session whether the list is
            /// still current, been told yes, and written nothing. Inside the
            /// intake lock.</summary>
            CurrencyChecked,

            /// <summary>The intake has written the connect flow's latch and
            /// not yet re-validated it. Inside the intake lock.</summary>
            LatchWritten,

            /// <summary>The intake has re-validated the latch — its last
            /// currency check — and is about to make the display writes:
            /// the ghost sweep, the WAN bank and their RadioRemoved, the
            /// account cache, the merge and its RadioFound. Inside the intake
            /// lock. A list parked here while the connection changes is the
            /// sighting a consumer must refuse (#619, Sol's review of L7).</summary>
            Revalidated,
        }

        /// <summary>
        /// For the suite only. Raised at each <see cref="WanIntakeStage"/> on
        /// the thread carrying the list, so a test can park a list at exactly
        /// the point Sol's review of L6 named and change the session's state
        /// underneath it — a dial, a death, a disconnect — rather than
        /// approximating the order with a list delivered after the fact. Null
        /// in production and never read by anything else.
        /// </summary>
        internal Action<WanIntakeStage> WanIntakeStageReached;

        /// <summary>
        /// The one currency question the intake asks of every list, pushed or
        /// replayed: is the list, by its own generation, still the session's
        /// current knowledge right now (#619)?
        /// </summary>
        private static bool ListStillCurrent(WanListProvenance provenance) =>
            provenance.Session.ListIsCurrent(provenance.ConnectionGeneration);

        /// <returns>Whether the list was consumed. False when it was refused
        /// as no longer the session's current knowledge, or the intake
        /// failed.</returns>
        private bool wanRadioListReceivedHandler(string accountId, IReadOnlyList<Radio> lst, WanListArrival arrival, WanListProvenance provenance)
        {
            try
            {
              lock (_wanIntakeLock)
              {
                Tracing.TraceLine($"wanRadioListReceivedHandler: account={accountId} count={lst.Count} arrival={arrival} from {provenance}", TraceLevel.Info);

                // ══ IS THIS LIST STILL CURRENT? PUSH AND REPLAY ALIKE ══════
                //
                // Asked at the moment of consuming, not the moment the list
                // was accepted or snapshotted. The owner decides under its
                // lock and forwards outside it, so a push can pause between
                // the two while a dial begins; and a replay takes its
                // snapshot, releases it, and reaches this line later, when a
                // dial, a disconnect or the transport's death may already
                // have happened. Either way, every line below would treat an
                // obsolete list as the account's full current one — the
                // latch, radios, the ghost sweep, the WAN bank, the cache.
                // Until Track L7 only a push was asked; a replay was trusted
                // to its caller's snapshot (#619, Sol's review of L6).
                //
                // The owner's gate is NOT held across this work, on purpose
                // (L6a): the intake raises events into the picker and the
                // coordinator, arbitrary callbacks, one of which takes a lock
                // of its own. So the question is asked twice around the latch:
                //
                //   1. here, the last point before anything is written;
                //   2. again straight AFTER the latch is written, still under
                //      this lock. If the answer has changed, the latch is put
                //      back exactly as it was and nothing else is written.
                //
                // The second question is what closes the window between the
                // first and the write. A latch that survives it was written
                // while its list was current: the re-validation came after
                // the write, so any dial, disconnect or death that follows is
                // later than the write, and the latch then holds what a list
                // consumed an instant before that change would hold. A call
                // reads its latch through LatchReceived, which never looks
                // inside an intake, so a latch taken back at step 2 is never
                // seen.
                //
                // Step 1 is subsumed by step 2 for correctness: removing it
                // turns no test red, measured on Track L7. It stays because it
                // is the ruled check, and because it keeps a list that was
                // already stale when it arrived — the common case, a late
                // callback or a replay after a dial — from touching the latch
                // at all, so the take-back runs only for the narrow race.
                //
                // THE WINDOW AFTER STEP 2, AND WHO CLOSES IT (#619, Sol's
                // review of L7). Between step 2 and the display writes that
                // follow it — the ghost sweep, the WAN bank and their
                // RadioRemoved, the account-list cache, the merge and its RadioFound,
                // and the rows' provenance — a dial, disconnect or death can
                // land, and those writes then happen after it. Track L7
                // argued nothing false could result; Sol traced RadioFound
                // one consumer further and found the picker treating it as a
                // FRESH LIVE SIGHTING: a live-row rewrite, the "radios have
                // arrived" close of the connecting window, an arrival
                // announcement. Three rounds each closed one consumer and
                // missed the next, so the rule now applies to EVERY consumer,
                // all the way out:
                //
                //   * The STATE these writes leave is what a consumption
                //     finishing at step 2 would have left — this lock admits
                //     no newer list in between, and the next connection's
                //     list replaces it under the same lock. A session keeps
                //     its list across a drop for display by design, so rows
                //     describing the last thing the server said are what any
                //     drop leaves. OwnRowsMayAnswerTheConnect compares the
                //     rows' recorded provenance with the session's snapshot
                //     when it asks, and is told no.
                //   * The EVENTS are another matter, because a consumer can
                //     treat one as news about NOW. So every RadioFound raised
                //     from here carries this list's provenance
                //     (RigData.FromWanList), and a consumer that treats the
                //     sighting as live asks RigData.StillCurrent() at its own
                //     decision and refuses when the answer is no — exactly
                //     what this intake would have done had the change landed
                //     an instant before step 2. The picker and the discovering
                //     window ask; the adapter in globals.vb forwards the
                //     sighting untouched; the bench tool records a historical
                //     "seen" and decides nothing from it.
                //   * RadioRemoved from the ghost sweep names a radio the
                //     server's list omitted at a moment that list was current.
                //     Its one consumer re-asks the rig's availability at its
                //     own decision and never trusts the event's content.
                //
                // These writes raise events and cannot be taken back, which
                // is why they are not bracketed like the latch: the provenance
                // travelling with the event is what stands in for the
                // bracket. Stage Revalidated below is where the suite parks a
                // list to hold this ordering exactly.
                if (!ListStillCurrent(provenance))
                {
                    Tracing.TraceLine(
                        $"wanRadioListReceivedHandler: {arrival} list of {lst.Count} radio(s) for {accountId} from {provenance} is no longer that session's current knowledge — a newer connection has been dialed, or that one has been closed, has died, or the session is going away — so it is not consumed",
                        TraceLevel.Info);
                    return false;
                }
                WanIntakeStageReached?.Invoke(WanIntakeStage.CurrencyChecked);

                // Each call waiting on THIS account gets the list on its own
                // latch (#713, repair round 2), put back below with the rest
                // if the list stops being current while it is taken; and only
                // if the list answers that call, by the one rule
                // (ListAnswersTheCall, repair round 4).
                var latchesBefore = AnswerWaitingCalls(accountId, lst, arrival == WanListArrival.ServerPush, provenance);
                WanIntakeStageReached?.Invoke(WanIntakeStage.LatchWritten);

                // Step 2: the same question, after the write. See above.
                if (!ListStillCurrent(provenance))
                {
                    PutBackWaitingCalls(latchesBefore);
                    Tracing.TraceLine(
                        $"wanRadioListReceivedHandler: {arrival} list of {lst.Count} radio(s) for {accountId} from {provenance} stopped being that session's current knowledge while it was being taken — a dial, a disconnect or the transport's death landed between the check and the latch — so the latch is put back as it was and nothing else is written",
                        TraceLevel.Info);
                    return false;
                }
                WanIntakeStageReached?.Invoke(WanIntakeStage.Revalidated);

                // Ghost sweep, scoped to THIS account: the list is the
                // server's FULL current list for the account that sent it, so
                // any WAN radio attributed to that account and absent from it
                // has gone offline (or left the account). Radios attributed to
                // OTHER accounts are untouched — their own sessions vouch for
                // them. Unattributed WAN radios ("" — pre-attribution arrivals)
                // are swept by every list, matching the old whole-map behavior.
                var freshSerials = new HashSet<string>(lst.Select(x => x.Serial), StringComparer.OrdinalIgnoreCase);
                var gone = myRadioList.Where(x =>
                    x.IsWan && !freshSerials.Contains(x.Serial)
                    && WanRadioBelongsToAccount(x.Serial, accountId)).ToList();
                foreach (Radio g in gone)
                {
                    Tracing.TraceLine($"wanRadioListReceivedHandler: WAN radio {g.Serial} ({g.Nickname}) absent from {accountId}'s fresh list — removing", TraceLevel.Info);
                    myRadioList.Remove(g);
                }

                // The WAN identity of every listed radio, banked BEFORE the merge
                // loop below folds the server's fields into an already-known LAN
                // object and discards the WAN one. Dual-homing's path choice
                // connects through these. Also drop handles for radios that left
                // the list — those serials are dual-homed no longer. Same scope
                // rule as the ghost sweep: only this account's handles.
                var bankGone = new List<(string Serial, string Name)>();
                lock (_wanRadiosLock)
                {
                    foreach (var stale in _wanRadiosBySerial
                        .Where(kv => !freshSerials.Contains(kv.Key)
                            && (string.IsNullOrEmpty(kv.Value.AccountId)
                                || string.Equals(kv.Value.AccountId, accountId, StringComparison.OrdinalIgnoreCase)))
                        .Select(kv => (kv.Key, kv.Value.Radio?.Nickname ?? "")).ToList())
                    {
                        _wanRadiosBySerial.Remove(stale.Key);
                        bankGone.Add((stale.Key, stale.Item2));
                    }
                }

                // ══ EVERY REMOVAL IS RAISED ONLY NOW, AFTER BOTH SOURCES ══════
                //
                // RadioRemoved's consumer re-asks RadioAvailability inside the
                // event, synchronously (the picker's Dispatcher.Invoke), and
                // RadioAvailability reads TWO sources: myRadioList for the
                // local half and the WAN bank for the SmartLink half. So the
                // event is raised only after the radio has left both. Until
                // repair round one of #1008 the sweep raised its removal
                // straight after myRadioList.Remove, while the bank still held
                // the radio: a SmartLink-only radio that went away under a
                // connected session was re-asked, read wan=true with a sighting
                // from this same connection (still current), stayed live, and
                // its row kept reading online. The bank was cleared a moment
                // later and nothing told the picker again.
                //
                // One removal per serial. The sweep's radios first, then a
                // radio this list dropped whose row the sweep could not see:
                // the dual-homed radio, held in myRadioList as its LAN object
                // (not IsWan), or kept on SmartLink alone by the bank after its
                // LAN half left. Its SmartLink half was the bank entry just
                // removed, so after a cable pull the row read "on SmartLink,
                // online" for minutes after the server had said the radio was
                // gone (#1008). The picker re-asks the radio's paths at its own
                // decision, so a radio still on the local network keeps its row
                // and loses only the SmartLink half, and a radio this list still
                // carries is in neither set and is never told it left.
                foreach (Radio g in gone)
                    RaiseRadioRemoved(this, g.Serial, g.Nickname ?? "");
                var sweptSerials = new HashSet<string>(gone.Select(g => g.Serial), StringComparer.OrdinalIgnoreCase);
                foreach (var (serial, name) in bankGone)
                {
                    if (sweptSerials.Contains(serial)) continue;
                    Tracing.TraceLine($"wanRadioListReceivedHandler: {serial} ({name}) absent from {accountId}'s fresh list — its SmartLink half is gone; telling the list", TraceLevel.Info);
                    RaiseRadioRemoved(this, serial, name);
                }
                foreach (Radio w in lst) RememberWanRadio(w, accountId, provenance);

                // Fast paint for next time: this account's radio list, on disk,
                // so the selector can speak the account's radios the instant it
                // opens instead of after a TLS round trip. Display only — see
                // RecordAccountRadioList, nothing connects from it. Attributed
                // to the account whose session DELIVERED the list — recording
                // a push from account B under the current account's email was
                // exactly the cross-account cache pollution #259 fights.
                try
                {
                    if (!string.IsNullOrWhiteSpace(accountId) && accountId.Contains('@'))
                        GetRadioConnectionCache().RecordAccountRadioList(accountId, lst.ToList());
                }
                catch (Exception cacheEx)
                {
                    Tracing.TraceLine($"wanRadioListReceivedHandler: account list cache failed: {cacheEx.Message}", TraceLevel.Warning);
                }

                foreach (Radio r in lst)
                {
                    // The list this row now describes, recorded before the
                    // sighting is raised, so a replay of the row asks this
                    // list whether it is still current (#619, Sol's review
                    // of L8). Recorded for the merge branch too: a row the
                    // intake adopted or refreshed was described by this list.
                    if (!string.IsNullOrWhiteSpace(r.Serial))
                        lock (_wanRowFromLock) { _wanRowFrom[r.Serial] = provenance; }

                    Radio oldRadio = findRadioInAPI(r.Serial);

                    // The radio we are CONNECTED to (or connecting to) may not
                    // be in this instance's myRadioList at all: findRadioForConnect
                    // can resolve it from the static WAN bank or the disk cache,
                    // and a fresh rig's list starts empty (#402, trace 2026-08-29
                    // 18:18 — "radioAddedHandler:1315-..." DURING the station-name
                    // wait). Taking the add-branch then splits the world: the
                    // pushed object joins myRadioList, every later push merges
                    // into IT, and theRadio — the object the connect flow and the
                    // station-name wait are actually reading — never hears a list
                    // again. The silent GuiClients merge below is the rescue
                    // channel that satisfied the wait in every successful field
                    // trace; adopt theRadio into the list so it stays connected
                    // to that channel.
                    var liveRadio = theRadio; // instance field; teardown can null it under us
                    if (oldRadio == null && liveRadio != null
                        && string.Equals(liveRadio.Serial, r.Serial, StringComparison.OrdinalIgnoreCase))
                    {
                        Tracing.TraceLine($"wanRadioListReceivedHandler: {r.Serial} is the connected radio but was absent from myRadioList — adopting it so list pushes merge into the live object", TraceLevel.Info);
                        myRadioList.Add(liveRadio);
                        oldRadio = liveRadio;
                    }

                    if (oldRadio == null)
                    {
                        // In v4 API the helper is private; directly raise our local handler.
                        radioAddedHandler(r, provenance);
                    }
                    else
                    {
                        // Radio already in myRadioList — update fields and re-raise RadioFound
                        // so the RigSelector dialog sees the radio on reconnect attempts.
                        // Without this, the second SmartLink discovery after disconnect
                        // silently updates the existing entry and the ConnectingForm never closes.
                        //
                        // This branch used to `break`, which abandoned the rest of
                        // the list the moment one radio was already known — with
                        // two SmartLink radios the second never got a RadioFound,
                        // and a dual-homed radio (always already known, because
                        // LAN found it first) killed the loop on the first
                        // iteration. `continue` is what was meant.
                        UpdateRadioDiscoveryFields(r, oldRadio, accountId);
                        RaiseRadioFound(null, BuildRigData(oldRadio, provenance));
                    }
                }

                // The rows for this account now describe THIS list, whichever
                // way it arrived. The connect flow compares this with the
                // session's snapshot before it lets the rows stand in for the
                // server's list (#619).
                _wanRowsFrom[accountId ?? ""] = provenance;
                return true;
              } // _wanIntakeLock
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("wanRadioListReceivedHandler:exception:" + ex.Message, TraceLevel.Error);
                return false;
            }
        }
        /// <summary>
        /// True on the thread that is merging a SmartLink list into a radio
        /// object, for the whole of the vendor's <c>UpdateGuiClientsList</c>,
        /// which raises GUIClientAdded/Removed synchronously on that thread.
        /// The handlers read it to know the raise is a list describing the
        /// radio and not the radio speaking: they update the roster and the
        /// identity snapshots as for any source, and leave the one display
        /// refresh to the merge, which raises it once, when the answer
        /// changed (#634). Thread-static because the command reader can be
        /// raising the same events for the radio's own status at the same
        /// moment on its own thread, and those must not read as a list's.
        /// </summary>
        [ThreadStatic] private static bool _mergingSmartLinkList;

        private void UpdateRadioDiscoveryFields(Radio newRadio, Radio oldRadio, string accountId)
        {
            Tracing.TraceLine("UpdateRadioDiscoveryFields:" + newRadio.Nickname + ' ' + newRadio.Callsign, TraceLevel.Info);
            // A LIST NEVER COMMANDS A RADIO'S NAME OR CALLSIGN (#477, under #397).
            // FlexLib's Nickname and Callsign setters send "radio name" and
            // "radio callsign", which the radio keeps in its firmware, and this
            // merge adopts the CONNECTED radio as its target - so a SmartLink
            // snapshot that lagged a rename wrote the old name back into
            // somebody's radio. The list is a copy of what the radio says about
            // itself; the radio is the authority:
            // - Connected, the radio reports its own name and callsign in its
            //   status, so the list's copy is not applied at all.
            // - Not connected, the list is the only source for the picker's
            //   row, so the copy is applied through UpdateIdentityFromList, a
            //   FlexLib patch that updates the record and sends nothing
            //   (MIGRATION.md item 24).
            // Not an ownership check: no radio, ours or anyone's, is ever told
            // its name by a list.
            if (!oldRadio.Connected)
                oldRadio.UpdateIdentityFromList(newRadio.Nickname, newRadio.Callsign);
            if (oldRadio.Status != newRadio.Status)
                oldRadio.Status = newRadio.Status;
            if (oldRadio.GuiClientIPs != newRadio.GuiClientIPs)
                oldRadio.GuiClientIPs = newRadio.GuiClientIPs;
            if (oldRadio.GuiClientHosts != newRadio.GuiClientHosts)
                oldRadio.GuiClientHosts = newRadio.GuiClientHosts;
            if (oldRadio.PublicTlsPort != newRadio.PublicTlsPort)
                oldRadio.PublicTlsPort = newRadio.PublicTlsPort;
            if (oldRadio.PublicUdpPort != newRadio.PublicUdpPort)
                oldRadio.PublicUdpPort = newRadio.PublicUdpPort;
            if (oldRadio.IsPortForwardOn != newRadio.IsPortForwardOn)
                oldRadio.IsPortForwardOn = newRadio.IsPortForwardOn;
            if (oldRadio.Version != newRadio.Version)
                oldRadio.Version = newRadio.Version;
            if (oldRadio.RequiresHolePunch != newRadio.RequiresHolePunch)
                oldRadio.RequiresHolePunch = newRadio.RequiresHolePunch;
            if (oldRadio.NegotiatedHolePunchPort != newRadio.NegotiatedHolePunchPort)
                oldRadio.NegotiatedHolePunchPort = newRadio.NegotiatedHolePunchPort;
            if (oldRadio.MaxLicensedVersion != newRadio.MaxLicensedVersion)
                oldRadio.MaxLicensedVersion = newRadio.MaxLicensedVersion;
            // RequiresAdditionalLicense removed in FlexLib v4.1.5
            if (oldRadio.RadioLicenseId != newRadio.RadioLicenseId)
                oldRadio.RadioLicenseId = newRadio.RadioLicenseId;
            if (oldRadio.LowBandwidthConnect != newRadio.LowBandwidthConnect)
                oldRadio.LowBandwidthConnect = newRadio.LowBandwidthConnect;

            // The client roster is the one field with a ruling on it (#634).
            // For the radio we are CONNECTED to over SmartLink, the list may
            // write our record — which is the station-name rescue's channel
            // — only when it came from the account this connection was
            // actually brokered through, read from the executed route and
            // never inferred (Track H's binding; null means unproven, and an
            // unproven account is not the account used). A list from any
            // other account describes the same radio but was not the path
            // we took, and its roster stays out of the live object. For a
            // radio we are not connected to, or connected to over the LAN,
            // the merge is display, as it always was.
            bool live = ReferenceEquals(oldRadio, theRadio);
            if (live)
            {
                var binding = CurrentConnectionBinding;
                if (binding != null && binding.IsWan
                    && (binding.AccountId == null
                        || !string.Equals(binding.AccountId, accountId, StringComparison.OrdinalIgnoreCase)))
                {
                    Tracing.TraceLine(
                        $"UpdateRadioDiscoveryFields: {oldRadio.Serial} is the connected radio, brokered through "
                        + (binding.AccountId == null ? "an unproven account" : "'" + binding.AccountId + "'")
                        + $"; a list from '{accountId}' does not write its client roster",
                        TraceLevel.Info);
                    return;
                }
            }

            // The merge raises GUIClientAdded/Removed for every difference,
            // and it would raise the display refresh per client and the
            // roster's Changed per client. The LAN discovery path was given
            // a signature gate for exactly this shape of unconditional raise
            // (discoveryGuiClientsChangedHandler, #394); this is its second
            // caller: one refresh per merge, and only when who-is-on-it
            // changed.
            _mergingSmartLinkList = true;
            try
            {
                oldRadio.UpdateGuiClientsList(newGuiClients: newRadio.GuiClients);
            }
            finally
            {
                _mergingSmartLinkList = false;
            }

            if (live && clientHandle != noClient)
            {
                // What THIS list said our own station is called, for the
                // rescue to name its source when it finishes the wait.
                string ours = null;
                lock (newRadio.GuiClientsLockObj)
                {
                    foreach (GUIClient c in newRadio.GuiClients)
                        if (c.ClientHandle == clientHandle) { ours = c.Station ?? ""; break; }
                }
                _ownStationSeenInSmartLinkList = ours;
            }

            int merged;
            string sig;
            lock (oldRadio.GuiClientsLockObj) merged = oldRadio.GuiClients.Count;
            sig = OccupancySignature(oldRadio);
            bool changed;
            lock (_occupancyRaised)
            {
                changed = !(_occupancyRaised.TryGetValue(oldRadio.Serial, out var prev) && prev == sig);
                if (changed) _occupancyRaised[oldRadio.Serial] = sig;
            }
            Tracing.TraceLine(
                $"occupancy[wan-merge]: {oldRadio.Serial} stations={merged}" + (changed ? $" — changed to [{sig}]" : " — unchanged, no refresh"),
                TraceLevel.Info);
            if (live && changed) GuiClientChanged?.Invoke();
        }

        // Sprint 26 Phase 4 deleted the `wan` field and the PreserveWanForRetry /
        // RestoreWanFromRetry methods. The SmartLink session now lives inside
        // SmartLinkServices.Coordinator and survives radio connect/disconnect
        // cycles by design — the preserve/restore band-aid is no longer needed.

        // SmartLink account manager for saved credentials
        private static SmartLinkAccountManager _accountManager;
        private static SmartLinkAccountManager AccountManager
        {
            get
            {
                if (_accountManager == null)
                {
                    _accountManager = new SmartLinkAccountManager();
                    _accountManager.LoadAccounts();
                }
                return _accountManager;
            }
        }

        /// <summary>
        /// The process-wide account manager — the SAME instance the connect
        /// flows use. Every UI that edits accounts must go through this, never
        /// a freshly constructed manager. Lesson of 2026-08-06: the account
        /// dialog used its own instance, so Reset Sign-In cleared tokens on
        /// disk while the rig's in-memory copy stayed live, reconnected
        /// silently, and its next save wrote the old tokens right back —
        /// the reset silently undone. One instance, one truth.
        /// </summary>
        public static SmartLinkAccountManager SharedAccountManager => AccountManager;

        /// <summary>
        /// The one answer to "which SmartLink account is in play." Wired once
        /// at startup (GetConfigInfo in globals.vb) to the same resolver the
        /// account selector uses: sole saved account, then the session
        /// "Use Now" override, then the saved default — and null when several
        /// accounts are saved and none has been chosen, because guessing there
        /// is the 2026-08-10 bug: most-recently-used elected another
        /// operator's account and silently opened a SmartLink session on it
        /// every launch. Static so a single wiring covers every FlexBase
        /// instance, including auto-connect's. Null hook means Radios.dll is
        /// running standalone.
        /// </summary>
        public static Func<SmartLinkAccount> ResolveCurrentAccountHook { get; set; }

        // Current SmartLink account (for token refresh on re-auth)
        private SmartLinkAccount _currentAccount;

        /// <summary>
        /// Ensure a SmartLink account session exists, resolving through
        /// ResolveCurrentAccountHook. Every path that sets _currentAccount
        /// otherwise lives in the REMOTE connect flows, so on a purely local
        /// connection the account was never loaded and everything downstream
        /// (registration preflight, registration query) wrongly concluded
        /// "not signed in" — found live 2026-08-04 when the Register button
        /// stayed grayed on the exact connection type registration requires.
        /// Returns false, deliberately, when several accounts are saved and
        /// none has been chosen: no answer beats a wrong one, and each caller
        /// handles the ambiguity in its own honest way.
        /// </summary>
        private bool TryLoadSavedAccount()
        {
            if (_currentAccount != null) return true;
            try
            {
                var accounts = AccountManager.Accounts;
                if (accounts == null || accounts.Count == 0) return false;

                var hook = ResolveCurrentAccountHook;
                if (hook != null)
                {
                    var resolved = hook();
                    if (resolved == null)
                    {
                        Tracing.TraceLine("TryLoadSavedAccount: several accounts saved, none chosen — not guessing", TraceLevel.Info);
                        return false;
                    }
                    _currentAccount = resolved;
                    Tracing.TraceLine($"TryLoadSavedAccount: resolved account '{_currentAccount.Email}'", TraceLevel.Info);
                    return true;
                }

                // Hook unwired: Radios.dll standalone. Most-recently-used is a
                // guess — trace loudly so a wiring regression in the app is
                // visible instead of quietly reviving the old behaviour.
                _currentAccount = accounts.OrderByDescending(a => a.LastUsed).First();
                Tracing.TraceLine($"TryLoadSavedAccount: resolver hook UNWIRED — standalone fallback picked most-recently-used '{_currentAccount.Email}'", TraceLevel.Warning);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"TryLoadSavedAccount: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Adopt a just-signed-in SmartLink account as this session's current
        /// account — the mid-session sign-in propagation fix (found live
        /// 2026-08-04: _currentAccount is only loaded during connect, so a New
        /// Login while connected left registration preflight insisting nobody
        /// was signed in until the app restarted). Prefers the saved instance
        /// from the shared manager when one exists for the same email, so
        /// later token refreshes persist; an unsaved sign-in (Remember
        /// unchecked) is adopted as-is for this session only.
        /// </summary>
        public bool AdoptSignedInAccount(SmartLinkAccount account)
        {
            if (account == null || string.IsNullOrEmpty(account.Email)) return false;
            var saved = AccountManager.GetAccountByEmail(account.Email);
            _currentAccount = saved ?? account;
            Tracing.TraceLine(
                $"AdoptSignedInAccount: session account is now '{_currentAccount.Email}' ({(saved != null ? "saved" : "session-only")})",
                TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Gets the email address of the currently active SmartLink account, if any.
        /// Used by the rig selector to save the account email in auto-connect config.
        /// </summary>
        public string CurrentSmartLinkEmail => _currentAccount?.Email ?? "";

        [Obsolete("Use setupRemote() which handles accounts automatically")]
        private string[] tokens;

        /// <summary>
        /// Delegate to show the SmartLink account selector dialog. Wired externally.
        /// Sprint 10: Replaces direct SmartLinkAccountSelector form creation.
        /// Returns (newLogin, selectedAccount, ok) or null if cancelled.
        /// </summary>
        public Func<SmartLinkAccountManager, (bool newLogin, SmartLinkAccount selected, bool ok)?> ShowAccountSelector { get; set; }

        /// <param name="allowInteractive">
        /// False when a sign-in form must NOT appear — the auth ladder's
        /// walk-before-prompting rung: when the connect chain still holds
        /// another path to try, a silent auth failure walks to it, and only
        /// an exhausted chain earns the native sign-in form. True preserves
        /// the historical behaviour for every existing caller.
        /// </param>
        /// <param name="radiosAccount">
        /// The saved account the RADIO is reached through, when the connect
        /// knows it (#798, from <see cref="PrepareSmartLinkForSerial"/>). Signs
        /// in as that account without asking the account selector, which has
        /// no serial and can only answer with the default. Null keeps the
        /// selector's answer, as for every caller before.
        /// </param>
        private bool setupRemote(bool allowInteractive = true, SmartLinkAccount radiosAccount = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"setupRemote: BEGIN allowInteractive={allowInteractive} radiosAccount={radiosAccount?.Email ?? "none"}", TraceLevel.Info);
            ConnectionProfiler.Current?.RecordEvent("setup_remote_begin");
            bool rv = false;
            string jwt = null;
            // Declared up here, not where it is first assigned, because the
            // done label reads it (#798) and the early exits jump over the
            // assignment. Null means no list was ever asked for.
            SmartLinkConnectResult? connectResult = null;

            // Check for saved accounts
            var accounts = AccountManager.Accounts;
            Tracing.TraceLine($"setupRemote: {accounts.Count} saved account(s) ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

            if (radiosAccount != null)
            {
                // The radio's own account, resolved from the radio (#798).
                _currentAccount = radiosAccount;
                Tracing.TraceLine($"setupRemote: signing in as the radio's own account '{_currentAccount.Email}', not the selector's default", TraceLevel.Info);
                jwt = GetJwtFromSavedAccount(_currentAccount, allowInteractiveLogin: allowInteractive);
                Tracing.TraceLine($"setupRemote: GetJwtFromSavedAccount returned jwt={(!string.IsNullOrEmpty(jwt) ? "yes" : "null/empty")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            }
            else if (accounts.Count > 0)
            {
                // Sprint 10: Show account selector via delegate (decoupled from WinForms Form)
                Tracing.TraceLine($"setupRemote: ShowAccountSelector delegate is {(ShowAccountSelector != null ? "WIRED" : "NULL")}", TraceLevel.Info);
                var result = ShowAccountSelector?.Invoke(AccountManager);
                Tracing.TraceLine($"setupRemote: ShowAccountSelector returned {(result.HasValue ? $"newLogin={result.Value.newLogin}, selected={result.Value.selected?.Email ?? "null"}, ok={result.Value.ok}" : "NULL")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                if (result == null || !result.Value.ok)
                {
                    Tracing.TraceLine("setupRemote: user cancelled account selection", TraceLevel.Info);
                    goto setupRemoteDone;
                }

                if (result.Value.newLogin)
                {
                    // User wants to log in with a new account - force Auth0 to show login page
                    Tracing.TraceLine("setupRemote: performing new login (user requested)", TraceLevel.Info);
                    jwt = allowInteractive ? PerformNewLogin(forceNewLogin: true) : null;
                    Tracing.TraceLine($"setupRemote: PerformNewLogin returned jwt={(!string.IsNullOrEmpty(jwt) ? "yes" : "null/empty")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }
                else if (result.Value.selected != null)
                {
                    // Use saved account
                    _currentAccount = result.Value.selected;
                    Tracing.TraceLine($"setupRemote: using saved account '{_currentAccount.Email}', calling GetJwtFromSavedAccount", TraceLevel.Info);
                    jwt = GetJwtFromSavedAccount(_currentAccount, allowInteractiveLogin: allowInteractive);
                    Tracing.TraceLine($"setupRemote: GetJwtFromSavedAccount returned jwt={(!string.IsNullOrEmpty(jwt) ? "yes" : "null/empty")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }
            }
            else
            {
                // No saved accounts - go straight to login
                Tracing.TraceLine("setupRemote: no saved accounts, performing new login", TraceLevel.Info);
                jwt = allowInteractive ? PerformNewLogin() : null;
                Tracing.TraceLine($"setupRemote: PerformNewLogin returned jwt={(!string.IsNullOrEmpty(jwt) ? "yes" : "null/empty")} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            }

            if (string.IsNullOrEmpty(jwt))
            {
                Tracing.TraceLine($"setupRemote: no jwt obtained, aborting ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                goto setupRemoteDone;
            }

            // Connect to SmartLink server
            Tracing.TraceLine($"setupRemote: calling ConnectToSmartLink ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            ConnectionProfiler.Current?.RecordEvent("smartlink_connect_begin");
            connectResult = ConnectToSmartLink(jwt);
            rv = connectResult == SmartLinkConnectResult.Success;
            ConnectionProfiler.Current?.RecordEvent("smartlink_connect_end", new Dictionary<string, object>
            {
                { "success", rv },
                { "result", connectResult.ToString() }
            });
            Tracing.TraceLine($"setupRemote: ConnectToSmartLink returned {connectResult} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);

            if (connectResult == SmartLinkConnectResult.NoRadios)
            {
                // Session connected fine, the account simply has no radios online.
                // Re-logging in cannot fix this — the prior code did exactly that
                // and hit "Invalid state for application registration" because the
                // session was already registered, then silently waited the full
                // 10s timeout per retry. Tag the trace outcome so the Archive
                // Browser can filter for this pattern and tell the user fast.
                TraceSessionContext.MarkOutcome(TraceSessionOutcome.NoRadios,
                    "SmartLink registered, server returned empty radio list");
                TraceSessionContext.AddKeyEvent("smartlink_no_radios");
                if (!SuppressSpeech)
                {
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("connect.smartlink.no_radios"),
                        VerbosityLevel.Critical, true);
                }
                goto setupRemoteDone;
            }

            // First medicine for any failure: cycle the WAN session and retry
            // with the CURRENT sign-in. Most non-auth failures here are the
            // pre-existing-session trap — the server sends the radio list once
            // per TLS session, so a re-entered connect over a live session can
            // time out (or worse) without any credential being wrong, and the
            // old response of popping an interactive login on a healthy account
            // was wrong medicine (Noel, 2026-08-06, trace 203418). Refresh the
            // JWT silently when we hold an account (id_tokens live 60 seconds;
            // native-lineage refresh takes ~250ms and no UI). The same cheap
            // silent pair is also the right FIRST medicine for AuthFailed — an
            // expired id_token refreshes without any form.
            if (connectResult == SmartLinkConnectResult.ConnectFailed
                || connectResult == SmartLinkConnectResult.AuthFailed)
            {
                Tracing.TraceLine($"setupRemote: {connectResult}; cycling WAN session and retrying with current sign-in ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                CycleWanSession("connect failed — possible stale pre-existing session");
                if (_currentAccount != null)
                {
                    string silentJwt = GetJwtFromSavedAccount(_currentAccount, allowInteractiveLogin: false);
                    if (!string.IsNullOrEmpty(silentJwt)) jwt = silentJwt;
                }
                connectResult = ConnectToSmartLink(jwt);
                rv = connectResult == SmartLinkConnectResult.Success;
                Tracing.TraceLine($"setupRemote: cycled retry returned {connectResult} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                if (connectResult == SmartLinkConnectResult.NoRadios)
                {
                    TraceSessionContext.MarkOutcome(TraceSessionOutcome.NoRadios,
                        "SmartLink registered after session cycle, server returned empty radio list");
                    TraceSessionContext.AddKeyEvent("smartlink_no_radios_after_cycle");
                    if (!SuppressSpeech)
                    {
                        ScreenReaderOutput.Speak(
                            Lexicon.Get("connect.smartlink.no_radios"),
                            VerbosityLevel.Critical, true);
                    }
                    goto setupRemoteDone;
                }
            }

            // QB Track D (item 6): the interactive sign-in form is the LAST
            // resort, and only for failures that are actually auth-shaped —
            // the server said AuthorizationExpired and a silent refresh did
            // not fix it. Transport/server failures (timeouts, exceptions,
            // list-never-came) must NOT summon a login form: the account is
            // healthy, and a form the user cannot fix anything with is worse
            // than an honest failure. (Also, historically: a fresh login +
            // ReRegister on an already-registered session triggers "Invalid
            // state for application registration" and a silent 10s hang.)
            if (connectResult == SmartLinkConnectResult.AuthFailed && !allowInteractive)
            {
                // Walk-before-prompting: the caller has another path to try,
                // so this failure stays silent and classified. The chain's
                // last SmartLink attempt comes back with interactive allowed.
                Tracing.TraceLine($"setupRemote: auth still failing after silent refresh; interactive login suppressed by caller ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
            }
            else if (connectResult == SmartLinkConnectResult.AuthFailed)
            {
                Tracing.TraceLine($"setupRemote: auth still failing after silent refresh, performing interactive login ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                jwt = PerformNewLogin();
                if (!string.IsNullOrEmpty(jwt))
                {
                    connectResult = ConnectToSmartLink(jwt);
                    rv = connectResult == SmartLinkConnectResult.Success;
                    Tracing.TraceLine($"setupRemote: retry ConnectToSmartLink returned {connectResult} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                    if (connectResult == SmartLinkConnectResult.NoRadios)
                    {
                        TraceSessionContext.MarkOutcome(TraceSessionOutcome.NoRadios,
                            "SmartLink registered after re-login, server returned empty radio list");
                        TraceSessionContext.AddKeyEvent("smartlink_no_radios_after_relogin");
                        if (!SuppressSpeech)
                        {
                            ScreenReaderOutput.Speak(
                                Lexicon.Get("connect.smartlink.no_radios"),
                                VerbosityLevel.Critical, true);
                        }
                    }
                }
            }

            // File the classified failure story so callers can speak the
            // reason itself. AuthFailed and ConnectFailed get different
            // words because they need different action from the user.
            if (connectResult == SmartLinkConnectResult.AuthFailed)
            {
                RecordConnectFailure(new ConnectFailureReport
                {
                    Class = ConnectFailureClass.AuthenticationFailed,
                    SpokenSummary = "SmartLink did not accept the sign-in for "
                        + (_currentAccount?.Email ?? "this account")
                        + ". Signing in again from the SmartLink account manager is the fix.",
                });
            }
            else if (connectResult == SmartLinkConnectResult.ConnectFailed)
            {
                RecordConnectFailure(new ConnectFailureReport
                {
                    Class = ConnectFailureClass.SessionSetupFailed,
                    SpokenSummary = "Could not reach the SmartLink server, or it stopped answering. "
                        + "Your sign-in is fine — this is a network or server problem. "
                        + "Check your internet connection and try again in a moment.",
                });
            }

            setupRemoteDone:
            // #798: the account signed in and its list came back empty. Every
            // NoRadios branch above jumps here, and none filed a report, so
            // ReconnectRemote's fallback told the operator sign-in had failed
            // when it had succeeded. Say what actually happened.
            if (connectResult == SmartLinkConnectResult.NoRadios)
            {
                RecordConnectFailure(new ConnectFailureReport
                {
                    Class = ConnectFailureClass.RadioNotFound,
                    // DRAFT wording (#798), awaiting Noel.
                    SpokenSummary = Lexicon.Get("connect.failure.smartlink_lists_no_radios",
                        ("email", _currentAccount?.Email ?? Lexicon.Get("connect.selector.this_account"))),
                });
            }
            // #259: the operator expressed remote intent this session, so from
            // here on hold a presence session per saved account — whatever this
            // particular pass's outcome. A failed pass on ONE account is no
            // reason to keep every other account's radios a guess.
            EngageSmartLinkPresence();
            sw.Stop();
            Tracing.TraceLine($"setupRemote: END result={rv} (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            return rv;
        }

        /// <summary>
        /// Sprint 35 Track K (#259) — wire the presence hooks (idempotent),
        /// make sure THIS instance's intake hears attributed lists, and hold
        /// one session per silently-signable saved account.
        ///
        /// <para><b>Two call sites, one rule.</b> The end of every remote pass
        /// (<c>setupRemote</c>), and the radio chooser opening onto a roster
        /// that contains at least one account-bound row (#382). Still
        /// deliberately NOT called from purely local flows or the background
        /// registration query: a LAN-only operator must never grow N background
        /// TLS connections they did not ask for.</para>
        ///
        /// <para><b>The second call site does not weaken that.</b> It is
        /// conditioned on the ROSTER, never on a guess about intent — an
        /// install that has only ever met radios on the LAN has no
        /// account-bound row, so nothing there can trigger it. What it fixes is
        /// the gate's POSITION: engagement used to require a COMPLETED remote
        /// pass, so a cold roster could not be live until the operator had
        /// already connected somewhere remote, which is the one moment the
        /// roster existed to help with. Noel, cold app, 2026-08-29: "If Don's
        /// radio is in my list, should it not at least sign in to check its
        /// status?"</para>
        ///
        /// <para>Public because the caller is the WPF picker, in another
        /// assembly. Kept as ONE method rather than a second entry point beside
        /// it: two ways to engage presence is how the same idea grows two
        /// implementations, and neither would conflict with the other at merge
        /// time.</para>
        /// </summary>
        public void EngageSmartLinkPresence()
        {
            try
            {
                Radios.SmartLink.SmartLinkPresenceService.AccountsHook ??= () => AccountManager.Accounts;
                Radios.SmartLink.SmartLinkPresenceService.SilentJwtHook ??=
                    (acct, force) => TryGetJwtSilently(acct, force);

                // Presence pushes must update rows even when no connect flow is
                // in flight, so engaging presence makes THIS rig the intake —
                // as ConnectToSmartLink does. The unsubscribe-then-subscribe
                // pair that used to stand here defended against doubling
                // WITHIN one instance and could not see the other instances at
                // all; there is now one handler and one intake for the whole
                // app, so there is nothing left to double (#386).
                becomeThePresenceIntake();

                Radios.SmartLink.SmartLinkPresenceService.EnsureHeldSessions(API.ProgramName);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"EngageSmartLinkPresence: {ex.Message}", TraceLevel.Error);
            }
        }

        /// <summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Restore focus to the parent window after a modal dialog closes.
        /// Must marshal to UI thread since auth runs on SmartLink thread.
        /// </summary>
        private void RestoreParentFocus()
        {
            try
            {
                var parent = Callouts?.ParentWindow as Control;
                if (parent != null && parent.IsHandleCreated)
                {
                    parent.BeginInvoke(new Action(() =>
                    {
                        SetForegroundWindow(parent.Handle);
                        if (parent is Form f)
                        {
                            f.Activate();
                            f.BringToFront();
                        }
                    }));
                }
            }
            catch { }
        }

        /// <summary>
        /// Performs a new login via WebView2 and optionally saves the account.
        /// </summary>
        /// <param name="forceNewLogin">When true, forces Auth0 to show the login page
        /// even if a session already exists (used when adding a new account).</param>
        /// <param name="title">Custom title for the auth form window (e.g., "Connecting to Radio"
        /// for auto-connect flows instead of the default "SmartLink Authentication").</param>
        /// <summary>
        /// Interactive Auth0 login.
        /// </summary>
        /// <param name="expectedAccount">
        /// The saved account this login is FOR, when the caller is trying to
        /// authenticate a specific one. Two things depend on it: the WebView2
        /// cookie profile (each account gets its own, so a live session for
        /// account A cannot silently satisfy a login request for account B),
        /// and the post-login identity check below. Passing null means "no
        /// particular account expected" — a genuinely new sign-in.
        /// 2026-08-05 bug this fixes: every caller relied on the ambient
        /// _currentAccount, so asking for Don's account opened Noel's cookie
        /// profile, auto-logged-in as Noel without a prompt, and saved the
        /// result — leaving the app connected to the wrong SmartLink account
        /// with no indication anything had gone sideways.
        /// </param>
        private string PerformNewLogin(SmartLinkAccount expectedAccount = null, bool forceNewLogin = false, string title = null)
        {
            // Native-first sign-in (2026-08-06, Don's lockout). The native form
            // exchanges email+password directly via the resource-owner grant —
            // SmartSDR's own mechanics — so no browser, cookie, or WebView2
            // profile is involved, and the refresh tokens it mints actually
            // renew id_tokens later. The browser form below survives as the
            // fallback: "Use Browser Instead", or automatically for accounts
            // that require two-factor sign-in.
            var native = ShowNativeLoginDialog(expectedAccount, forceNewLogin);
            if (native.result == DialogResult.OK)
            {
                RestoreParentFocus();
                return FinishInteractiveLogin(native.email, native.idToken, native.refreshToken, native.expiresIn, expectedAccount,
                    friendlyNameFromForm: native.friendlyName, rememberSignIn: native.rememberSignIn);
            }
            if (native.result != DialogResult.Retry)
            {
                Tracing.TraceLine("PerformNewLogin: native sign-in cancelled", TraceLevel.Info);
                RestoreParentFocus();
                return null;
            }
            Tracing.TraceLine("PerformNewLogin: falling back to browser sign-in", TraceLevel.Info);

            string jwt = null;

            // Manual lifetime, not `using`: this method runs on the SmartLink
            // background thread, but ShowDialog is marshaled to the UI thread,
            // so the WebView2's COM objects live there. Disposing from this
            // thread throws InvalidCastException (E_NOINTERFACE) inside
            // WebView2's cleanup — an unhandled crash that took the whole
            // process down live on 2026-08-04. The finally marshals the
            // dispose back to the UI thread.
            // Constructed directly. This used to go through
            // AuthForm.CreateAuthForm(), a factory that existed to choose
            // between the IE-based form and this one — but it returned only
            // this type, and the caller immediately downcast to it. The IE form
            // is gone (2026-08-17), and with one implementation left the
            // factory was pure indirection.
            var form = new AuthFormWebView2();
            try
            {
                form.ForceNewLogin = forceNewLogin;
                // AccountEmail follows the account being authenticated, not
                // whichever account happens to be current. It selects the
                // per-account WebView2 cookie profile (re-added in 1957420b
                // after the 81b688fe revert — an earlier comment here claimed
                // profiles were gone, which Don's 2026-08-06 trace disproved).
                // Cross-account safety = per-account profiles + ForceNewLogin
                // cookie clearing + the identity check in FinishInteractiveLogin.
                form.AccountEmail = expectedAccount?.Email ?? _currentAccount?.Email ?? "";
                if (!string.IsNullOrEmpty(title))
                {
                    form.Text = title;
                }

                // Show dialog on the main UI thread so it gets foreground focus.
                // PerformNewLogin may be called from the SmartLink background thread.
                DialogResult dialogResult = DialogResult.Cancel;
                var parent = Callouts?.ParentWindow as Control;
                if (parent != null && parent.IsHandleCreated && parent.InvokeRequired)
                {
                    parent.Invoke(new Action(() => { dialogResult = form.ShowDialog(parent as IWin32Window); }));
                }
                else
                {
                    dialogResult = form.ShowDialog();
                }

                if (dialogResult != DialogResult.OK)
                {
                    Tracing.TraceLine("setupRemote: auth form cancelled or failed", TraceLevel.Info);
                    RestoreParentFocus();
                    return null;
                }

                // Restore focus to the main app window after auth form closes.
                // Without this, focus falls to whatever window is behind the app
                // because ShowDialog() runs without an owner (cross-thread unsafe).
                RestoreParentFocus();

                jwt = FinishInteractiveLogin(form.Email, form.IdToken, form.RefreshToken, form.ExpiresIn, expectedAccount);
            }
            finally
            {
                DisposeAuthFormOnUiThread(form);
            }

            return jwt;
        }

        /// <summary>
        /// Show the native SmartLink sign-in dialog on the UI thread and hand
        /// back its outcome. Retry means "open the browser form instead" —
        /// either the user asked for it or the account needs two-factor.
        /// </summary>
        private (DialogResult result, string email, string idToken, string refreshToken, int expiresIn, string friendlyName, bool rememberSignIn)
            ShowNativeLoginDialog(SmartLinkAccount expectedAccount, bool forceNewLogin)
        {
            // Adding a genuinely new account starts blank; re-authenticating a
            // known account starts on its email with focus in the password box.
            string prefill = forceNewLogin ? "" : (expectedAccount?.Email ?? _currentAccount?.Email ?? "");

            DialogResult dr = DialogResult.Cancel;
            string email = "", idToken = "", refreshToken = "", friendlyName = "";
            int expiresIn = 0;
            bool rememberSignIn = true;

            // The dialog gets its OWN STA thread, deliberately — never shown
            // via the SmartLink thread or marshaled onto the main window.
            // Lesson from the 2026-08-06 trace: pumping a modal on the
            // SmartLink thread executed queued UI work there (ApplyUIMode ran
            // on T21 mid-login) and the shell then failed with "Error creating
            // window handle" at Show. A dedicated thread pumps only its own
            // queue, so no shared control can get its handle created on the
            // wrong thread. Same pattern as the connecting form. IsBackground
            // guarantees this thread can never keep a closed app's process
            // alive (the 2026-08-06 zombie-process lesson).
            var dialogThread = new System.Threading.Thread(() =>
            {
                try
                {
                    using var dlg = new SmartLinkLoginForm(AccountManager, prefill);
                    dr = dlg.ShowDialog();
                    email = dlg.Email;
                    idToken = dlg.IdToken;
                    refreshToken = dlg.RefreshToken;
                    expiresIn = dlg.ExpiresIn;
                    friendlyName = dlg.FriendlyName;
                    rememberSignIn = dlg.RememberSignIn;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"ShowNativeLoginDialog: dialog thread exception: {ex.Message}", TraceLevel.Error);
                    dr = DialogResult.Cancel;
                }
            });
            dialogThread.SetApartmentState(System.Threading.ApartmentState.STA);
            dialogThread.IsBackground = true;
            dialogThread.Start();
            dialogThread.Join();

            return (dr, email, idToken, refreshToken, expiresIn, friendlyName, rememberSignIn);
        }

        /// <summary>
        /// Shared tail of every interactive sign-in, native or browser:
        /// diagnostics, the cross-account identity check, and saving/updating
        /// the account. Returns the id_token, or null when the sign-in must be
        /// rejected.
        /// </summary>
        private string FinishInteractiveLogin(string email, string idToken, string refreshToken, int expiresIn, SmartLinkAccount expectedAccount,
            string friendlyNameFromForm = null, bool rememberSignIn = true)
        {
            // Diagnostic: log the exp claim from the fresh token
            if (!string.IsNullOrEmpty(idToken))
            {
                try
                {
                    var jwtParts = idToken.Split('.');
                    if (jwtParts.Length == 3)
                    {
                        var jwtPayload = jwtParts[1];
                        switch (jwtPayload.Length % 4)
                        {
                            case 2: jwtPayload += "=="; break;
                            case 3: jwtPayload += "="; break;
                        }
                        jwtPayload = jwtPayload.Replace('-', '+').Replace('_', '/');
                        var jwtJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(jwtPayload));
                        using var jwtDoc = System.Text.Json.JsonDocument.Parse(jwtJson);
                        if (jwtDoc.RootElement.TryGetProperty("exp", out var expEl))
                        {
                            var expUnix = expEl.GetInt64();
                            var expTime = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
                            var delta = expTime - DateTime.UtcNow;
                            Tracing.TraceLine($"PerformNewLogin: fresh JWT exp={expTime:yyyy-MM-dd HH:mm:ss}Z, delta={delta.TotalMinutes:F1}min, ExpiresIn={expiresIn}s", TraceLevel.Info);
                        }
                        if (jwtDoc.RootElement.TryGetProperty("iat", out var iatEl))
                        {
                            var iatUnix = iatEl.GetInt64();
                            var iatTime = DateTimeOffset.FromUnixTimeSeconds(iatUnix).UtcDateTime;
                            Tracing.TraceLine($"PerformNewLogin: fresh JWT iat={iatTime:yyyy-MM-dd HH:mm:ss}Z", TraceLevel.Info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"PerformNewLogin: JWT diagnostic parse failed: {ex.Message}", TraceLevel.Warning);
                }
            }

            if (string.IsNullOrEmpty(idToken))
            {
                Tracing.TraceLine("setupRemote: no id_token from sign-in", TraceLevel.Error);
                return null;
            }

            // Identity check: if this login was for a specific saved account,
            // the token we got back must belong to that account. The browser
            // path can hand back a different identity when a cookie session
            // already exists; the native path canonicalizes the typed email.
            // Silently accepting a mismatch connects the user to someone
            // else's radios and reports confusing state everywhere after.
            if (expectedAccount != null
                && !string.IsNullOrEmpty(email)
                && !string.Equals(email, expectedAccount.Email, StringComparison.OrdinalIgnoreCase))
            {
                Tracing.TraceLine(
                    $"PerformNewLogin: identity mismatch — asked for {expectedAccount.Email}, Auth0 returned {email}; rejecting",
                    TraceLevel.Error);
                ScreenReaderOutput.Speak(
                    Lexicon.Get("connect.smartlink.identity_mismatch",
                        ("email", email), ("expectedEmail", expectedAccount.Email)),
                    VerbosityLevel.Critical, true);
                return null;
            }

            // Save or update the account. NO dialogs anywhere in this tail —
            // round 27 (Don, 2026-08-06): the old "Save this account?"
            // MessageBox popped ownerless behind the TopMost Connecting form,
            // unannounced, and the SmartLink thread blocked on it until Don
            // gave up. The remember decision now rides in from the sign-in
            // form itself; the browser path (no such field) saves and says
            // so — an unwanted account is a ten-second delete in the account
            // manager, a question nobody can perceive is a hang.
            if (!string.IsNullOrEmpty(refreshToken))
            {
                if (!rememberSignIn)
                {
                    Tracing.TraceLine($"setupRemote: user chose not to remember sign-in for {email}", TraceLevel.Info);
                    ScreenReaderOutput.Speak(Lexicon.Get("connect.smartlink.signed_in_not_remembered_during_connect"), VerbosityLevel.Terse, true);
                }
                else
                {
                    // Check if this email already has a saved account
                    var existingAccount = AccountManager.GetAccountByEmail(email);

                    if (existingAccount != null)
                    {
                        // Silently update the existing account's tokens
                        existingAccount.IdToken = idToken;
                        existingAccount.RefreshToken = refreshToken;
                        existingAccount.ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 86400);
                        // A name typed during re-auth is a rename request.
                        if (!string.IsNullOrEmpty(friendlyNameFromForm))
                        {
                            existingAccount.FriendlyName = friendlyNameFromForm;
                        }
                        AccountManager.SaveAccount(existingAccount);
                        _currentAccount = existingAccount;

                        Tracing.TraceLine($"setupRemote: updated existing account for {email}", TraceLevel.Info);
                    }
                    else
                    {
                        string friendlyName = !string.IsNullOrEmpty(friendlyNameFromForm)
                            ? friendlyNameFromForm
                            : email;

                        var account = new SmartLinkAccount
                        {
                            FriendlyName = friendlyName,
                            Email = email ?? string.Empty,
                            IdToken = idToken,
                            RefreshToken = refreshToken,
                            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 86400),
                            LastUsed = DateTime.UtcNow
                        };

                        AccountManager.SaveAccount(account);
                        _currentAccount = account;

                        ScreenReaderOutput.Speak(
                            Lexicon.Get("connect.smartlink.account_saved_during_connect", ("friendlyName", friendlyName)),
                            VerbosityLevel.Terse, true);
                        Tracing.TraceLine($"setupRemote: saved account for {email}", TraceLevel.Info);
                    }
                }
            }

            // Legacy compatibility
            #pragma warning disable CS0618
            tokens = new[] { $"id_token={idToken}" };
            #pragma warning restore CS0618

            return idToken;
        }

        /// <summary>
        /// Dispose the WebView2 auth form on the UI thread that owns its COM
        /// objects. See the lifetime note in PerformNewLogin.
        /// </summary>
        private void DisposeAuthFormOnUiThread(Form form)
        {
            if (form == null) return;
            try
            {
                var parent = Callouts?.ParentWindow as Control;
                if (parent != null && parent.IsHandleCreated && parent.InvokeRequired)
                {
                    parent.Invoke(new Action(() =>
                    {
                        try { form.Dispose(); }
                        catch (Exception ex) { Tracing.TraceLine($"DisposeAuthFormOnUiThread: {ex.Message}", TraceLevel.Error); }
                    }));
                }
                else
                {
                    form.Dispose();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"DisposeAuthFormOnUiThread: {ex.Message}", TraceLevel.Error);
            }
        }

        /// <summary>
        /// Gets JWT from a saved account, refreshing if necessary.
        /// </summary>
        /// <param name="allowInteractiveLogin">
        /// False for background callers (the connect-time registration query):
        /// when a silent refresh cannot produce a valid JWT, return null instead
        /// of popping the login dialog. A background check surprising the user
        /// with a login page — and hanging Settings behind it — is exactly what
        /// happened live on 2026-08-04.
        /// </param>
        private string GetJwtFromSavedAccount(SmartLinkAccount account, bool allowInteractiveLogin = true, bool markUsed = true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"GetJwtFromSavedAccount: BEGIN email={account.Email}, ExpiresAt={account.ExpiresAt}, now={DateTime.UtcNow}, interactive={allowInteractiveLogin}", TraceLevel.Info);

            // If we already have an active WAN connection, the previous JWT may have been
            // consumed by the server. Always refresh to get a fresh token for re-registration.
            bool needsRefresh = AccountManager.IsTokenExpired(account);
            bool wanActive = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession?.IsConnected == true;
            Tracing.TraceLine($"GetJwtFromSavedAccount: IsTokenExpired={needsRefresh}, wanActive={wanActive}", TraceLevel.Info);
            if (!needsRefresh && wanActive)
            {
                Tracing.TraceLine("GetJwtFromSavedAccount: existing WAN connection detected, forcing refresh for re-registration", TraceLevel.Info);
                needsRefresh = true;
            }

            // The whole silent machinery lives in TryGetJwtSilently — the ONE
            // token recipe, shared with the presence sessions' auto-register
            // (#259). This method only layers the interactive fallback and
            // the deliberate-use bookkeeping on top.
            string jwt = TryGetJwtSilently(account, forceRefresh: needsRefresh);

            if (string.IsNullOrEmpty(jwt))
            {
                Tracing.TraceLine($"GetJwtFromSavedAccount: no JWT available silently, interactive={allowInteractiveLogin} ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                // Pass the account so the identity check applies, and force a
                // fresh session when we're authenticating an account other than
                // the one already signed in — WebView2 uses ONE shared cookie
                // store (per-account profiles were reverted in 81b688fe over
                // folder locks), so an existing Auth0 session would otherwise
                // silently satisfy a request for a different account.
                bool differentAccount =
                    _currentAccount != null
                    && !string.Equals(_currentAccount.Email, account.Email, StringComparison.OrdinalIgnoreCase);
                return allowInteractiveLogin
                    ? PerformNewLogin(account, forceNewLogin: differentAccount)
                    : null;
            }

            // LastUsed means "the operator deliberately used this account" —
            // connects and registration commands qualify; the silent
            // background registration query passes markUsed: false. Stamping
            // on every touch was half the latch that kept re-electing the
            // same account under the old most-recently-used resolution
            // (2026-08-10; the other half was the token-refresh stamp in
            // SmartLinkAccountManager).
            if (markUsed) AccountManager.MarkAccountUsed(account);

            sw.Stop();
            Tracing.TraceLine($"GetJwtFromSavedAccount: END returning jwt=yes (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
            return jwt;
        }

        /// <summary>
        /// The silent JWT recipe, and the only one: refresh-token grant first
        /// when the stored id_token is dead (it lives 60 seconds on this
        /// tenant, so it essentially always is — SmartSDR refreshes before
        /// every registration and gets a fresh id_token back), an extra
        /// forced refresh when the caller says the current token may already
        /// be consumed, and null — NEVER a login form — when silence cannot
        /// produce a valid token. Serving both the interactive flow
        /// (<see cref="GetJwtFromSavedAccount"/> layers the login fallback on
        /// top) and the held presence sessions' auto-registration (#259),
        /// which wire it through
        /// <see cref="Radios.SmartLink.SmartLinkPresenceService.SilentJwtHook"/>.
        /// Safe on any thread; blocks on the token refresh HTTP round trip.
        /// </summary>
        internal static string TryGetJwtSilently(SmartLinkAccount account, bool forceRefresh)
        {
            if (account == null) return null;

            bool isJwtExpired = SmartLinkAccountManager.IsJwtExpired(account.IdToken);
            Tracing.TraceLine($"TryGetJwtSilently: email={account.Email} forceRefresh={forceRefresh} isJwtExpired={isJwtExpired} hasRefreshToken={!string.IsNullOrEmpty(account.RefreshToken)}", TraceLevel.Info);

            if (isJwtExpired && !string.IsNullOrEmpty(account.RefreshToken))
            {
                bool jitRefreshed = false;
                try
                {
                    jitRefreshed = Task.Run(() => AccountManager.RefreshTokenAsync(account)).Result;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"TryGetJwtSilently: JIT refresh threw: {ex.Message}", TraceLevel.Warning);
                }
                if (jitRefreshed)
                {
                    isJwtExpired = SmartLinkAccountManager.IsJwtExpired(account.IdToken);
                    Tracing.TraceLine($"TryGetJwtSilently: JIT refresh ok, isJwtExpired now {isJwtExpired}", TraceLevel.Info);
                }
            }

            if (isJwtExpired)
            {
                Tracing.TraceLine("TryGetJwtSilently: JWT still expired after JIT refresh — nothing silent left to try", TraceLevel.Info);
                return null;
            }

            if (forceRefresh)
            {
                // The manager serializes refreshes per account and satisfies a
                // second ask within seconds from the first one's result, so a
                // forced refresh right after the JIT one above costs nothing.
                bool refreshed = false;
                try
                {
                    refreshed = Task.Run(() => AccountManager.RefreshTokenAsync(account)).Result;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"TryGetJwtSilently: forced refresh threw: {ex.InnerException?.Message ?? ex.Message}", TraceLevel.Error);
                }
                if (!refreshed)
                {
                    Tracing.TraceLine("TryGetJwtSilently: forced refresh failed", TraceLevel.Warning);
                    return null;
                }
                if (SmartLinkAccountManager.IsJwtExpired(account.IdToken))
                {
                    Tracing.TraceLine("TryGetJwtSilently: JWT still expired after forced refresh", TraceLevel.Warning);
                    return null;
                }
            }

            return account.IdToken;
        }

        /// <summary>
        /// Prompts user for a friendly name for the account.
        /// </summary>
        private string PromptForAccountName(string defaultEmail)
        {
            using (var inputForm = new Form())
            {
                inputForm.Text = "Name This Account";
                inputForm.Size = new System.Drawing.Size(400, 150);
                inputForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                inputForm.StartPosition = FormStartPosition.CenterParent;
                inputForm.MaximizeBox = false;
                inputForm.MinimizeBox = false;

                var label = new Label
                {
                    Text = "Enter a friendly name for this account (e.g., \"Home Shack\"):",
                    Location = new System.Drawing.Point(12, 12),
                    AutoSize = true
                };

                var textBox = new TextBox
                {
                    Text = defaultEmail ?? "My SmartLink Account",
                    Location = new System.Drawing.Point(12, 35),
                    Size = new System.Drawing.Size(360, 23),
                    AccessibleName = "Account name"
                };
                textBox.SelectAll();

                var okButton = new Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(216, 70),
                    Size = new System.Drawing.Size(75, 28),
                    DialogResult = DialogResult.OK
                };

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(297, 70),
                    Size = new System.Drawing.Size(75, 28),
                    DialogResult = DialogResult.Cancel
                };

                inputForm.Controls.AddRange(new Control[] { label, textBox, okButton, cancelButton });
                inputForm.AcceptButton = okButton;
                inputForm.CancelButton = cancelButton;

                if (inputForm.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    return textBox.Text.Trim();
                }
            }

            return defaultEmail ?? "SmartLink Account";
        }

        /// <summary>
        /// Replay each held session's latest list through the intake, so a
        /// new instance's bookkeeping catches up with what the server has
        /// already told a session that outlives it — but only a list that
        /// describes the present. Returns how many lists were replayed.
        /// </summary>
        /// <remarks>
        /// <para>Sprint 35 Track K (#259): a held session KEEPS its account's
        /// last list across this instance's whole lifetime — but a NEW
        /// FlexBase's own bookkeeping starts empty and the next spontaneous
        /// push could be minutes away. Replaying the cached lists through the
        /// intake gives this instance — and the selector's rows — the current
        /// truth immediately instead of a 10s timeout followed by a needless
        /// session cycle.</para>
        ///
        /// <para>EVERY held session's list, not only the current account's
        /// (#402). The server sends one list per TLS session, so a rig created
        /// after that list landed can only ever get the cached copy — and
        /// replaying just <c>_currentAccount</c>'s meant a foreign radio
        /// (Don's, owned by another account) never entered <c>myRadioList</c>
        /// at all. The 2026-08-29 18:18 trace shows the cost: the mid-connect
        /// push for the very radio being connected took the add-branch
        /// instead of merging into <c>theRadio</c>, and the silent GuiClients
        /// merge — the channel that satisfied the station-name wait in every
        /// successful trace — was dead for the whole 42-second hang. Each list
        /// is attributed to ITS OWN account; the ghost sweep inside the
        /// handler is account-scoped, so replaying A's list still says nothing
        /// about B's radios.</para>
        ///
        /// <para><b>A list from an earlier connection is not replayed at all
        /// (#619).</b> A session keeps its list across a drop, so after a
        /// reconnect and before the new connection's first push a connected
        /// session still carries the previous connection's list. Track L3
        /// left the replay reading it, on the reasoning that the replay is
        /// discovery rather than evidence and the next push corrects it; Sol's
        /// review of L3 listed what the replay actually does with a list —
        /// sets the connect flow's latch, adds to
        /// <c>myRadioList</c>, rewrites the WAN object bank, runs the
        /// account-wide ghost sweep, writes the account-list display cache,
        /// and can satisfy the connect wait on the spot — and every one of
        /// those treats the list as the server's FULL CURRENT list for the
        /// account. The previous connection's list is not that, so it is not
        /// given to the handler. It stays held in the session, where the
        /// post-drop diagnostic probe reads it, and the flow waits for the
        /// live connection's own push exactly as it does for a session that
        /// has just connected. One read, from
        /// <see cref="Radios.SmartLink.IWanSessionOwner.RadioListSnapshot"/>,
        /// so the list and the fact about it come from the same moment.</para>
        /// </remarks>
        /// <param name="accountEmail">The account this connect flow is
        /// working with — the one whose session it will wait on.</param>
        /// <param name="sessionWasAlreadyConnected">Whether that account's
        /// session was live before the flow began. Its own list is replayed
        /// only then: a session that just connected will push its list
        /// itself, and the flow's latch waits for it.</param>
        /// <param name="sw">The connect flow's stopwatch, for the trace
        /// timings; null outside the flow.</param>
        internal int ReplayHeldListsIntoTheIntake(
            string accountEmail, bool sessionWasAlreadyConnected, System.Diagnostics.Stopwatch sw = null,
            Radios.SmartLink.SessionRadioListSnapshot? ownListAtStart = null)
        {
            int replayed = 0;
            string at() => sw == null ? "" : $" ({sw.ElapsedMilliseconds}ms)";
            foreach (var held in Radios.SmartLink.SmartLinkServices.Coordinator.AllSessions)
            {
                try
                {
                    if (held == null) continue;
                    var snapshot = held.RadioListSnapshot;
                    if (!snapshot.SessionConnected) continue;
                    bool own = string.Equals(held.AccountId, accountEmail, StringComparison.OrdinalIgnoreCase);
                    if (own && !sessionWasAlreadyConnected) continue;
                    // A connect call replays its OWN account's list only if it
                    // is the list the call found (#613, repair round 3, Codex
                    // B3-R2a). A list accepted during the call is that call's
                    // answer from the server; replaying it as a held copy
                    // satisfied the call before the push reached its latch, and
                    // the answer was lost. The push brings it instead.
                    if (own && ownListAtStart.HasValue && !TheListWasFoundAtStart(ownListAtStart.Value, snapshot))
                    {
                        if (snapshot.Radios != null && snapshot.Radios.Count > 0)
                            Tracing.TraceLine(
                                $"ConnectToSmartLink: not replaying {held.AccountId}'s list (session {snapshot.SessionId} connection {snapshot.ConnectionGeneration}) — it arrived during this call, so its push is this call's answer{at()}",
                                TraceLevel.Info);
                        continue;
                    }
                    var cached = snapshot.Radios;
                    if (cached == null || cached.Count == 0) continue;
                    if (!snapshot.ArrivedOnTheLiveConnection)
                    {
                        Tracing.TraceLine(
                            $"ConnectToSmartLink: not replaying {held.AccountId}'s held list ({cached.Count} radio(s)) — it arrived on an earlier connection and the live one has not sent its own yet{at()}",
                            TraceLevel.Info);
                        continue;
                    }
                    // Rows this rig already holds for the account stand only
                    // if they came from THIS list. A rig that was not the
                    // intake when the live connection's list arrived still
                    // holds the rows it took on an earlier connection — the
                    // session's list is live, this rig's rows are not — and
                    // the replay is what brings them up to date (#619, Sol's
                    // review of L5). Track L5 skipped the replay whenever any
                    // matching row existed, which is how such a rig answered
                    // from stale rows.
                    if (myRadioList.Any(r => r.IsWan && WanRadioBelongsToAccount(r.Serial, held.AccountId))
                        && WanRowsCameFrom(held.AccountId, snapshot)) continue;
                    Tracing.TraceLine(
                        $"ConnectToSmartLink: replaying held session's cached list for {held.AccountId} ({cached.Count} radio(s), session {snapshot.SessionId} connection {snapshot.ConnectionGeneration}) through the intake{at()}",
                        TraceLevel.Info);
                    // The snapshot above is one moment, and the intake is a
                    // later one: a dial, a disconnect or the transport's
                    // death can land between them. The intake asks the
                    // session again, with the snapshot's generation, exactly
                    // as it asks for a push, and refuses a list that has
                    // stopped being current (#619, Sol's review of L6). Only
                    // a list it actually took is counted.
                    WanIntakeStageReached?.Invoke(WanIntakeStage.ReplaySnapshotTaken);
                    if (wanRadioListReceivedHandler(held.AccountId, cached, WanListArrival.ReplayOfHeldCopy,
                            new WanListProvenance(held, snapshot.SessionId, snapshot.ConnectionGeneration, snapshot.ListRevision)))
                        replayed++;
                }
                catch (Exception replayEx)
                {
                    Tracing.TraceLine($"ConnectToSmartLink: cached-list replay for {held?.AccountId ?? "?"} failed: {replayEx.Message}", TraceLevel.Warning);
                }
            }
            return replayed;
        }

        /// <summary>
        /// Whether the connect flow may answer from this instance's own WAN
        /// rows for <paramref name="accountEmail"/> — satisfying itself on the
        /// spot on a re-entry, or accepting them when the list wait times
        /// out — instead of insisting on the server's list.
        /// </summary>
        /// <remarks>
        /// <para><b>Only when the live connection has listed the account
        /// (#619).</b> The shortcut exists because the server sends its list
        /// once per TLS session, so on a re-entry over a session that has
        /// already been listed, no new list is coming and waiting for one is
        /// dead time (QB Track A). That premise is false in exactly one
        /// window: after a SmartLink drop and reconnect, before the new
        /// connection's first push. The session is connected, and an
        /// EXISTING rig still holds the rows it took on the previous
        /// connection — nothing removes them on a SmartLink-only drop, and
        /// FlexLib's RadioRemoved never fires for a WAN-only radio — so a
        /// re-entered rig answered instantly from rows that describe the
        /// connection before this one, while a fresh rig, whose replay Track
        /// L4 had already made wait, waited for the push. Sol's review of L4
        /// named the asymmetry. In that window the list IS coming, within
        /// about a hundred milliseconds of the registration in every trace
        /// L2 and L3 cited, so the flow waits for it as a fresh rig does.</para>
        ///
        /// <para>Outside that window nothing changes: a session whose live
        /// connection has listed the account still answers instantly from the
        /// rows, which the intake kept current from that same push. And the
        /// wait is bounded as it always was — ten seconds, then the flow
        /// fails as it does for a fresh rig, so there is no path that waits
        /// forever.</para>
        /// </remarks>
        internal bool OwnRowsMayAnswerTheConnect(
            Radios.SmartLink.IWanSessionOwner session, string accountEmail, System.Diagnostics.Stopwatch sw = null)
            => session != null && OwnRowsMayAnswerTheConnect(session.RadioListSnapshot, accountEmail, sw);

        /// <summary>
        /// <see cref="OwnRowsMayAnswerTheConnect(Radios.SmartLink.IWanSessionOwner, string, System.Diagnostics.Stopwatch)"/>
        /// on a reading the caller already took, so its other decisions rest
        /// on the same one (#613, repair round 3).
        /// </summary>
        internal bool OwnRowsMayAnswerTheConnect(
            Radios.SmartLink.SessionRadioListSnapshot snapshot, string accountEmail, System.Diagnostics.Stopwatch sw = null)
        {
            // WAN entries only: myRadioList also accumulates LAN radios, and a
            // LAN-only cache says nothing about this SmartLink session. Scoped
            // to THIS account: with presence holding every account's sessions,
            // another account's radios in myRadioList say nothing about the
            // account this flow is connecting.
            int rows = myRadioList.Count(r => r.IsWan && WanRadioBelongsToAccount(r.Serial, accountEmail));
            if (rows == 0) return false;

            // One reading, so the connected fact and the arrived-on-the-live-
            // connection fact describe the same moment.
            if (!snapshot.SessionConnected) return false;
            if (!snapshot.ArrivedOnTheLiveConnection)
            {
                Tracing.TraceLine(
                    $"ConnectToSmartLink: {rows} cached WAN radio(s) for {accountEmail} were taken before the live connection listed this account — waiting for its list rather than answering from them"
                    + (sw == null ? "" : $" ({sw.ElapsedMilliseconds}ms)"),
                    TraceLevel.Info);
                return false;
            }

            // The session's list is live. That says nothing about THIS rig's
            // rows unless they came from it: another rig may have been the
            // intake when the live connection's list arrived, leaving this
            // one holding the rows it took on an earlier connection (#619,
            // Sol's review of L5). Track L5 answered here from the session's
            // liveness alone.
            if (WanRowsCameFrom(accountEmail, snapshot)) return true;

            Tracing.TraceLine(
                $"ConnectToSmartLink: {rows} cached WAN radio(s) for {accountEmail} did not come from the live connection's list (session {snapshot.SessionId} connection {snapshot.ConnectionGeneration}) — this rig was not the intake when it arrived; waiting for the list rather than answering from them"
                + (sw == null ? "" : $" ({sw.ElapsedMilliseconds}ms)"),
                TraceLevel.Info);
            return false;
        }

        /// <summary>
        /// True when the session's list for its LIVE connection has already
        /// arrived and holds no radio — an empty answer, given (#613).
        /// </summary>
        /// <remarks>
        /// <para><b>Arrived, not merely "connected with nothing cached".</b>
        /// <see cref="OwnRowsMayAnswerTheConnect"/> answers false at zero rows
        /// before it reads whether the list arrived, so an account whose list
        /// came back EMPTY was indistinguishable from one whose list had not
        /// come yet, and the connect waited ten seconds for a second list the
        /// server does not send per TLS session (measured in Noel's trace of
        /// 2026-09-23: the wait, then the cycle, then the list 79 ms after the
        /// fresh registration). A session that connected a moment ago and has
        /// not been listed yet also holds nothing; that is NOT this, and it
        /// still waits, because its list is coming.</para>
        /// <para>One snapshot, so the connected fact, the arrived fact and the
        /// list describe the same moment, as in
        /// <see cref="OwnRowsMayAnswerTheConnect"/>. The session is one
        /// account's, so its list is that account's list.</para>
        /// </remarks>
        /// <summary>
        /// True when <paramref name="now"/> is the very list the call found
        /// at its start, and that list was an answer then: connected and
        /// arrived on that connection, and still the same session,
        /// connection and list revision now (#613, repair rounds 2 to 4; the
        /// revision replaced the wall-clock arrival time in round 4). Two
        /// readings compared as values; nothing is read here.
        /// </summary>
        internal static bool TheListWasFoundAtStart(
            Radios.SmartLink.SessionRadioListSnapshot atStart, Radios.SmartLink.SessionRadioListSnapshot now)
            => atStart.SessionConnected && atStart.ArrivedOnTheLiveConnection
               && now.SessionConnected && now.ArrivedOnTheLiveConnection
               && string.Equals(now.SessionId, atStart.SessionId, StringComparison.Ordinal)
               && now.ConnectionGeneration == atStart.ConnectionGeneration
               && now.ListRevision == atStart.ListRevision;

        /// <summary>
        /// True when this one reading is a list that arrived on its live
        /// connection and holds no radio: an empty answer, given (#613).
        /// The caller passes the reading it decides everything else from.
        /// </summary>
        internal static bool TheLiveListAnsweredEmpty(
            Radios.SmartLink.SessionRadioListSnapshot snapshot, string account, System.Diagnostics.Stopwatch sw = null)
        {
            if (!snapshot.SessionConnected || !snapshot.ArrivedOnTheLiveConnection) return false;
            if (snapshot.Radios != null && snapshot.Radios.Count > 0) return false;
            Tracing.TraceLine(
                $"ConnectToSmartLink: {account}'s list for its live connection (session {snapshot.SessionId} connection {snapshot.ConnectionGeneration}) has arrived and is empty — no list wait"
                + (sw == null ? "" : $" ({sw.ElapsedMilliseconds}ms)"),
                TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Connects to SmartLink server with the given JWT.
        ///
        /// <para>
        /// Sprint 26 Phase 2: this method no longer owns the WanServer directly.
        /// It asks <see cref="Radios.SmartLink.SmartLinkServices.Coordinator"/>
        /// for the session associated with the current account, drives
        /// <see cref="Radios.SmartLink.IWanSessionOwner.Connect"/>, waits for the
        /// session to report IsConnected, then calls ReRegister with the JWT.
        /// The monitor thread inside the session owner handles backoff, reconnect,
        /// and lifecycle — FlexBase never touches a WanServer directly after this
        /// change.
        /// </para>
        ///
        /// <para>
        /// Sprint 35 Track K (#259): radio-list discovery now runs through the
        /// coordinator's attributed <c>SessionRadioListReceived</c> event —
        /// each held session hears only its own account's lists (instance
        /// event in the vendored FlexLib, see MIGRATION.md item 13) and the
        /// coordinator re-raises them stamped with the account. Our handler
        /// answers this call's own latch (a newer push for its account on its
        /// session; ListAnswersTheCall); the owner populates
        /// <c>session.AvailableRadios</c> for every account.
        /// </para>
        /// </summary>
        /// <param name="accountEmail">The account whose session to work
        /// through; null keeps the connection's account, as every caller did
        /// before the registration query learned to ask under the radio's own
        /// account (#713).</param>
        private SmartLinkConnectResult ConnectToSmartLink(string jwt, string accountEmail = null)
            => ConnectToSmartLink(jwt, accountEmail, out _);

        /// <param name="serverPush">The list the SmartLink server pushed to
        /// THIS call for its account, or null when none came: the evidence a
        /// registration query may read an absence from.</param>
        /// <param name="activateSession">True for a connect, which makes its
        /// account's session the active one, as it always has. False for an
        /// advisory call (the registration query): it uses the account's
        /// session without making it active, so the spoken status, the next
        /// connect's fallback and ReconnectRemote's question stay on the
        /// operator's account (integration pass, finding 1).</param>
        private SmartLinkConnectResult ConnectToSmartLink(string jwt, string accountEmail,
            out SmartLinkRegistrationEvidence.AccountList? serverPush, bool activateSession = true)
        {
            serverPush = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tracing.TraceLine($"ConnectToSmartLink: BEGIN jwt length={jwt?.Length ?? 0}", TraceLevel.Info);
            ConnectionProfiler.Current?.RecordEvent("smartlink_connect_begin", new Dictionary<string, object>
            {
                { "jwtLength", jwt?.Length ?? 0 }
            });
            ConnectLatch latch = null;
            try
            {
                if (string.IsNullOrWhiteSpace(accountEmail))
                    accountEmail = _currentAccount?.Email ?? "default-account";
                latch = new ConnectLatch(accountEmail);
                var session = activateSession
                    ? Radios.SmartLink.SmartLinkServices.Coordinator.EnsureSessionForAccount(accountEmail)
                    : Radios.SmartLink.SmartLinkServices.Coordinator.GetOrCreateSession(accountEmail);

                // Take the intake, so this rig's rows are kept current with
                // session.AvailableRadios. A waiting call is answered wherever
                // the intake is (repair round 5), but the rows a held answer
                // uses are this rig's, so the intake is still taken. #386 replaced the
                // per-instance unsubscribe/subscribe pair here: one static
                // dispatcher, one intake, this one.
                becomeThePresenceIntake();

                // Whether this TLS session was live BEFORE this call. The server
                // sends the radio list exactly once per TLS session, so on a
                // re-entry over a live session no new list is ever coming and
                // waiting for one is pure dead time (QB Track A, 2026-08-07,
                // trace 20260805-163019: re-clicking Remote sat through the
                // full wait for an event the server never re-sends).
                bool sessionWasAlreadyConnected = session.IsConnected;
                // The session's list as this call FOUND it (#613, repair rounds
                // 2 and 3). Only an answer that was already there, and is
                // still the very same list, may stand in for the wait below or
                // be replayed to this call; a list that arrives during the call
                // is this call's answer and comes through the intake to this
                // call's latch. Read under the lock that opens the latch: the
                // session records a list before it forwards it, and the intake
                // writes latches under this lock, so a list is either in this
                // reading (held) or reaches the latch (pushed), never neither.
                Radios.SmartLink.SessionRadioListSnapshot listAtStart;

                // Kick the monitor thread. If the session is already connected (e.g. we're
                // re-entering ConnectToSmartLink after a successful previous call), Connect
                // is a cheap no-op because _wan.IsConnected is already true.
                lock (_wanIntakeLock)
                {
                    _openLatches.Add(latch);
                    listAtStart = session.RadioListSnapshot;
                    latch.StartSessionId = listAtStart.SessionId ?? "";
                    latch.StartRevision = listAtStart.ListRevision;
                }
                session.Connect();

                Tracing.TraceLine($"ConnectToSmartLink: waiting up to 10s for session IsConnected ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                if (!await(() => session.IsConnected || session.Status == Radios.SmartLink.SessionStatus.AuthorizationExpired, 10000))
                {
                    Tracing.TraceLine($"ConnectToSmartLink: session did not reach Connected within 10s (status={session.Status}) ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                    return SmartLinkConnectResult.ConnectFailed;
                }

                if (session.Status == Radios.SmartLink.SessionStatus.AuthorizationExpired)
                {
                    Tracing.TraceLine($"ConnectToSmartLink: session reports AuthorizationExpired; setupRemote handles re-auth ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                    return SmartLinkConnectResult.AuthFailed;
                }

                // One registration per connection: the session's own
                // auto-register (held-open presence, #259) may already have
                // sent it, and a second registration is at best a pointless
                // server poke. TryClaimRegistration is the atomic answer to
                // who sends it; the claim resets when the connection drops.
                if (session.TryClaimRegistration())
                {
                    Tracing.TraceLine($"ConnectToSmartLink: session connected; ReRegister {API.ProgramName} Win10 jwt={jwt.Substring(0, Math.Min(20, jwt.Length))}... ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                    session.ReRegister(API.ProgramName, "Win10", jwt);
                }
                else
                {
                    Tracing.TraceLine($"ConnectToSmartLink: session already registered this connection — skipping duplicate registration ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                }

                ReplayHeldListsIntoTheIntake(accountEmail, sessionWasAlreadyConnected, sw, listAtStart);

                // ONE reading of this account's list decides both shortcuts
                // below (#613, repair round 3, Codex B3-R2b): whether it is the
                // list the call found, whether this rig's rows came from it,
                // and whether it is empty. A helper that checked the list's
                // identity on one reading and its emptiness on another could
                // pair the start list's identity with a reconnect's answer.
                var listNow = session.RadioListSnapshot;
                bool foundAtStart = TheListWasFoundAtStart(listAtStart, listNow);

                // When we already hold a radio list from this session, don't make
                // the user sit through the full 10s window on the off chance the
                // server volunteers a new one — it does not resend per session.
                // "From this session" means from the connection that is live
                // now: rows taken on the connection before a reconnect are
                // not this session's list, and the live connection's push is
                // still coming (#619) — see OwnRowsMayAnswerTheConnect.
                bool haveCachedList = OwnRowsMayAnswerTheConnect(listNow, accountEmail, sw);

                // Re-entry over a session that was ALREADY live when this call
                // began and has been listed on its live connection: the one
                // list this TLS session will ever send arrived long ago, so
                // satisfy the wait from the cache IMMEDIATELY instead of
                // burning even the short window (QB Track A). The attributed
                // SessionRadioListReceived subscription stays active, so pushes
                // keep landing as refreshes through wanRadioListReceivedHandler
                // exactly as the 2026-08-06 refresh/morph flow expects.
                if (haveCachedList && foundAtStart)
                {
                    // Held rows answer only for the list the call FOUND
                    // (#613, repair round 4, Codex B3-R3b). A list that
                    // arrived during the call is answered by its push, by the
                    // one rule; rows taken from it, by this call's replay or
                    // another call's, must not answer in its place.
                    var fromRows = myRadioList.Where(r => r.IsWan && WanRadioBelongsToAccount(r.Serial, accountEmail)).ToList();
                    lock (_wanIntakeLock) latch.Radios = fromRows;
                    Tracing.TraceLine(
                        $"ConnectToSmartLink: session was already live — satisfied immediately from {fromRows.Count} cached WAN radio(s), no list wait ({sw.ElapsedMilliseconds}ms)",
                        TraceLevel.Info);
                }
                else if (!haveCachedList && foundAtStart && TheLiveListAnsweredEmpty(listNow, accountEmail, sw))
                {
                    // #613: the session's one list for this connection has
                    // ARRIVED and holds no row for this account, so waiting ten
                    // seconds for another has no possible outcome. Answer from
                    // it: an empty list, which the count below turns into
                    // NoRadios. A push that lands meanwhile is not overwritten.
                    //
                    // ONLY for a session that was live before this call. A
                    // session this call connected is pushed its list through
                    // the intake, and the snapshot can show that list a moment
                    // before the intake has taken it; answering from the
                    // snapshot then lost the server's answer, so a fresh empty
                    // list read as Unknown instead of a live absence (found in
                    // repair round 1: SmartLinkRegistrationReplayTests'
                    // fresh-push test failed one run in three). Round 2 made
                    // it exact: the empty list must be the one the call FOUND,
                    // unchanged (same session, connection and list revision), so a
                    // first list or a reconnect's list arriving during the
                    // call is waited for, not answered from the snapshot.
                    lock (_wanIntakeLock)
                    {
                        if (!latch.Received) latch.Radios = new List<Radio>();
                    }
                }
                else
                {
                const int listWaitMs = 10000;

                Tracing.TraceLine($"ConnectToSmartLink: registration sent, waiting up to {listWaitMs / 1000}s for radio list (cached={myRadioList.Count}) ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                // The latch is read through LatchReceived, which never
                // looks inside an intake: the intake writes the latch and
                // re-validates it as one step, and may take it back (#619).
                // A list whose intake is still running when the window ends
                // is settled by one read that waits for the intake to finish.
                bool listArrived = await(() => LatchReceived(latch) || session.Status == Radios.SmartLink.SessionStatus.AuthorizationExpired, listWaitMs);
                if (!listArrived)
                {
                    lock (_wanIntakeLock) listArrived = latch.Received;
                }
                if (!listArrived)
                {
                    // Only the server's answer completes a waiting call; rows
                    // this rig holds are not one (repair round 4). The case
                    // this branch once covered with rows, a re-entry over a
                    // session whose one list arrived long ago (Noel, ~80 s of
                    // apparent hanging on 2026-08-05, trace 20260805-171637),
                    // is the held list the call FOUND at its start, answered
                    // above without waiting.
                    Tracing.TraceLine($"ConnectToSmartLink: TIMED OUT waiting for radio list after {listWaitMs / 1000}s ({sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                    return SmartLinkConnectResult.ConnectFailed;
                }
                } // end fresh-session wait path

                if (session.Status == Radios.SmartLink.SessionStatus.AuthorizationExpired)
                {
                    Tracing.TraceLine($"ConnectToSmartLink: server rejected JWT during registration ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                    return SmartLinkConnectResult.AuthFailed;
                }

                // One settled read, used for everything below. The intake
                // writes the latch before it re-validates and may put it
                // back; reading it bare here could count one list and walk
                // another (#619, Sol's review of L7).
                // This call's own settled list, and the server's push to it.
                List<Radio> listed;
                lock (_wanIntakeLock)
                {
                    listed = latch.Radios ?? new List<Radio>();
                    serverPush = latch.ServerPush;
                }
                Tracing.TraceLine($"ConnectToSmartLink: radio list received! {listed.Count} radio(s), myRadioList has {myRadioList.Count} entries ({sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                ConnectionProfiler.Current?.RecordEvent("wan_radio_list", new Dictionary<string, object>
                {
                    { "count", listed.Count },
                    { "myRadioListCount", myRadioList.Count },
                    { "elapsedMs", sw.ElapsedMilliseconds }
                });
                foreach (var r in listed)
                {
                    // Ports and forwarding flags belong in this line: a radio that
                    // advertises a forwarded port nothing is listening behind
                    // fails as a bare "connect failed" 100ms later, with no clue
                    // that the router rule is the problem (Don's radio, 2026-08-05).
                    Tracing.TraceLine(
                        $"  WAN radio: serial={r.Serial} name={r.Nickname} status={r.Status} " +
                        $"tlsPort={r.PublicTlsPort} udpPort={r.PublicUdpPort} forwarded={r.IsPortForwardOn} punch={r.RequiresHolePunch}",
                        TraceLevel.Info);
                }

                if (listed.Count == 0)
                {
                    // Distinct from ConnectFailed: the session is alive and registered,
                    // the server simply has no radios for this account. Re-logging in
                    // can't fix this (and in fact triggers "Invalid state for
                    // application registration" because we're already registered).
                    Tracing.TraceLine($"ConnectToSmartLink: no radios in list ({sw.ElapsedMilliseconds}ms)", TraceLevel.Warning);
                    return SmartLinkConnectResult.NoRadios;
                }

                sw.Stop();
                Tracing.TraceLine($"ConnectToSmartLink: END success (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Info);
                return SmartLinkConnectResult.Success;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Tracing.TraceLine($"ConnectToSmartLink: EXCEPTION {ex.GetType().Name}: {ex.Message} (total {sw.ElapsedMilliseconds}ms)", TraceLevel.Error);
                return SmartLinkConnectResult.ConnectFailed;
            }
            finally
            {
                if (latch != null)
                    lock (_wanIntakeLock)
                    {
                        _openLatches.Remove(latch);
                        if (serverPush == null) serverPush = latch.ServerPush;
                    }
            }
        }

        /// <summary>
        /// The session a SmartLink connect to this serial will actually route
        /// through, and (via <paramref name="viaOwningAccount"/>) whether that
        /// is the radio's owning account rather than the active one.
        ///
        /// <para>Sprint 35 Track K (#259): a SmartLink connect must go through
        /// the session of the account that OWNS the radio — the broker only
        /// knows serials on the session's own account, so dialling Don's radio
        /// through another account's session can only fail. With one held
        /// session per account, the attributed handle map knows the owner;
        /// prefer its live session over whatever happens to be active. (This
        /// is the root of #203's two-Enter behaviour: the first Enter used to
        /// have no session that could act on the row.)</para>
        ///
        /// <para><b>One resolver, every consumer (#401).</b> The routing here
        /// was already right; the spoken connect sentence asked
        /// "_currentAccount" instead and told the operator a falsehood at the
        /// exact moment a connect was failing. Anything that needs to NAME the
        /// account a connect will use asks
        /// <see cref="AccountThatWillBroker"/>, which answers from this same
        /// decision — the words and the wire cannot diverge again.</para>
        /// </summary>
        private static Radios.SmartLink.IWanSessionOwner ResolveBrokeringSession(string serial, out bool viaOwningAccount,
            string plannedAccount = null)
        {
            viaOwningAccount = false;
            var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;

            // The account this connect's plan chose and named, and ONLY that
            // account (Codex B1 on the 2026-11-06 integration pass, and B1-R on
            // the rulings round). The serial-keyed bank below is written by
            // whichever held list was replayed LAST, so on a radio two held
            // accounts list, the plan announced one account and the wire went
            // through the other. And when the planned account's session has
            // dropped by the dial, falling through to the bank or the active
            // session dialled through another account, unannounced: a plan
            // that named an account dials through it or not at all, so the
            // answer is then null and the caller says why.
            if (!string.IsNullOrEmpty(plannedAccount))
            {
                var planned = Radios.SmartLink.SmartLinkServices.Coordinator.GetSessionForAccount(plannedAccount);
                if (planned == null || !planned.IsConnected) return null;
                viaOwningAccount = session == null
                    || !string.Equals(session.AccountId, plannedAccount, StringComparison.OrdinalIgnoreCase);
                return planned;
            }

            string owningAccount = GetWanAccountForSerial(serial);
            if (!string.IsNullOrEmpty(owningAccount)
                && (session == null
                    || !string.Equals(session.AccountId, owningAccount, StringComparison.OrdinalIgnoreCase)))
            {
                var owningSession = Radios.SmartLink.SmartLinkServices.Coordinator.GetSessionForAccount(owningAccount);
                if (owningSession != null && owningSession.IsConnected)
                {
                    viaOwningAccount = true;
                    session = owningSession;
                }
            }
            return session;
        }

        /// <summary>
        /// The account email a SmartLink connect to this serial will actually
        /// go through — the answer the connect ANNOUNCEMENT must speak (#401).
        /// Falls back to <paramref name="activeAccountEmail"/> when no held
        /// session can broker the serial, because that is then genuinely the
        /// account the attempt will use.
        /// </summary>
        public static string AccountThatWillBroker(string serial, string activeAccountEmail,
            IEnumerable<string> savedAccounts = null)
        {
            try
            {
                // The #798 plan decides every SmartLink connect now, the
                // fast path included (Codex F2), so the account to name is
                // the plan's, from one reading (integration pass, finding 4;
                // #401's class), and the wire uses that same account (Codex
                // B1).
                string planned = ConnectAccountFor(serial, savedAccounts);
                if (planned.Length > 0) return planned;
                // Nothing names the radio's account. With a session live the
                // connect signs in to nothing and the wire resolves as it
                // always did; name what it will resolve.
                if (Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession?.IsConnected == true)
                {
                    var session = ResolveBrokeringSession(serial, out _);
                    if (session != null && !string.IsNullOrEmpty(session.AccountId))
                        return session.AccountId;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"AccountThatWillBroker({serial}): {ex.Message}", TraceLevel.Warning);
            }
            return activeAccountEmail ?? "";
        }

        /// <summary>
        /// The account a SmartLink connect to <paramref name="serial"/> would
        /// actually use, by the connect's own plan: the held session whose
        /// live list names the radio, else the first of its chosen and
        /// last-seen accounts that is saved on this computer. EMPTY when the
        /// plan names no account (no saved account lists the radio, or the
        /// connect would sign in through the saved default), and on any error.
        /// </summary>
        /// <remarks>
        /// <para>#405, ruled by Noel 2026-11-08 ~16:28: the radio list's
        /// "another account" flag and the Radio Associations row name "the
        /// account connecting will actually use (the first matching account
        /// saved on this computer), not the one chosen or last seen". So they
        /// read THIS, which is <see cref="PlanSmartLinkSignIn"/> and
        /// <see cref="AccountPicture.FirstSaved"/> through
        /// <see cref="RadiosOwnAccount"/>: the one decision the connect, the
        /// announcement, the wire and registration already share, and no third
        /// tier walk.</para>
        /// <para>Reads the radio's own record from disk and the held sessions,
        /// so a caller painting many rows asks it outside any list lock.</para>
        /// </remarks>
        /// <param name="savedAccounts">The saved accounts; null reads the account manager.</param>
        public static string ConnectAccountFor(string serial, IEnumerable<string> savedAccounts = null)
        {
            if (string.IsNullOrWhiteSpace(serial)) return "";
            try
            {
                var saved = savedAccounts ?? AccountManager.Accounts.Where(a => a != null).Select(a => a.Email);
                return RadiosOwnAccount(PlanSmartLinkSignIn(TakeAccountPictureFor(serial, saved: saved)));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectAccountFor({serial}): {ex.Message}", TraceLevel.Warning);
                return "";
            }
        }

        /// <summary>
        /// Whether the active SmartLink session is live AND its current list
        /// names <paramref name="serial"/>: the state the old fast path stood
        /// on. It no longer decides anything (Codex F2 on the 2026-11-06
        /// integration pass: the fast path skipped adopting the account and
        /// the shared-radio tie-break, so it could reach a different account
        /// than the plan). <see cref="ReconnectRemote"/> asks it only to say
        /// in the trace which route the plan is taking.
        /// </summary>
        internal static bool TheActiveSessionListsTheRadio(string serial)
        {
            var active = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;
            if (active == null || !active.IsConnected) return false;
            var s = active.RadioListSnapshot;
            return s.SessionConnected && s.ArrivedOnTheLiveConnection
                && s.Radios != null
                && s.Radios.Any(r => r != null && string.Equals(r.Serial, serial, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// THE answer to "which account is this radio's, for this connect":
        /// the account a plan uses (its held session, or a sign-in as the
        /// radio's own account), else empty. The connect's preparation acts on
        /// it, the announcement names it, the wire resolves through it, and
        /// registration registers to it (Codex B1, F1 and F2 on the
        /// 2026-11-06 integration pass): one decision, every consumer.
        /// </summary>
        internal static string RadiosOwnAccount(SmartLinkSignInPlan plan)
            => plan.Kind == SmartLinkSignInPlanKind.UseHeldSession
               || plan.Kind == SmartLinkSignInPlanKind.SignInAsRadiosAccount
                ? plan.Account ?? ""
                : "";

        /// <summary>
        /// The account this rig's plan chose for a connect to a serial, kept
        /// so the dial goes through the account the plan named (Codex B1).
        /// Set by <see cref="PrepareSmartLinkForSerial"/>; read by
        /// <see cref="BrokeringSessionForConnect"/>, and only for the same
        /// serial, so a later connect to another radio never inherits it.
        /// </summary>
        private (string Serial, string Account) _connectPlanAccount;

        /// <summary>
        /// The session the dial to <paramref name="serial"/> goes through:
        /// this rig's plan's account when it chose one for this serial, else
        /// the long-standing resolution (the owning account's live session,
        /// then the active one).
        /// </summary>
        internal Radios.SmartLink.IWanSessionOwner BrokeringSessionForConnect(string serial, out bool viaOwningAccount)
            => BrokeringSessionForConnect(serial, out viaOwningAccount, out _);

        /// <summary>
        /// As above; <paramref name="plannedAccount"/> is the account this
        /// rig's plan named for the serial, or null when it named none. A null
        /// session with a planned account means that account's session is
        /// gone, and the dial must not go out through any other (B1-R).
        /// </summary>
        internal Radios.SmartLink.IWanSessionOwner BrokeringSessionForConnect(string serial, out bool viaOwningAccount,
            out string plannedAccount)
        {
            var planned = _connectPlanAccount;
            plannedAccount = !string.IsNullOrEmpty(planned.Account)
                && string.Equals(planned.Serial, serial, StringComparison.OrdinalIgnoreCase)
                ? planned.Account : null;
            return ResolveBrokeringSession(serial, out viaOwningAccount, plannedAccount);
        }

        /// <summary>
        /// The report filed when the dial refused because the planned
        /// account's session dropped (B1-R), kept so the connect's general
        /// failure report does not replace the true reason. Cleared at each
        /// dial.
        /// </summary>
        private ConnectFailureReport _dialRefusedReport;

        /// <summary>What a SmartLink connect to one serial should do about signing in.</summary>
        internal enum SmartLinkSignInPlanKind
        {
            /// <summary>Nothing better is known: sign in through the saved default account, as before.</summary>
            SignInAsUsual,

            /// <summary>A held session already lists the radio on its live connection: use it, sign in to nothing.</summary>
            UseHeldSession,

            /// <summary>The radio's own account is known and saved, but its session cannot answer now: sign in as THAT account.</summary>
            SignInAsRadiosAccount,

            /// <summary>Every saved account's list has arrived on its live connection and none lists the radio: say so, sign in to nothing.</summary>
            NotListedByAnySavedAccount,
        }

        /// <summary>The plan, and the account it names (empty for the default path).</summary>
        internal readonly record struct SmartLinkSignInPlan(SmartLinkSignInPlanKind Kind, string Account, string Why)
        {
            public override string ToString() => $"{Kind}{(string.IsNullOrEmpty(Account) ? "" : " " + Account)}: {Why}";
        }

        /// <summary>
        /// One held session's list, as one immutable reading: which account,
        /// which session and connection, whether the session is connected and
        /// its list arrived on that connection, when that list arrived, and
        /// whether it names the serial asked about.
        /// </summary>
        internal readonly record struct AccountListView(
            string Account, string SessionId, long Generation, bool Connected, bool Arrived,
            long Revision, bool ListsSerial)
        {
            /// <summary>This account's list answers now: connected, and arrived on that connection.</summary>
            public bool Answers => Connected && Arrived;
        }

        /// <summary>
        /// EVERYTHING a SmartLink connect decides its account from, read once:
        /// each held session's list (<see cref="AccountListView"/>), the
        /// radio's chosen and last-seen accounts, and whether it is on the LAN.
        /// The plan is a pure function of this, and the action uses this, not
        /// a second look at live state (#798, repair round 2).
        /// </summary>
        internal sealed record AccountPicture(
            string Serial, IReadOnlyList<AccountListView> Lists, string Preferred, string LastSeen, bool OnLan,
            IReadOnlyList<string> Saved)
        {
            /// <summary>
            /// The account whose list answers now and names the radio: live
            /// attribution, from the session's CURRENT list, never from the WAN
            /// bank (which can lag a newer list on the same connection, Codex
            /// B1). A club rig on two accounts: the chosen account, then the
            /// last-seen one, then the first by name, so the answer never
            /// depends on the order sessions were made in (Codex F2).
            /// </summary>
            public string LiveAccount
            {
                get
                {
                    var listing = Lists.Where(l => l.Answers && l.ListsSerial).Select(l => l.Account).ToList();
                    if (listing.Count == 0) return "";
                    string pick(string want) => listing.FirstOrDefault(a => !string.IsNullOrWhiteSpace(want)
                        && string.Equals(a, want, StringComparison.OrdinalIgnoreCase));
                    return pick(Preferred) ?? pick(LastSeen)
                        ?? listing.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).First();
                }
            }

            /// <summary>The routing answer this picture gives: live, then chosen, then last seen, then none.</summary>
            public AccountRoute Route => RadioAccountRouting.Resolve(LiveAccount, Preferred, LastSeen);

            /// <summary>
            /// The first of <paramref name="tiers"/>, in order, that is a saved
            /// account; empty when none is. THE tier walk for "which saved
            /// account is this radio's": the connect's plan walks the chosen
            /// then the last-seen account through it. Registration no longer
            /// walks it separately: it takes the plan's own answer
            /// (<see cref="RadiosOwnAccount"/>, Codex F1), so one radio
            /// resolves to one account.
            /// </summary>
            public string FirstSaved(params string[] tiers)
                => tiers.FirstOrDefault(IsSaved) ?? "";

            /// <summary>
            /// Whether an account is one of this picture's saved accounts.
            /// </summary>
            public bool IsSaved(string account)
                => !string.IsNullOrWhiteSpace(account)
                   && (Saved ?? Array.Empty<string>()).Any(s => string.Equals(s, account, StringComparison.OrdinalIgnoreCase));

            /// <summary>
            /// True when <paramref name="other"/> describes the same moment:
            /// the same lists (account, session, connection, connected,
            /// arrived, arrival time, membership), the same chosen and
            /// last-seen accounts, and the same saved accounts (repair round
            /// 3, Codex B2-R2: an account saved between the readings has not
            /// answered, and the plan must see it). Two readings that differ
            /// mean something was received in between, and two equal
            /// readings mean nothing was: each list carries its session's
            /// list revision, which every accepted list advances (repair
            /// round 4, settling Codex F4; the wall-clock arrival time is no
            /// longer compared).
            /// </summary>
            public bool SameAs(AccountPicture other)
                => other != null
                   && string.Equals(Serial, other.Serial, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Preferred ?? "", other.Preferred ?? "", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(LastSeen ?? "", other.LastSeen ?? "", StringComparison.OrdinalIgnoreCase)
                   && OnLan == other.OnLan
                   && Lists.SequenceEqual(other.Lists)
                   && (Saved ?? Array.Empty<string>()).SequenceEqual(other.Saved ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Read the picture once from the held sessions and the given chosen
        /// and last-seen accounts. Each session's list is one snapshot.
        /// </summary>
        internal static AccountPicture TakeAccountPicture(string serial,
            IEnumerable<Radios.SmartLink.IWanSessionOwner> sessions, string preferred, string lastSeen, bool onLan,
            IEnumerable<string> saved = null)
        {
            var lists = new List<AccountListView>();
            foreach (var held in sessions ?? Array.Empty<Radios.SmartLink.IWanSessionOwner>())
            {
                if (held == null || string.IsNullOrWhiteSpace(held.AccountId)) continue;
                var s = held.RadioListSnapshot;
                bool lists_ = s.Radios != null
                    && s.Radios.Any(r => r != null && string.Equals(r.Serial, serial, StringComparison.OrdinalIgnoreCase));
                lists.Add(new AccountListView(held.AccountId, s.SessionId ?? "", s.ConnectionGeneration,
                    s.SessionConnected, s.ArrivedOnTheLiveConnection, s.ListRevision, lists_));
            }
            lists.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Account, b.Account));
            var savedNames = (saved ?? Array.Empty<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new AccountPicture(serial ?? "", lists, preferred ?? "", lastSeen ?? "", onLan, savedNames);
        }

        /// <summary>The picture for a serial now, from the coordinator's sessions, the radio's own record and these saved accounts.</summary>
        internal AccountPicture TakeAccountPicture(string serial, IEnumerable<string> saved)
            => TakeAccountPictureFor(serial, RadioAvailability(serial).lan, saved);

        /// <summary>
        /// The picture without a rig: no LAN reading (false). Without
        /// <paramref name="saved"/> the picture carries no saved accounts,
        /// which its <see cref="AccountPicture.Route"/> does not use.
        /// </summary>
        internal static AccountPicture TakeAccountPictureFor(string serial, bool onLan = false, IEnumerable<string> saved = null)
        {
            string preferred = "", lastSeen = "";
            try
            {
                var cfg = RadioConfig.LoadForRadio(serial);
                preferred = cfg.PreferredAccount ?? "";
                lastSeen = cfg.LastSeenViaAccount ?? "";
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"TakeAccountPicture({serial}): per-radio record unreadable: {ex.Message}", TraceLevel.Warning);
            }
            return TakeAccountPicture(serial, Radios.SmartLink.SmartLinkServices.Coordinator.AllSessions,
                preferred, lastSeen, onLan, saved);
        }

        /// <summary>
        /// The plan, a pure function of one picture, its saved accounts
        /// included (#798; the rules are in the 2026-11-06 repair-2 and
        /// repair-3 reports):
        /// <list type="number">
        /// <item>An account's list answers now and names the radio: use that
        /// held session (<see cref="AccountPicture.LiveAccount"/>).</item>
        /// <item>Every saved account's list answers now, and none names the
        /// radio: say so. Any saved account without an answering list makes
        /// this unknown, and the plan falls through.</item>
        /// <item>The radio's chosen account is saved: sign in as it; else its
        /// last-seen account is saved: sign in as that.</item>
        /// <item>Otherwise sign in as before, through the saved default.</item>
        /// </list>
        /// </summary>
        internal static SmartLinkSignInPlan PlanSmartLinkSignIn(AccountPicture picture)
        {
            string live = picture.LiveAccount;
            if (live.Length > 0)
                return new SmartLinkSignInPlan(SmartLinkSignInPlanKind.UseHeldSession, live,
                    "its held session lists the radio on its live connection");

            var saved = picture.Saved ?? Array.Empty<string>();
            bool IsSaved(string a) => picture.IsSaved(a);

            if (saved.Count > 0 && saved.All(a => picture.Lists.Any(l => l.Answers
                    && string.Equals(l.Account, a, StringComparison.OrdinalIgnoreCase))))
                return new SmartLinkSignInPlan(SmartLinkSignInPlanKind.NotListedByAnySavedAccount, "",
                    $"all {saved.Count} saved account(s) answer on their live connections, and none lists the radio");

            string own = picture.FirstSaved(picture.Preferred, picture.LastSeen);
            if (own.Length > 0)
                return new SmartLinkSignInPlan(SmartLinkSignInPlanKind.SignInAsRadiosAccount, own,
                    string.Equals(own, picture.Preferred, StringComparison.OrdinalIgnoreCase)
                        ? "the radio's chosen account has no live list that answers"
                        : "the account that last listed the radio has no live list that answers");

            return new SmartLinkSignInPlan(SmartLinkSignInPlanKind.SignInAsUsual, "",
                "no saved account is known to reach the radio, and not every saved account answers");
        }

        /// <summary>
        /// For a windowless test: runs between the two readings of the picture
        /// in <see cref="PrepareSmartLinkForSerial"/>. Null in the product.
        /// </summary>
        internal Action AfterAccountPictureTaken;

        /// <summary>How many times the preparation re-reads a picture that changed under it before giving up on a verdict.</summary>
        internal const int PictureReadAttempts = 3;

        /// <summary>
        /// The SmartLink half of <see cref="ReconnectRemote"/> when no session
        /// is active: decide from ONE picture and act on THAT picture.
        /// </summary>
        /// <remarks>
        /// <para><b>Read twice, act only on a steady reading (repair rounds
        /// 2 and 3).</b> The picture is read, then read again; the plan is made and
        /// acted on only when the two are the same, which means nothing was
        /// received in between, so every list in it held at one moment. A
        /// list that arrived while the lists were being read, a reconnect, a
        /// change of the radio's chosen account, or an account saved meanwhile
        /// makes the readings differ, and the picture is taken again. If it
        /// never steadies, no plan is made from it at all: the preparation
        /// falls back to the usual sign-in, which only costs time. A change not yet
        /// received when the reading is made cannot be known, and is not
        /// guarded against.</para>
        /// </remarks>
        /// <param name="savedAccounts">The saved accounts; null reads the
        /// account manager, as production always does. A test passes its own
        /// so it never edits the process-wide account store.</param>
        internal bool PrepareSmartLinkForSerial(string serial, bool allowInteractiveLogin,
            System.Diagnostics.Stopwatch sw = null, IReadOnlyList<SmartLinkAccount> savedAccounts = null)
        {
            string at() => sw == null ? "" : $" ({sw.ElapsedMilliseconds}ms)";
            var coordinator = Radios.SmartLink.SmartLinkServices.Coordinator;
            var savedSource = savedAccounts ?? AccountManager.Accounts;

            // The saved accounts are read WITH each picture, and the account
            // objects kept from the reading the plan is made from (repair
            // round 3, Codex B2-R2): an account saved between two readings
            // makes them differ, and a steady picture's saved set is the one
            // the action signs in from.
            List<SmartLinkAccount> savedNow = null;
            AccountPicture Read()
            {
                savedNow = savedSource.Where(a => a != null).ToList();
                return TakeAccountPicture(serial, savedNow.Select(a => a.Email));
            }

            AccountPicture picture = null;
            SmartLinkSignInPlan plan = default;
            bool steady = false;
            for (int attempt = 0; attempt < PictureReadAttempts && !steady; attempt++)
            {
                picture = Read();
                var savedAtPicture = savedNow;
                AfterAccountPictureTaken?.Invoke();
                steady = Read().SameAs(picture);
                savedNow = savedAtPicture;
                if (!steady)
                    Tracing.TraceLine($"PrepareSmartLinkForSerial: {serial} the accounts changed while being read; reading again{at()}", TraceLevel.Info);
            }
            if (steady)
            {
                plan = PlanSmartLinkSignIn(picture);
            }
            else
            {
                // No steady picture: no plan is made from one (repair round 3,
                // Codex F3). Every plan, not only the verdict of absence,
                // would act on a reading already known to be out of date; the
                // usual sign-in is the path that decides nothing from it.
                plan = new SmartLinkSignInPlan(SmartLinkSignInPlanKind.SignInAsUsual, "",
                    "the accounts kept changing while being read; nothing is decided from an unsteady picture");
            }
            Tracing.TraceLine($"PrepareSmartLinkForSerial: {serial} route={picture.Route} plan={plan} steady={steady}{at()}", TraceLevel.Info);

            // The dial goes through the account this plan names, never
            // through whichever held list the replay below wrote last (Codex
            // B1). Empty when the plan names none, and the resolution is then
            // the long-standing one.
            _connectPlanAccount = (serial, RadiosOwnAccount(plan));

            switch (plan.Kind)
            {
                case SmartLinkSignInPlanKind.UseHeldSession:
                {
                    // The session that lists the radio becomes the one this
                    // connect works through, exactly as a sign-in under that
                    // account would have made it; nothing signs in.
                    var held = coordinator.EnsureSessionForAccount(plan.Account);
                    // The connection's own account is now the held one, as a
                    // sign-in under it would have made it (integration pass,
                    // finding 3): the Network tab's port preference, the
                    // legacy hole-punch port and the session key read it.
                    // The account object comes from the same reading the plan
                    // was made from; a held account nobody saved is adopted
                    // for this session only.
                    // This rig's own sign-in for the same account is kept, with its
                    // tokens, before an email-only stand-in is made (round two,
                    // Codex F1-R: a session-only sign-in lost its tokens here).
                    AdoptSignedInAccount(
                        savedNow.FirstOrDefault(a => string.Equals(a.Email, plan.Account, StringComparison.OrdinalIgnoreCase))
                        ?? (_currentAccount != null && string.Equals(_currentAccount.Email, plan.Account, StringComparison.OrdinalIgnoreCase)
                            ? _currentAccount : null)
                        ?? new SmartLinkAccount { Email = plan.Account, FriendlyName = plan.Account });
                    becomeThePresenceIntake();
                    int replayed = ReplayHeldListsIntoTheIntake(plan.Account, sessionWasAlreadyConnected: true, sw);
                    Tracing.TraceLine(
                        $"PrepareSmartLinkForSerial: using {plan.Account}'s held session {held.SessionId}, no sign-in; replayed {replayed} held list(s){at()}",
                        TraceLevel.Info);
                    return true;
                }

                case SmartLinkSignInPlanKind.NotListedByAnySavedAccount:
                {
                    // The LAN reading is the picture's, taken with the lists.
                    string name = RadioNameForSpeech(serial);
                    RecordConnectFailure(new ConnectFailureReport
                    {
                        Class = ConnectFailureClass.RadioNotFound,
                        SpokenSummary = picture.OnLan
                            ? Lexicon.Get("connect.failure.smartlink_not_on_any_account_but_local", ("radioName", name))
                            : Lexicon.Get("connect.failure.smartlink_not_on_any_account", ("radioName", name)),
                    });
                    return false;
                }

                case SmartLinkSignInPlanKind.SignInAsRadiosAccount:
                {
                    // From the same reading the plan was made from.
                    var account = savedNow.FirstOrDefault(a =>
                        string.Equals(a.Email, plan.Account, StringComparison.OrdinalIgnoreCase));
                    return setupRemote(allowInteractiveLogin, account);
                }

                default:
                    // Nothing better is known. With a SmartLink session already
                    // live, that is the path ReconnectRemote always took: no
                    // sign-in, straight on to finding the radio through the
                    // sessions that are up. Only with none live does the saved
                    // default sign in (integration pass, finding 6: the plan
                    // now runs with a live session, and must not add a sign-in
                    // prompt to the case it has nothing to say about).
                    if (coordinator.ActiveSession?.IsConnected == true)
                    {
                        Tracing.TraceLine($"PrepareSmartLinkForSerial: {serial} nothing better known and a session is live — no sign-in{at()}", TraceLevel.Info);
                        return true;
                    }
                    return setupRemote(allowInteractiveLogin);
            }
        }

        /// <summary>
        /// The operator's name for a radio known only by serial: the chosen
        /// name, else the radio's own, else the serial.
        /// </summary>
        private static string RadioNameForSpeech(string serial)
        {
            try
            {
                var name = RadioConfig.LoadForRadio(serial).DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"RadioNameForSpeech({serial}): {ex.Message}", TraceLevel.Warning);
            }
            return serial;
        }

        private bool sendRemoteConnect(Radio r)
        {
            Tracing.TraceLine("sendRemoteConnect: " + r.Serial, TraceLevel.Info);
            // Also cleared here because RetryConnect calls this without going
            // through Connect() — and each retry re-loads the per-radio profile,
            // so a Settings edit made between attempts is honored.
            LastConnectFailureReport = null;
            ConnectionProfiler.Current?.RecordEvent("send_remote_connect", new Dictionary<string, object>
            {
                { "serial", r.Serial }
            });

            _dialRefusedReport = null;
            var session = BrokeringSessionForConnect(r.Serial, out bool viaOwningAccount, out string plannedAccount);
            if (session == null && plannedAccount != null)
            {
                // B1-R: the account the plan named and announced has lost its
                // session since the preparation. Dialling through another
                // account would connect as someone the operator was not told
                // about, so nothing is sent; the reason is filed the way the
                // plan files every failed connect, and connecting again plans,
                // and names the account, afresh.
                Tracing.TraceLine(
                    $"sendRemoteConnect: REFUSED, the planned account {plannedAccount}'s session is not connected; not dialling {r.Serial} through any other account",
                    TraceLevel.Warning);
                RecordConnectFailure(new ConnectFailureReport
                {
                    Class = ConnectFailureClass.SessionSetupFailed,
                    SpokenSummary = Lexicon.Get("connect.failure.smartlink_planned_account_dropped",
                        ("email", plannedAccount), ("radioName", RadioNameForSpeech(r.Serial))),
                });
                _dialRefusedReport = LastConnectFailureReport;
                return false;
            }
            if (viaOwningAccount)
            {
                Tracing.TraceLine(
                    $"sendRemoteConnect: routing through owning account's session ({session.SessionId}) instead of active",
                    TraceLevel.Info);
            }
            if (session == null)
            {
                Tracing.TraceLine("sendRemoteConnect: no active session", TraceLevel.Error);
                return false;
            }

            // Hole-punch port selection.
            //
            // The radio itself tells us whether hole punch is required — SmartLink
            // sets Radio.RequiresHolePunch in the radio-list message. That flag, not
            // our local account config, is the authority: a network that needs hole
            // punch needs it regardless of which tier the user picked.
            //
            // IMPORTANT — "NegotiatedHolePunchPort" is a misnomer in FlexLib. Nothing
            // ever assigns it: it's initialised to -1 in WanServer's radio-list parse
            // and read (never written) by Radio.Connect and the VitaSocket ctor, in
            // both 4.0.1 and 4.2.20. Picking the port is the CLIENT's job. SmartSDR
            // does exactly this — `if (radio.RequiresHolePunch)
            // radio.NegotiatedHolePunchPort = random.Next(25000, 65000);` — then
            // advertises the same number. If we leave it at -1, FlexLib calls
            // Connect(ip, -1, -1) and the connect cannot succeed.
            //
            // A FRESH port per connect is deliberate, not lazy: reusing a port risks
            // colliding with a stale NAT mapping from a previous session, which makes
            // hole punch fail intermittently and unreproducibly.
            //
            // A configured listen port is for the port-FORWARD path (Tier 1/2), where
            // the user has told their router about a specific stable port. When
            // forwarding works SmartLink reports RequiresHolePunch = false, so the two
            // paths are mutually exclusive in practice. We honour an explicit
            // configured port if one exists, and randomise otherwise.
            // Per-radio profile (barefoot-punch-pathfinder Phase 1a). Reachability
            // is a property of the radio's SITE, not the operator's account —
            // Don's radio is forwarded while the 8600 needs punch, and one
            // account-level tier can't describe both. The serial-keyed profile
            // therefore outranks the account fields. Auto (the default, and the
            // state of every radio without a saved profile) follows the
            // radio-reported RequiresHolePunch flag exactly as before, so
            // pre-profile behavior is unchanged.
            var radioProfile = RadioConfig.LoadForRadio(r.Serial);
            if (radioProfile.ConnectionPreference == RadioConnectionPreference.HolePunch
                && !r.RequiresHolePunch)
            {
                Tracing.TraceLine(
                    "sendRemoteConnect: per-radio profile FORCES hole punch (radio reported RequiresHolePunch=false)",
                    TraceLevel.Warning);
                r.RequiresHolePunch = true;
            }
            else if (radioProfile.ConnectionPreference == RadioConnectionPreference.ForwardOnly
                && r.RequiresHolePunch)
            {
                if (r.PublicTlsPort <= 0)
                {
                    // Fail fast (QB Track C item 6): ForwardOnly forbids the punch
                    // path, and SmartLink advertises no public port for this radio —
                    // there is literally no address:port a forwarded connect could
                    // try. Attempting anyway is the old behavior: tens of seconds of
                    // silent grinding ending in a bare "connection failed". Refuse
                    // up front and say why, filed as a pre-attempt refusal in
                    // Track D's failure classification.
                    RecordConnectFailure(new ConnectFailureReport
                    {
                        Class = ConnectFailureClass.PreflightRefused,
                        SpokenSummary =
                            "Not connecting. This radio is set to use forwarded ports only, " +
                            "but SmartLink reports no forwarded port is reachable for it, so there is nothing to connect to. " +
                            "Set up port forwarding at the radio's site, or change this radio's connection setting " +
                            "on the Radios tab in Settings — hole punch there needs no port forwarding.",
                    });
                    Tracing.TraceLine(
                        "sendRemoteConnect: FAIL FAST — per-radio profile is ForwardOnly, radio reported RequiresHolePunch=true and no public TLS port; refusing the doomed attempt",
                        TraceLevel.Error);
                    ConnectionProfiler.Current?.RecordEvent("forward_only_fail_fast", new Dictionary<string, object>
                    {
                        { "serial", r.Serial },
                        { "publicTlsPort", r.PublicTlsPort },
                        { "publicUdpPort", r.PublicUdpPort }
                    });
                    return false;
                }

                // Public ports ARE advertised — the user said the radio's punch
                // flag is wrong and there is a real address:port to try, which is
                // exactly the escape hatch the mode's description promises.
                Tracing.TraceLine(
                    $"sendRemoteConnect: per-radio profile FORCES forwarded path (radio reported RequiresHolePunch=true, PublicTlsPort={r.PublicTlsPort}) — connect will fail if the radio has no reachable public ports",
                    TraceLevel.Warning);
                r.RequiresHolePunch = false;
            }

            int holePunchPort = 0;
            string holePunchPortSource = "none";
            if (r.RequiresHolePunch)
            {
                if (radioProfile.FixedHolePunchPort > 0)
                {
                    holePunchPort = radioProfile.FixedHolePunchPort;
                    holePunchPortSource = "radioProfile";
                    Tracing.TraceLine(
                        $"sendRemoteConnect: hole punch required — using per-radio fixed port {holePunchPort}",
                        TraceLevel.Info);
                }
                else if (_currentAccount != null
                    && _currentAccount.ConnectionMode == SmartLinkConnectionMode.AutomaticHolePunch
                    && _currentAccount.ConfiguredListenPort.HasValue)
                {
                    // Legacy account-level pin, kept as a fallback for configs
                    // written before per-radio profiles existed — including the
                    // documented hand-edit interim unblocker (connectionMode 2 +
                    // configuredListenPort in SmartLinkAccounts.json). No UI
                    // writes this meaning anymore (QB Track C untangle); the
                    // per-radio FixedHolePunchPort above outranks it whenever
                    // set. Known wart, accepted: a value written by port-forward
                    // Apply (the field's forward meaning) can land here as a
                    // punch port if the router rule later breaks — semantically
                    // wrong but functionally harmless, since any port number
                    // punches equally well.
                    holePunchPort = _currentAccount.ConfiguredListenPort.Value;
                    holePunchPortSource = "account";
                    Tracing.TraceLine(
                        $"sendRemoteConnect: hole punch required — using configured port {holePunchPort}",
                        TraceLevel.Info);
                }
                else
                {
                    holePunchPort = System.Random.Shared.Next(25000, 65000);
                    holePunchPortSource = "random";
                    Tracing.TraceLine(
                        $"sendRemoteConnect: hole punch required — auto-assigned port {holePunchPort}",
                        TraceLevel.Info);
                }

                // The value FlexLib actually reads at connect time. Without this the
                // whole hole-punch path is dead.
                r.NegotiatedHolePunchPort = holePunchPort;
            }

            else if (_currentAccount != null
                && _currentAccount.ConnectionMode == SmartLinkConnectionMode.AutomaticHolePunch
                && _currentAccount.ConfiguredListenPort.HasValue)
            {
                // Tier 3 selected by the user but the radio didn't ask for hole punch.
                // Advertise the configured port anyway — harmless, and preserves the
                // Sprint 27 Track F behaviour.
                holePunchPort = _currentAccount.ConfiguredListenPort.Value;
                Tracing.TraceLine(
                    $"sendRemoteConnect: Tier 3 mode, radio did not require hole punch — advertising {holePunchPort}",
                    TraceLevel.Info);
            }

            // Record what actually happened so the UI can report the real port
            // rather than the configured one — the two differ whenever the radio
            // did not ask for a hole-punch.
            LastHolePunchPort = holePunchPort;

            ConnectionProfiler.Current?.RecordEvent("hole_punch_port_selected", new Dictionary<string, object>
            {
                { "requiresHolePunch", r.RequiresHolePunch },
                { "holePunchPort", holePunchPort },
                { "portSource", holePunchPortSource },
                { "radioProfilePreference", radioProfile.ConnectionPreference.ToString() }
            });

            // session.ConnectToRadio returns Task<string?>: handle on success, null on timeout/failure.
            // We block synchronously to preserve the existing caller contract; the session owner's
            // internal TCS does the actual awaiting off-thread.
            var task = session.ConnectToRadio(r.Serial, holePunchPort);
            if (!task.Wait(5000))
            {
                Tracing.TraceLine("sendRemoteConnect:Radio not ready for connect (timeout).", TraceLevel.Error);
                ConnectionProfiler.Current?.RecordEvent("send_remote_connect_timeout", new Dictionary<string, object>
                {
                    { "serial", r.Serial }
                });
                return false;
            }

            var handle = task.Result;
            if (string.IsNullOrEmpty(handle))
            {
                Tracing.TraceLine("sendRemoteConnect:Radio connect returned null handle.", TraceLevel.Error);
                return false;
            }

            r.WANConnectionHandle = handle;
            rememberCommandRoute(r, handle, session.AccountId);

            // Refresh the per-radio profile stub on every successful WAN
            // connect: keeps the nickname current and guarantees every radio
            // ever connected shows up in the Settings per-radio picker with
            // zero setup. Load/Save never throw; an unset BaseDirectory just
            // traces and declines.
            string observedNickname = r.Nickname;
            RadioConfig.Update(r.Serial, profileStub => {
                if (!string.IsNullOrEmpty(observedNickname))
                    profileStub.Nickname = observedNickname;
            });

            ConnectionProfiler.Current?.RecordEvent("wan_connect_ready", new Dictionary<string, object>
            {
                { "serial", r.Serial }
            });
            return true;
        }
        #endregion
    }
}
