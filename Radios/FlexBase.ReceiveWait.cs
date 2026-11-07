#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// FlexBase, waiting for the radio to be really receiving before something
    /// held for a transmission is let go (#835, Codex round 1030, B5 and its
    /// sibling).
    /// </summary>
    public partial class FlexBase
    {
        /// <summary>
        /// The radio's own word, now, on whether it is transmitting: true while
        /// it reports transmit, false once it reports receive, null when there
        /// is no connected radio to ask.
        /// </summary>
        /// <remarks>
        /// The same pair <see cref="WhenRadioReallyReceives"/> waits on: our
        /// cached <see cref="Transmit"/> AND the radio's interlock
        /// (<see cref="RadioInterlockReportsTransmit"/>), because the cache
        /// falls the moment we ask for an unkey while a hardware push to talk
        /// line or VOX may still key the radio. Asked by the push to talk
        /// controller after it lets go of its key, so "Receiving" is the
        /// radio's claim and not only the app's (task 904).
        /// </remarks>
        public bool? RadioReportsTransmitNow
        {
            get
            {
                Radio? radio = theRadio;
                if (radio == null || !radio.Connected) return null;
                return _Transmit || RadioInterlockReportsTransmit;
            }
        }

        /// <summary>
        /// The radio's command connection as it stands now, for comparing by
        /// reference only: the same object while one transport attempt lasts,
        /// a new one for every attempt, and null with no radio. Connected or
        /// not, so a lost connection is still the same one; only a new attempt
        /// or another radio changes it.
        /// </summary>
        /// <remarks>
        /// The identity <see cref="WhenRadioReallyReceives"/> pins its wait to,
        /// offered to the push to talk controller so its wait for the radio's
        /// receive after a release is pinned the same way (task 904, repair 7o).
        /// <see cref="CurrentConnectionBinding"/> is asked beside it: the
        /// binding follows the transport's own report, this reads the
        /// transport.
        /// </remarks>
        public object? CommandConnectionNow
        {
            get
            {
                try { return theRadio?.CurrentCommandConnection; }
                catch { return null; }
            }
        }

        /// <summary>
        /// <see cref="WhenRadioReallyReceives"/> for the push-to-talk controller:
        /// the falling tri-tone of a carrier this computer did not key for
        /// itself plays on the radio's real receive, not on our cache's
        /// (#863, repair round 3, Codex's second re-check B1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Positive, since repair round 4</b> (ruled by Noel 2026-11-04
        /// 22:24: every falling transmit cue waits for the radio's own receive
        /// report). "Really receiving" here is our cached Transmit false AND a
        /// recognised receive report on the connection the wait began on,
        /// still connected (<see cref="RadioInterlockConfirmsReceive"/>), not
        /// merely "the interlock does not say transmit": a tone means really
        /// off the air, so a state we do not recognise is not receive.
        /// </para>
        /// <para>
        /// <b>Woken by the reports, not polled</b>, so a tone follows the
        /// radio's word with no delay of our own: FlexLib's interlock and Mox
        /// notifications and our own record of the report each wake it. A
        /// slow fallback check catches a connection that ends quietly.
        /// </para>
        /// </remarks>
        public bool WhenReallyReceiving(Action<bool> then) => WhenRadioReportsReceive((same, _) => then(same), null);

        /// <summary>
        /// The same wait, given up quietly once <paramref name="stillWanted"/>
        /// says no (a newer debt replaced the one it was for): then
        /// <paramref name="then"/> never runs, and nothing is left waiting
        /// behind a carrier that may hold for a long time.
        /// </summary>
        public bool WhenReallyReceiving(Action<bool> then, Func<bool> stillWanted) =>
            WhenRadioReportsReceive((same, _) => then(same), stillWanted);

        /// <summary>
        /// The same wait, handing <paramref name="then"/> a way to act only if
        /// the radio is STILL receiving, for this transmission, on this
        /// connection (repair rounds 5 and 6, Codex's #863 N2 and S1). The
        /// function runs its action, and returns true, only when that holds,
        /// with the check and the action as one step against every new key
        /// this process asks for and every report or connection change the
        /// transport publishes (<see cref="FallingCueGate"/>).
        /// </summary>
        public bool WhenReallyReceiving(Action<bool, Func<Action, bool>> then, Func<bool> stillWanted) =>
            WhenRadioReportsReceive(then, stillWanted);

        /// <summary>
        /// The same wait, for a debt that belongs to a transmission begun when
        /// <see cref="TransmissionsAsked"/> stood at <paramref name="origin"/>
        /// (repair round 7, Codex's #863 S2): any transmission asked for since
        /// voids it at once, however late the debt is made.
        /// </summary>
        public bool WhenReallyReceiving(Action<bool, Func<Action, bool>> then, Func<bool> stillWanted, long origin) =>
            WhenRadioReportsReceive(then, stillWanted, origin);

        /// <summary>
        /// The same wait, for a falling cue owed to a transmission WE keyed
        /// (repair round 9, the coordinator's anchor): it is paid only on a
        /// receive report numbered after our own OFF for that key was really
        /// written, and only when our ON for it was really written before that.
        /// If the ON never went out, nothing was transmitted: the wait ends at
        /// once and <paramref name="then"/> runs, and the caller reads
        /// <see cref="OwnKey.NeverWent"/> to know there is no cue owed.
        /// </summary>
        public bool WhenReallyReceiving(Action<bool, Func<Action, bool>> then, Func<bool> stillWanted, OwnKey key) =>
            WhenRadioReportsReceive(then, stillWanted, key.Origin, key);

        /// <summary>
        /// One key this rig asked for itself (repair round 9, Codex's #863 S4
        /// and S5): its count of transmissions asked, and where it stands on the
        /// wire. ASKED (queued) becomes ON WRITTEN when its xmit 1 was really
        /// written, or NEVER WENT when the queue refused, revoked or skipped it;
        /// ON WRITTEN becomes OFF WRITTEN when our xmit 0 was really written,
        /// marked with the interlock report sequence at that moment. A carrier
        /// we keyed keeps this identity, whatever report about it comes later.
        /// </summary>
        public sealed class OwnKey
        {
            internal const int Asked = 0, OnWritten = 1, NeverWentState = 2, OffWritten = 3;
            private int _state;
            private long _offMark;
            private CommandConnection? _connection;

            internal OwnKey(long origin, OwnKey? previous = null, CommandConnection? askedOn = null)
            {
                Origin = origin;
                _previous = previous;
                AskedOn = askedOn;
            }

            /// <summary>
            /// The command connection the key was asked for (repair round 11,
            /// Codex's #596 B17): from the moment it is asked until it is known
            /// never to have gone out, it may be going out there.
            /// </summary>
            internal CommandConnection? AskedOn { get; }
            private OwnKey? _previous;

            /// <summary>
            /// The latest key of ours when this one was asked (repair round 10,
            /// Codex's #863 S6): if this key's own ON writes nothing while that
            /// one's ON is still outstanding on the same connection, this key
            /// CONTINUES that carrier (a push-to-talk takeover of a send, held
            /// push to talk becoming the lock) rather than never going out.
            /// </summary>
            /// <remarks>Let go once this key's own ON is written, so the keys of a
            /// session never form an ever-growing chain. A key that never went out
            /// keeps it (repair round 11, Codex's #863 F1): the predecessor's
            /// carrier, or its unfinished receive, is still the question for the
            /// restores and the next key.</remarks>
            internal OwnKey? Previous => Volatile.Read(ref _previous);

            /// <summary>The count of transmissions asked when this key was asked.</summary>
            public long Origin { get; }

            internal int State => Volatile.Read(ref _state);

            /// <summary>The key never went out: nothing was transmitted, so no falling cue is owed for it.</summary>
            public bool NeverWent => State == NeverWentState;

            /// <summary>The command connection its ON was written on; null until then.</summary>
            internal CommandConnection? Connection => Volatile.Read(ref _connection);

            internal bool NoteOnWritten(CommandConnection? connection)
            {
                Volatile.Write(ref _connection, connection);
                bool moved = Interlocked.CompareExchange(ref _state, OnWritten, Asked) == Asked;
                Volatile.Write(ref _previous, null);
                return moved;
            }

            internal bool NoteNeverWent()
            {
                // Keep the predecessor (round 11, F1), collapsed to the key that
                // holds evidence, so a run of keys that never went out is one hop.
                var previous = Previous;
                if (previous != null && previous.State == NeverWentState) Volatile.Write(ref _previous, previous.Previous);
                return Interlocked.CompareExchange(ref _state, NeverWentState, Asked) == Asked;
            }

            /// <summary>
            /// Our OFF was written, the reports standing at <paramref name="mark"/>:
            /// it ends this key if its ON went out, or, when this key never went
            /// out, the predecessor whose carrier it stood for (round 12, F5).
            /// </summary>
            internal bool NoteOffWritten(long mark)
            {
                var target = State == NeverWentState ? Previous : this;
                if (target == null || target.State != OnWritten) return false;
                Interlocked.Exchange(ref target._offMark, mark);
                return Interlocked.CompareExchange(ref target._state, OffWritten, OnWritten) == OnWritten;
            }

            /// <summary>
            /// Our OFF for this key was written, and since then the radio has
            /// reported a recognised receive state on the connection its ON went
            /// out on, which is still up. Reads only.
            /// </summary>
            internal bool ReceiveReportedAfterOff()
            {
                if (State != OffWritten) return false;
                var c = Connection;
                if (c == null || c.State != CommandConnectionState.Connected) return false;
                var last = InterlockReports.LastOn(c);
                return last != null && last.IsReceive && last.Sequence > Interlocked.Read(ref _offMark);
            }

            /// <summary>
            /// THE one question about whether this key of ours has really ended
            /// on <paramref name="target"/> (repair round 10, Codex's #596 B16):
            /// asked by its falling cue (<see cref="EndedOn"/>) and by every
            /// restoration of the transmit chain, so the two cannot disagree.
            /// Null when the key is not on that connection, never went out, or
            /// ended (our xmit 0 written and a receive reported after it); else
            /// why it has not.
            /// </summary>
            internal string? WhyNotEndedOn(CommandConnection? target)
            {
                int state = State;
                // Asked and not yet settled (round 11, Codex's #596 B17): its ON
                // may be going out right now on that connection, before any
                // bookkeeping says so. Possibly keyed, for every restore, until it
                // is known never to have gone out or has ended. On another
                // connection, an earlier key of ours it would continue answers.
                if (state == Asked)
                    return target != null && ReferenceEquals(AskedOn, target)
                        ? "our own " + What + " has been asked for and may be going out"
                        : Previous?.WhyNotEndedOn(target);
                // Never went out: nothing of its own to end, but its predecessor's
                // carrier or unfinished receive still answers (round 11, F1).
                if (state == NeverWentState) return Previous?.WhyNotEndedOn(target);
                if (target == null || !ReferenceEquals(Connection, target)) return null;
                if (state == OnWritten) return "our own " + What + " went out and its off has not been written";
                return ReceiveReportedAfterOff() ? null
                    : "the radio has not reported receive since our own " + What + "'s off went out";
            }

            /// <summary>This key's carrier has ended on <paramref name="target"/>: OFF written and receive reported after it.</summary>
            internal bool EndedOn(CommandConnection? target) =>
                State == OffWritten && target != null && ReferenceEquals(Connection, target) && WhyNotEndedOn(target) == null;

            /// <summary>Its xmit 1 (or tune ON) went out and no off has been written.</summary>
            internal bool OffUnwrittenOn(CommandConnection? target) =>
                State == OnWritten ? target != null && ReferenceEquals(Connection, target)
                : State is Asked or NeverWentState && Previous?.OffUnwrittenOn(target) == true;

            /// <summary>"key" or "tune", for the trace.</summary>
            internal string What { get; init; } = "key";

            /// <summary>Its carrier may still be up or still be reported: asked, on, or off with no receive reported since.</summary>
            internal bool Live => State switch
            {
                Asked or OnWritten => true,
                OffWritten => !ReceiveReportedAfterOff(),
                // A key that never went out labels no carrier (unchanged in
                // round 11); its predecessor's evidence matters to restores only.
                _ => false,
            };
        }

        /// <summary>The latest key this rig asked for itself; null before any.</summary>
        public OwnKey? LatestOwnKey => Volatile.Read(ref _latestOwnKey);
        private OwnKey? _latestOwnKey;

        /// <summary>The key the calling thread's last <see cref="Transmit"/> or send key asked for; null when that ask was refused.</summary>
        public OwnKey? KeyAskedOnThisThread => t_keyAskedHere;
        [ThreadStatic] private static OwnKey? t_keyAskedHere;

        /// <summary>One of our keys changed state: a wait for its end looks again.</summary>
        private event Action? OwnKeyChanged;

        private void RaiseOwnKeyChanged()
        {
            try { OwnKeyChanged?.Invoke(); } catch { }
        }

        /// <summary>
        /// Our xmit 0 was really written, the reports standing at
        /// <paramref name="mark"/> when its writer succeeded: the latest own key,
        /// if its ON went out (or it continues one that did), is now off.
        /// </summary>
        private void NoteOwnOffWritten(OwnKey? offFor, long mark)
        {
            // The key the OFF was asked for (round 12, Codex's #596 F5), not
            // whichever is latest now: a newer ask published after this OFF was
            // queued must not keep the earlier carrier's end from being recorded.
            var key = offFor ?? LatestOwnKey;
            if (key != null && key.NoteOffWritten(mark)) RaiseOwnKeyChanged();
        }

        /// <summary>
        /// One of our own forced offs (xmit 0 or transmit tune 0), dated at the
        /// moment its writer succeeded (repair round 10, Codex's #863 F2): the
        /// interlock report sequence then, read through the write pin's
        /// OnWritten (MIGRATION.md item 22). A report the radio makes after the
        /// bytes left is numbered after the mark, however long the caller takes
        /// to resume. Inside a pinned step its pin is used; otherwise a pin of
        /// its own on the radio's current connection, as SendCommandForced
        /// would make. True when the off was written.
        /// </summary>
        private static bool WriteOwnOff(Radio radio, Func<Radio, bool> send, out long mark)
        {
            var outer = t_stepPin;
            CommandWritePin? own = null;
            if (outer == null && radio.CurrentCommandConnection is CommandConnection current)
                own = CommandWritePin.Pin(current);
            var pin = outer ?? own;
            long at = 0;
            bool seen = false;
            var before = pin?.OnWritten;
            if (pin != null)
                pin.OnWritten = () =>
                {
                    if (!seen) { at = InterlockReports.Sequence; seen = true; }
                    before?.Invoke();
                };
            try
            {
                bool written = send(radio);
                mark = seen ? at : InterlockReports.Sequence;
                return written;
            }
            finally
            {
                if (pin != null) pin.OnWritten = before;
                own?.Dispose();
            }
        }

        /// <summary>
        /// Our latest tune carrier, tracked as a key (repair round 10, Codex's
        /// #596 B16, the sibling family): ON WRITTEN when its transmit tune 1
        /// really went out, OFF WRITTEN when its transmit tune 0 did.
        /// </summary>
        private OwnKey? _latestOwnTune;

        /// <summary>Our transmit tune 0 was really written, dated at its writer's success (round 10, F2).</summary>
        private void NoteOwnTuneOffWritten(OwnKey? offFor, long mark) =>
            (offFor ?? Volatile.Read(ref _latestOwnTune))?.NoteOffWritten(mark);

        /// <summary>
        /// Why a carrier of OUR OWN keeps the transmit chain from being restored
        /// on <paramref name="connection"/>, or null (repair round 10, Codex's
        /// #596 B16): our latest key or tune went out and has not ended by
        /// <see cref="OwnKey.WhyNotEndedOn"/>, the same question its falling cue
        /// asks. A receive report from before our xmit 0 was written is not the
        /// end of our key, so a failed unkey can no longer be read as receive.
        /// Reads only.
        /// </summary>
        internal string? OwnCarrierNotEnded(object? connection)
        {
            var c = connection as CommandConnection;
            return LatestOwnKey?.WhyNotEndedOn(c) ?? Volatile.Read(ref _latestOwnTune)?.WhyNotEndedOn(c);
        }

        /// <summary>Our latest key or tune went out on <paramref name="connection"/> and its off has not been written.</summary>
        private bool OwnOffUnwritten(object? connection)
        {
            var c = connection as CommandConnection;
            return LatestOwnKey?.OffUnwrittenOn(c) == true || Volatile.Read(ref _latestOwnTune)?.OffUnwrittenOn(c) == true;
        }

        /// <summary>Transmissions this rig has asked for: every key, send, check and tune (repair rounds 6 and 7).</summary>
        private long _transmissionsAsked;

        /// <summary>How many transmissions this rig has asked for so far. A falling-tone debt carries it from its origin.</summary>
        public long TransmissionsAsked => Interlocked.Read(ref _transmissionsAsked);

        /// <summary>
        /// <see cref="TransmissionsAsked"/> as it stood at the latest carrier
        /// rise this rig saw (our own key asked, or a Mox the radio reported):
        /// the origin of a debt owed to a carrier the radio keyed (repair round
        /// 7, Codex's #863 S2). Recorded where the rise happens, on the reader,
        /// so a deferred notification of it carries the right origin.
        /// </summary>
        public long LastCarrierOrigin => Interlocked.Read(ref _lastCarrierOrigin);
        private long _lastCarrierOrigin;

        /// <summary>
        /// A new transmission is asked for, counted under the falling cue's gate.
        /// Returns its count, which travels with the request (repair round 8).
        /// </summary>
        private long NoteTransmissionAsked(bool carrier)
        {
            long origin = FallingCueGate.NewTransmissionAsked(ref _transmissionsAsked);
            if (carrier) Interlocked.Exchange(ref _lastCarrierOrigin, origin);
            return origin;
        }

        /// <summary>
        /// A carrier rose. While a key of ours is live (asked, on, or off with
        /// no receive reported since), the rise is that key's: its echo, or a
        /// delayed report of it such as an UNKEY_REQUESTED after our OFF, and it
        /// keeps our key's origin (repair round 9, Codex's #863 S4; this
        /// replaces round 8's thread-local echo token). A carrier we did not ask
        /// for (a hand mic, a foot switch, VOX, another client) takes the count
        /// when the radio reports it.
        /// </summary>
        private void NoteCarrierRose()
        {
            var key = LatestOwnKey;
            Interlocked.Exchange(ref _lastCarrierOrigin, key != null && key.Live ? key.Origin : TransmissionsAsked);
        }

        /// <summary>For tests: runs on the payment's thread just before its final check takes the transport's dispatch gate.</summary>
        internal Action? BeforeFallingCueFinalCheckForTests;

        /// <summary>
        /// The interlock reports as they stood when this rig's latest
        /// transmission was asked for, or its cached Transmit last rose
        /// (repair round 5, #863 N1). Zero before any.
        /// </summary>
        private long _txEpisodeMark;

        /// <summary>A transmission begins: a report from before now says nothing about its end.</summary>
        private void NoteTransmitEpisodeBegins() => Volatile.Write(ref _txEpisodeMark, InterlockReports.Sequence);

        /// <summary>For tests: the fallback interval, in place of <see cref="ReceiveReportFallbackMs"/>; zero uses it.</summary>
        internal int ReceiveReportFallbackMsOverride;

        private bool WhenRadioReportsReceive(Action<bool, Func<Action, bool>> then, Func<bool>? stillWanted, long? origin = null,
            OwnKey? key = null)
        {
            Radio? radio = theRadio;
            CommandConnection? connection = radio?.Connected == true ? radio.CurrentCommandConnection : null;
            // The episode this wait is about (repair round 5, Codex's #863 N1):
            // the reports read before this release, and before this
            // transmission began, cannot confirm its end. A READY delivered
            // before the key was pressed is not the radio saying this key's
            // carrier is down.
            long releaseMark = InterlockReports.Sequence;
            long episodeMark = Volatile.Read(ref _txEpisodeMark);
            bool SameConnection() => radio != null && connection != null && ReferenceEquals(theRadio, radio)
                && radio.Connected && ReferenceEquals(connection, radio.CurrentCommandConnection)
                && connection.State == CommandConnectionState.Connected;
            // A key of ours (round 9): anchored to its written OFF, on the
            // connection its ON went out on. A carrier we did not key keeps the
            // report-anchored rule.
            bool ReportsReceive() => !_Transmit && RadioInterlockConfirmsReceive
                && (key != null
                    ? key.EndedOn(connection)
                    : InterlockReports.ConfirmsReceiveFor(connection, releaseMark, episodeMark));
            // Our ON never went out: nothing to wait for.
            bool KeyNeverWent() => key != null && key.NeverWent;
            // Repair round 6 (Codex's #863 S1 variant): a newer transmission this
            // process asked for since the debt was made (another send, a check,
            // a tune) voids it. Its own end owes its own cue; the first
            // carrier's receive report says nothing about the newer one.
            // The debt's own origin when it carries one (round 7, S2); otherwise now.
            long transmissions = origin ?? TransmissionsAsked;
            bool Superseded() => TransmissionsAsked != transmissions;
            bool StillReceiving() => SameConnection() && !Superseded() && ReportsReceive();
            // The final check and the caller's action as ONE step (repair round
            // 6, S1): under the transport's dispatch gate, which the vendor's
            // parser, our interlock record and the connection's own changes
            // are written under, and under the gate every new key is counted
            // under. Lock order: dispatch gate, cue gate, then the caller's.
            bool SubmitIfStillReceiving(Action act)
            {
                BeforeFallingCueFinalCheckForTests?.Invoke();
                object? dispatch = InterlockReports.ConnectionSyncOf(radio);
                if (dispatch == null) return false;
                // Bounded, as every other app-side take of this gate is
                // (integration fix): this can run on the UI thread. Not had in
                // time, the cue is not played, silence plus a trace, the ruled
                // no-report behaviour.
                if (!System.Threading.Monitor.TryEnter(dispatch, CommandWritePin.SerializeBound))
                {
                    JJTrace.Tracing.TraceLine("Falling cue: the radio's report gate was not had in "
                        + (int)CommandWritePin.SerializeBound.TotalMilliseconds + " ms, so the cue is not played",
                        System.Diagnostics.TraceLevel.Info);
                    return false;
                }
                try { return FallingCueGate.RunIf(StillReceiving, act); }
                finally { System.Threading.Monitor.Exit(dispatch); }
            }
            int fallback = ReceiveReportFallbackMsOverride > 0 ? ReceiveReportFallbackMsOverride : ReceiveReportFallbackMs;

            if (!SameConnection()) { then(false, SubmitIfStillReceiving); return false; }
            if (KeyNeverWent() || ReportsReceive()) { then(true, SubmitIfStillReceiving); return false; }

            var wake = new System.Threading.SemaphoreSlim(0);
            System.ComponentModel.PropertyChangedEventHandler onRadio = (_, e) =>
            {
                if (e.PropertyName is "InterlockState" or "Mox") { try { wake.Release(); } catch { } }
            };
            Action<CommandConnection> onReport = c =>
            {
                if (ReferenceEquals(c, connection)) { try { wake.Release(); } catch { } }
            };
            Action onKey = () => { try { wake.Release(); } catch { } };
            radio!.PropertyChanged += onRadio;
            InterlockReports.Reported += onReport;
            if (key != null) OwnKeyChanged += onKey;
            _ = Task.Run(async () =>
            {
                bool same;
                try
                {
                    while ((same = SameConnection()) && !KeyNeverWent() && !ReportsReceive() && !Superseded())
                    {
                        if (stillWanted != null && !stillWanted()) return;
                        await wake.WaitAsync(fallback).ConfigureAwait(false);
                    }
                }
                finally
                {
                    radio.PropertyChanged -= onRadio;
                    InterlockReports.Reported -= onReport;
                    if (key != null) OwnKeyChanged -= onKey;
                }
                then(same, SubmitIfStillReceiving);
            });
            return true;
        }

        /// <summary>
        /// How often a wait for the receive report looks again with nothing to
        /// wake it: only for a connection that ends without a word. Every
        /// report wakes it at once, so this never delays a tone that has its
        /// report.
        /// </summary>
        internal const int ReceiveReportFallbackMs = 250;

        /// <summary>
        /// Run <paramref name="then"/> once the radio is really receiving, or
        /// once the connection this call began on is gone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>"Receive" is the radio's word, not only our cache.</b> Our cached
        /// <see cref="Transmit"/> falls the moment we drop our own key, which a
        /// hardware takeover does while the hand mic still keys the radio, and
        /// FlexLib's Mox cache flips the moment we ask for an unkey. So both
        /// must say receive: cached Transmit false AND the radio's interlock
        /// out of transmit (<see cref="RadioInterlockReportsTransmit"/>), the
        /// same pair the check's restores wait for.
        /// </para>
        /// <para>
        /// <b>Pinned to the radio and command connection it began on</b>
        /// (<c>feedback_deferred_state_carries_its_session</c>). Another radio,
        /// a new connection or a lost one ends the wait the way a disconnect
        /// always has: <paramref name="then"/> runs with
        /// <c>connectionHeld</c> false, and the caller's own lost-connection
        /// rules apply.
        /// </para>
        /// <para>
        /// <b>No time limit,</b> deliberately: the ruling is "only when the radio
        /// is really receiving". An interlock that never reports READY on a
        /// live connection keeps the wait going; whether that wants a limit is
        /// Noel's question, not this method's.
        /// </para>
        /// </remarks>
        /// <param name="then">
        /// Runs exactly once. Its argument is true when the radio reported
        /// receive on the same connection, false when the connection went.
        /// </param>
        /// <param name="beforeWaiting">
        /// Runs before the wait starts, only if there is one to wait for, so a
        /// trace line saying "waiting" always comes before the one saying it
        /// ended.
        /// </param>
        /// <returns>
        /// False when nothing had to be waited for: <paramref name="then"/> has
        /// already run, on this thread. True when it runs later, on the pool.
        /// </returns>
        private bool WhenRadioReallyReceives(Action<bool> then, Action? beforeWaiting = null)
        {
            Radio? radio = theRadio;
            CommandConnection? connection = radio?.Connected == true ? radio.CurrentCommandConnection : null;
            bool SameConnection() => radio != null && ReferenceEquals(theRadio, radio) && radio.Connected
                && ReferenceEquals(connection, radio.CurrentCommandConnection);
            // Read only after SameConnection(), so theRadio is the pinned radio.
            bool StillTransmitting() => _Transmit || RadioInterlockReportsTransmit;

            bool held = SameConnection();
            if (!held || !StillTransmitting())
            {
                then(held);
                return false;
            }
            beforeWaiting?.Invoke();
            _ = Task.Run(async () =>
            {
                bool same;
                while ((same = SameConnection()) && StillTransmitting())
                    await Task.Delay(20).ConfigureAwait(false);
                then(same);
            });
            return true;
        }
    }
}
