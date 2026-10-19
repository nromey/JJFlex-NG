using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The GUI client identity JJ Flexible presents to the radio (#782): what
    /// is handed to <c>Radio.Connect</c> on both connect paths, what the radio
    /// reports back, and the one refusal the radio can make.
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
    /// The radio's report of our own id arrives three ways (its connected
    /// status through the producer seam, and the vendor list's add and update
    /// raises); all three feed <see cref="noteOwnClientIdReported"/>, which is
    /// idempotent, so whichever arrives first records it and the rest are
    /// silent.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>Which running copy of the program this is (1 for the first).
        /// From <see cref="OpenParms.ProgramInstance"/>; the trace file name is
        /// numbered from the same count.</summary>
        private int _programInstance = 1;

        /// <summary>Process-wide: the stored id was refused as a duplicate on
        /// an earlier connection this run, so later attempts present none.</summary>
        private bool _clientIdRefusedThisRun;

        private readonly Dictionary<Radio, EventHandler> _duplicateClientIdHandlers = new();

        /// <summary>
        /// The identity of the current (or most recent) connection attempt:
        /// what was presented, what the radio reported, whether it was
        /// refused, and <see cref="GuiClientIdentity.IsOurs"/> for a feature
        /// asking "did I create this in an earlier session?". Null before the
        /// first connect attempt of this rig.
        /// </summary>
        public GuiClientIdentity ClientIdentity { get; private set; }

        /// <summary>
        /// Whether this rig presents the stored GUI client id and stores the
        /// radio's mint. True for the operator's rig. False makes every
        /// connect a session-only identity: the Connection Tester builds
        /// throwaway rigs in the same process as the live connection, and a
        /// second rig presenting the live one's id would make the radio
        /// refuse one of the two as a duplicate — and nothing says which.
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
            var identity = GuiClientIdentity.ForConnect(serial, _programInstance, _clientIdRefusedThisRun, UseStoredGuiClientId);
            ClientIdentity = identity;
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
        /// already holds it, and it is closing this one. The rest of this run
        /// presents no id; the stored one is kept.
        /// </summary>
        private void onDuplicateClientIdRefused(Radio radio)
        {
            if (!ReferenceEquals(radio, theRadio)) return;
            _clientIdRefusedThisRun = true;
            var identity = ClientIdentity;
            identity?.MarkRefusedAsDuplicate();
            string id = identity?.PresentedId ?? identity?.EffectiveId ?? "(unknown)";
            Tracing.TraceLine("GuiClientIdentity: the radio refused our client id " + id
                + " as a DUPLICATE — another live connection holds it (an earlier session of ours the radio has not timed out, or a second copy of the program) — and is disconnecting this session."
                + " The next connect in this run presents no id so the radio mints a fresh one; the stored id is kept"
                + (identity?.FilePath != null ? " at " + identity.FilePath : "") + ".", TraceLevel.Error);
        }

        /// <summary>
        /// The post-connect fence (#782): one reply-bearing <c>ping</c> on the
        /// command channel, sent right after the vendor's Connect returns.
        /// The radio answers it after every earlier command — FlexLib's
        /// subscriptions included — so by the reply every panadapter and
        /// slice the radio restored for a returning client has been reported.
        /// The reply feeds the station phase's materialization boundary
        /// (<see cref="Radios.StationConnect.MaterializationEndsAtConnectFencePolicy"/>);
        /// the trace line beside it is what the bench reads to check the
        /// order the policy assumes.
        /// </summary>
        private void sendConnectFence(string why)
        {
            var radio = theRadio;
            if (radio == null) return;
            int gen = AttemptGen;
            var sw = Stopwatch.StartNew();
            string refusal = SendRadioCommandWithReply(radio, "ping", reply =>
            {
                int ownSlices = StationTracker.Snapshot().OwnSliceCount;
                int pans;
                try { pans = radio.PanadapterList?.Count ?? -1; } catch { pans = -1; }
                Tracing.TraceLine("ConnectFence: " + why + " — ping " + (reply.Acknowledged ? "acknowledged" : "answered 0x" + reply.Code.ToString("X"))
                    + " after " + sw.ElapsedMilliseconds + " ms; own slices reported so far: " + ownSlices
                    + ", panadapters on the radio (all clients): " + pans
                    + " — every object the radio restored for this client should have been reported ABOVE this line; a restored slice traced below it means the fence is early", TraceLevel.Info);
                ProfileEvidence.ConnectFenceObserved(gen);
            });
            if (refusal != null)
            {
                Tracing.TraceLine("ConnectFence: " + why + " — not sent: " + refusal
                    + "; the station phase will not allocate (materialization stays unknown)", TraceLevel.Error);
            }
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
    }
}
