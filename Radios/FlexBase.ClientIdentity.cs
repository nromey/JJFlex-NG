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
            }
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
            string refusal = SendRadioCommandWithReply(radio, "ping", reply =>
            {
                if (!reply.Acknowledged)
                {
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
                Tracing.TraceLine("ConnectFence: " + why + " — ping acknowledged after " + sw.ElapsedMilliseconds
                    + " ms — every object the radio restored for this client should have been REPORTED above this line; a restored slice or panadapter traced below it means the fence is early", TraceLevel.Info);
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
