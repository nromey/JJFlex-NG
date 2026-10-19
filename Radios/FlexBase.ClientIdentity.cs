using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The GUI client identity JJ Flexible presents to the radio (#782): what
    /// is handed to <c>Radio.Connect</c> on both connect paths, what the radio
    /// reports back, the one refusal the radio can make, and the post-connect
    /// fence a RETURNING connect's station phase waits for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both <c>Connect</c> and <c>RetryConnect</c> call
    /// <see cref="prepareClientIdentityForConnect"/> for the argument to
    /// <c>Radio.Connect</c>; nothing else chooses the id. Jim's reconnect
    /// under <c>#if zero</c> in <c>onRadioConnectedChanged</c> passed the
    /// radio-reported id back on a drop; it is left disabled and untouched —
    /// this is the live version of that idea, with the id persisted.
    /// </para>
    /// <para>
    /// The slot that keys the id file is <see cref="GuiClientSlot.Current"/>,
    /// held by the process; the refusal memory is
    /// <see cref="GuiClientIdentityRefusals"/>, also the process's. Nothing
    /// identity-shaped lives on the rig except the current attempt's
    /// <see cref="ClientIdentity"/>, because the application replaces its rig
    /// on reconnect and retry.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        private readonly Dictionary<Radio, EventHandler> _duplicateClientIdHandlers = new();

        /// <summary>
        /// The identity of the current (or most recent) connection attempt:
        /// what was presented, what the radio reported, whether it was
        /// refused, and the two questions about an id —
        /// <see cref="GuiClientIdentity.IsThisConnectionsId"/> (ownership on
        /// this connection) and
        /// <see cref="GuiClientIdentity.IsAnIdThisCopyHasPresentedOrStored"/>
        /// (history). Null before the first connect attempt of this rig.
        /// </summary>
        public GuiClientIdentity ClientIdentity { get; private set; }

        /// <summary>
        /// Whether this rig presents the stored GUI client id and stores the
        /// radio's mint. True for the operator's rig. False makes every
        /// connect a session-only identity: the Connection Tester and the
        /// radio-in-the-loop harness build throwaway rigs beside the live
        /// connection, and a second rig presenting the live one's id would
        /// make the radio refuse one of the two as a duplicate — and nothing
        /// says which.
        /// </summary>
        public bool UseStoredGuiClientId { get; set; } = true;

        /// <summary>
        /// Choose the id for this attempt and say so in the trace. Returns
        /// what <c>Radio.Connect</c> is handed: the stored id, or null for
        /// "radio, mint one".
        /// </summary>
        internal string prepareClientIdentityForConnect(string why)
        {
            var radio = theRadio;
            string serial = radio?.Serial ?? "";
            int slot = UseStoredGuiClientId ? GuiClientSlot.Current.Number : 0;
            var identity = GuiClientIdentity.ForConnect(serial, slot, UseStoredGuiClientId);
            ClientIdentity = identity;
            ClearConnectFence();
            Tracing.TraceLine("GuiClientIdentity: " + why + " — " + identity.DescribePresentation(), TraceLevel.Info);
            return identity.PresentedId;
        }

        /// <summary>The radio has told us our own client's id, by any of its
        /// three routes. Records it, stores a fresh mint, and traces once.</summary>
        private void noteOwnClientIdReported(string clientId, string source)
        {
            var identity = ClientIdentity;
            if (identity == null || string.IsNullOrWhiteSpace(clientId)) return;
            string line = identity.RadioReported(clientId);
            if (line != null) Tracing.TraceLine("GuiClientIdentity: " + line + " (via " + source + ")", TraceLevel.Info);
        }

        /// <summary>
        /// FlexLib saw <c>client &lt;our handle&gt; disconnected ... duplicate_client_id=1</c>:
        /// the radio refused the id we presented because a live connection
        /// already holds it, and it is closing this one. The refusal is
        /// recorded for the process (this radio and slot), so the next
        /// attempt — on this rig or on the one the application builds to
        /// replace it — presents no id; the stored one is kept.
        /// </summary>
        private void onDuplicateClientIdRefused(Radio radio)
        {
            if (!ReferenceEquals(radio, theRadio)) return;
            var identity = ClientIdentity;
            if (identity != null) identity.MarkRefusedAsDuplicate();
            else GuiClientIdentityRefusals.Record(radio?.Serial ?? "", GuiClientSlot.Current.Number);
            string id = identity?.PresentedId ?? identity?.EffectiveId ?? "(unknown)";
            Tracing.TraceLine("GuiClientIdentity: the radio refused our client id " + id
                + " as a DUPLICATE — another live connection holds it (an earlier session of ours the radio has not timed out, or a second copy of the program) — and is disconnecting this session."
                + " The next connect in this run to this radio presents no id so the radio mints a fresh one; the stored id is kept"
                + (identity?.FilePath != null ? " at " + identity.FilePath : "") + ".", TraceLevel.Error);
        }

        private void wireClientIdentityHandlers(Radio radio)
        {
            if (radio == null || _duplicateClientIdHandlers.ContainsKey(radio)) return;
            EventHandler handler = (s, e) => onDuplicateClientIdRefused(radio);
            _duplicateClientIdHandlers.Add(radio, handler);
            radio.DuplicateClientIdDisconnectReq += handler;
        }

        private void unwireClientIdentityHandlers(Radio radio)
        {
            if (radio == null) return;
            if (_duplicateClientIdHandlers.Remove(radio, out var handler)) radio.DuplicateClientIdDisconnectReq -= handler;
        }

        // ── the post-connect fence, for a RETURNING connect only ──────────

        private readonly object _fenceSync = new object();
        /// <summary>The attempt whose fence the radio has acknowledged and
        /// which is waiting for every reported own slice to be ready; -1 when none.</summary>
        private int _fenceAcknowledgedForAttempt = -1;
        private string _fenceWhy = "";
        private int _fenceHeldReportedCount = -1;

        /// <summary>Whether this connect presented a stored id, and so may be
        /// handed a restored station by the radio. Chooses the station
        /// phase's materialization boundary (<see cref="Radios.StationConnect.StationPolicies.ForConnect"/>).</summary>
        internal bool PresentedStoredIdThisConnect => ClientIdentity?.PresentedStoredId == true;

        private void ClearConnectFence()
        {
            lock (_fenceSync)
            {
                _fenceAcknowledgedForAttempt = -1;
                _fenceWhy = "";
                _fenceHeldReportedCount = -1;
                _arrivalNumbers.Clear();
                _arrivalsUnattributed.Clear();
                _arrivals.Clear();
                _arrivalCount = 0;
            }
        }

        // ── arrival order, for the bench's firmware question ───────────────
        //
        // The fence proves the ping's reply arrived after every status line
        // the radio had sent by then; it cannot prove the radio had nothing
        // more to send. Only the radio can answer that, and the bench reads
        // the answer off this log: on a RETURNING connect, the first line seen
        // for each slice and panadapter in the vendor's lists (its ARRIVAL,
        // ready or not) and the ping's acknowledgement are numbered in the
        // order they arrived. Every object the radio restored for us must
        // carry a lower number than the acknowledgement, and no own object
        // may arrive after the fence is recorded unless we asked for it.

        //
        // FlexLib puts an object in its list (and raises the list change)
        // BEFORE it parses the line's keys, so at the instant of arrival the
        // object's client handle is still 0. The log therefore has two kinds
        // of line: the ARRIVAL, numbered, written the moment the object is
        // first seen; and its ATTRIBUTION, written under the same number at
        // the next trigger (the next list change, the object becoming ready,
        // or the acknowledgement — whose own walk runs first, so everything
        // that arrived before the reply is attributed before the ACK line).

        private readonly Dictionary<object, int> _arrivalNumbers = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        private readonly List<object> _arrivalsUnattributed = new List<object>();
        private readonly List<string> _arrivals = new List<string>();
        private int _arrivalCount;

        /// <summary>The arrival log of the current returning connect, in the
        /// order written, exactly as the trace shows it after the
        /// "ConnectFence: ARRIVAL " prefix. Empty on a fresh connect.</summary>
        internal IReadOnlyList<string> ConnectFenceArrivals
        {
            get { lock (_fenceSync) return _arrivals.ToList(); }
        }

        private void noteArrivalLine(string what)
        {
            lock (_fenceSync) _arrivals.Add(what);
            Tracing.TraceLine("ConnectFence: ARRIVAL " + what, TraceLevel.Info);
        }

        /// <summary>A one-off event in the arrival stream (the ping's reply).</summary>
        private void noteArrival(string what)
        {
            int n;
            lock (_fenceSync) n = ++_arrivalCount;
            noteArrivalLine("#" + n + " " + what);
        }

        /// <summary>
        /// Attribute every earlier arrival whose handle is now parsed, then
        /// log every object in the vendor's slice and panadapter lists not
        /// seen before, in list order. Called on each list change, on each
        /// own object becoming ready, and before the ping's reply is logged,
        /// on a returning connect only.
        /// </summary>
        private void noteArrivals()
        {
            if (!PresentedStoredIdThisConnect) return;
            var radio = theRadio;
            if (radio == null) return;
            uint handle = clientHandle;
            var lines = new List<string>();

            lock (_fenceSync)
            {
                for (int i = _arrivalsUnattributed.Count - 1; i >= 0; i--)
                {
                    object o = _arrivalsUnattributed[i];
                    uint h = o is Slice sl ? sl.ClientHandle : o is Panadapter pa ? pa.ClientHandle : 0;
                    if (h == 0) continue;
                    bool ours = handle != noClient && h == handle;
                    string name = o is Slice s2 ? "slice " + s2.Index : "panadapter 0x" + ((Panadapter)o).StreamID.ToString("X");
                    string pan = o is Slice s3 ? ", pan 0x" + s3.PanadapterStreamID.ToString("X") : "";
                    lines.Add("#" + _arrivalNumbers[o] + " attributed: " + name + " is " + (ours ? "OURS" : "another client's") + " (handle 0x" + h.ToString("X") + pan + ")");
                    _arrivalsUnattributed.RemoveAt(i);
                }
                lines.Reverse(); // attributions in arrival order
            }

            var slices = radio.SliceList;
            if (slices != null)
            {
                lock (slices)
                {
                    foreach (var s in slices)
                    {
                        if (s == null) continue;
                        lock (_fenceSync)
                        {
                            if (_arrivalNumbers.ContainsKey(s)) continue;
                            int n = ++_arrivalCount;
                            _arrivalNumbers[s] = n;
                            _arrivalsUnattributed.Add(s);
                            lines.Add("#" + n + " slice " + s.Index + " reported (handle and pan not parsed yet at arrival; not yet ready)");
                        }
                    }
                }
            }
            var pans = radio.PanadapterList;
            if (pans != null)
            {
                lock (pans)
                {
                    foreach (var p in pans)
                    {
                        if (p == null) continue;
                        lock (_fenceSync)
                        {
                            if (_arrivalNumbers.ContainsKey(p)) continue;
                            int n = ++_arrivalCount;
                            _arrivalNumbers[p] = n;
                            _arrivalsUnattributed.Add(p);
                            lines.Add("#" + n + " panadapter 0x" + p.StreamID.ToString("X") + " reported (handle not parsed yet at arrival)");
                        }
                    }
                }
            }
            foreach (var l in lines) noteArrivalLine(l);
        }

        /// <summary>
        /// The post-connect fence (#782): one reply-bearing <c>ping</c> on the
        /// command channel, sent right after the vendor's Connect returns,
        /// ONLY when this connect presented a stored id — a fresh mint has
        /// nothing persisted for the radio to restore, which bench D measured,
        /// and keeps the own-handle boundary. The radio answers the ping after
        /// every earlier command on that channel, FlexLib's subscriptions
        /// included, so by the reply every panadapter and slice the radio
        /// restored for a returning client has been REPORTED. Reported is not
        /// READY: FlexLib raises <c>SliceAdded</c> only once
        /// <c>Slice.CheckReady</c> finds the slice's panadapter ready, and the
        /// station tracker counts ready slices. So an ACKNOWLEDGED reply is
        /// held until every own slice in the vendor's list is also in ours;
        /// each readiness or list change re-checks. If one never readies,
        /// the fence is never recorded and the station phase ends Unknown
        /// (allocates nothing). A reply with a nonzero code is not evidence.
        /// </summary>
        /// <remarks>
        /// What the fence cannot prove, and the bench must: that the radio
        /// finishes reporting a restored station BEFORE it answers a later
        /// command, rather than from a background step of its own. The
        /// <c>ConnectFence:</c> trace lines say where restored objects must
        /// appear relative to them, so the bench reads it off the trace.
        /// </remarks>
        private void sendConnectFence(string why)
        {
            var radio = theRadio;
            if (radio == null) return;
            if (!PresentedStoredIdThisConnect)
            {
                Tracing.TraceLine("ConnectFence: " + why + " — not sent: this connect presented no stored id, so the radio has nothing of ours to restore;"
                    + " the station phase uses the own-handle boundary (bench D)", TraceLevel.Info);
                return;
            }
            int gen = AttemptGen;
            var sw = Stopwatch.StartNew();
            noteArrivals(); // whatever the vendor already holds when the fence goes out
            string refusal = SendRadioCommandWithReply(radio, "ping", reply =>
            {
                noteArrivals(); // anything that arrived before the reply and was not yet logged
                if (!reply.Acknowledged)
                {
                    noteArrival("ping REJECTED with code 0x" + reply.Code.ToString("X") + " after " + sw.ElapsedMilliseconds + " ms — not fence evidence");
                    Tracing.TraceLine("ConnectFence: " + why + " — ping answered with code 0x" + reply.Code.ToString("X") + " after " + sw.ElapsedMilliseconds
                        + " ms; a rejected ping is NOT fence evidence, so the station phase will not allocate (materialization stays unknown)", TraceLevel.Error);
                    return;
                }
                lock (_fenceSync)
                {
                    _fenceAcknowledgedForAttempt = gen;
                    _fenceWhy = why;
                    _fenceHeldReportedCount = -1;
                }
                noteArrival("ping ACK after " + sw.ElapsedMilliseconds + " ms — every slice or panadapter the radio restored for this client must carry a LOWER arrival number than this one; an OURS arrival with a higher number that we did not request means the radio restores after answering, and the fence is early");
                tryRecordConnectFence("the ping reply");
            });
            if (refusal != null)
            {
                Tracing.TraceLine("ConnectFence: " + why + " — not sent: " + refusal
                    + "; the station phase will not allocate (materialization stays unknown)", TraceLevel.Error);
            }
        }

        /// <summary>
        /// Record the acknowledged fence as station evidence once every own
        /// slice the radio has REPORTED (in the vendor's list) is READY (in
        /// ours). Called from the ping reply, from each own slice readiness
        /// and removal, from the vendor's list changes, and when our handle
        /// is established. Cheap, idempotent, and silent when nothing is pending.
        /// </summary>
        private void tryRecordConnectFence(string trigger)
        {
            int gen;
            string why;
            lock (_fenceSync)
            {
                gen = _fenceAcknowledgedForAttempt;
                why = _fenceWhy;
            }
            if (gen < 0) return;
            if (gen != AttemptGen) { ClearConnectFence(); return; }

            var radio = theRadio;
            if (radio == null) return;
            uint handle = clientHandle;
            if (handle == noClient)
            {
                noteFenceHeld(-1, "our own handle is not established yet", trigger);
                return;
            }

            int reported;
            var list = radio.SliceList;
            if (list == null) reported = 0;
            else lock (list) reported = list.Count(s => s != null && s.ClientHandle == handle);
            int ready;
            lock (mySlices) ready = mySlices.Count;

            if (reported > ready)
            {
                noteFenceHeld(reported - ready, (reported - ready) + " own slice(s) the radio has reported are not yet ready (their panadapter has not reported), out of " + reported, trigger);
                return;
            }

            lock (_fenceSync)
            {
                if (_fenceAcknowledgedForAttempt != gen) return;
                _fenceAcknowledgedForAttempt = -1;
                _fenceHeldReportedCount = -1;
            }
            int pans;
            try { pans = radio.PanadapterList?.Count ?? -1; } catch { pans = -1; }
            Tracing.TraceLine("ConnectFence: " + why + " — recorded on " + trigger + ": own slices reported " + reported + ", ready " + ready
                + ", panadapters on the radio (all clients) " + pans + "; the station phase may now judge the radio's own delivery ended", TraceLevel.Info);
            ProfileEvidence.ConnectFenceObserved(gen);
        }

        private void noteFenceHeld(int pending, string reason, string trigger)
        {
            bool trace;
            lock (_fenceSync)
            {
                trace = _fenceHeldReportedCount != pending;
                _fenceHeldReportedCount = pending;
            }
            if (trace)
                Tracing.TraceLine("ConnectFence: acknowledged but HELD on " + trigger + ": " + reason
                    + "; re-checked as slices become ready; if they never do, the station phase ends unknown and allocates nothing", TraceLevel.Info);
        }
    }
}
