using System;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>FlexBase, what the radio says about transmitting (#447).</summary>
    public partial class FlexBase
    {
        // FlexLib has no "reported" flag for this. It holds false from
        // construction, so the notification is our only proof the value is the
        // radio's: the same reasoning as _radioReportedAutosave (#499), and like
        // that one it belongs to a connection. Cleared at Connect, at
        // RetryConnect and when the connection falls.
        private volatile bool _txAllowedReported;

        // What this client last asked of the tune carrier, for TuneFact. Noted
        // beside the request, which goes on exactly as before: nothing here
        // gates a carrier or changes when one is written.
        private readonly object _tuneFactGate = new();
        private bool _tuneRequested;
        private long _tuneRequestedAtMs;

        /// <summary>The clock a request is timed by. For tests.</summary>
        internal Func<long> TuneFactClock { get; set; } = () => Environment.TickCount64;

        /// <summary>A tune was asked for, or asked off. On the caller's thread, so a door on that thread sees it at once, ahead of the command queue.</summary>
        private void NoteTuneRequest(bool on)
        {
            lock (_tuneFactGate)
            {
                _tuneRequested = on;
                if (on) _tuneRequestedAtMs = TuneFactClock();
            }
        }

        // ── The tune write's own life in the command queue (2026-11-02) ──────
        //
        // A tune-ON sits in the command queue until the worker runs it, and until
        // then it can still key the radio. Codex showed two ways that went wrong
        // in Check your audio: the request's answer window expired at 1.5 s while
        // the ON was still queued, so Escape closed the window ahead of a carrier;
        // and an OFF cleared the request at once while the ON ahead of it still
        // ran, so a second Escape closed before it keyed. A timeout is not a
        // command's lifetime. So every tune write takes a generation, a later
        // write REVOKES an earlier ON that has not run, and a queued ON is
        // counted by TuneAskedOrUp until it has run or been revoked.

        private readonly object _tuneWriteGate = new();
        private long _tuneWriteGen;
        private bool _tuneOnQueued;

        /// <summary>
        /// Queue one write of FlexLib's tune flag. A later write revokes an ON
        /// that has not yet run: it is dropped, never sent after a stop. An OFF
        /// always runs. The check and the write share a lock with the revoking
        /// setter, so an ON is either sent before the later write's generation
        /// is taken, and its flag is then up for every door to see, or never.
        /// </summary>
        /// <param name="held">Run instead of the write when an Audio Check teardown holds an ON.</param>
        private void EnqueueTuneWrite(bool value, string what, Action held) =>
            EnqueueTuneWrite(value, what, held, ownCarrierOnly: false);

        /// <param name="ownCarrierOnly">An OFF that must not touch another
        /// client's carrier (a teardown's, #596 repair round 2): at the write it
        /// re-reads the radio's transmitting client and its connection, and
        /// sends nothing when either has changed to someone else's.</param>
        private void EnqueueTuneWrite(bool value, string what, Action held, bool ownCarrierOnly)
        {
            Radio owner = theRadio;
            object connection = owner?.CurrentCommandConnection;
            long gen;
            lock (_tuneWriteGate)
            {
                gen = ++_tuneWriteGen;
                _tuneOnQueued = value;
            }
            NoteTuneRequest(value);
            // A tune carrier is a transmission too (#863 S1, repair round 6).
            OwnKey tuneKey = null;
            if (value)
            {
                long asked = NoteTransmissionAsked(carrier: false);
                // Our tune, recorded as asked BEFORE anything is written (round 11,
                // Codex's #596 B17): possibly keyed for every restore until it is
                // known never to have gone out, or has ended.
                tuneKey = new OwnKey(asked, System.Threading.Volatile.Read(ref _latestOwnTune),
                    connection as Flex.Smoothlake.FlexLib.CommandConnection) { What = "tune" };
                System.Threading.Volatile.Write(ref _latestOwnTune, tuneKey);
                if (q == null || !q.MainLoop) tuneKey.NoteNeverWent();
            }
            q.Enqueue((FunctionDel)(() =>
            {
                bool tuneWritten = false;
                try
                {
                lock (_tuneWriteGate)
                {
                    bool latest = gen == _tuneWriteGen;
                    if (value && !latest)
                    {
                        JJTrace.Tracing.TraceLine("TuneWrite: an ON for the " + what + " was revoked by a later write before it ran", System.Diagnostics.TraceLevel.Info);
                        return;
                    }
                    if (value && TeardownHeldSilently(what))
                    {
                        if (latest) _tuneOnQueued = false;
                        held?.Invoke();
                        return;
                    }
                    if (!value && ownCarrierOnly)
                    {
                        if (!OurCarrierToUnkey(owner, connection, "the " + what + " off")) return;
                        // The teardown's own tune release, pinned at the transport's
                        // write with its ownership asked there too (repair round 7,
                        // Codex's #596 B12). An unkey of ours: contention never
                        // refuses it, only a positive "not ours".
                        // Forced (round 8, MIGRATION.md item 22, Codex's #596 B14): a
                        // tune=0 report that landed while our tune ON was written
                        // cannot turn this OFF into a no-op.
                        bool off = false;
                        long offMark = 0;
                        string refusedAtWrite = WriteOnPinnedTarget(owner, connection, null,
                            () => off = WriteOwnOff(owner, r => r.SendTuneOff(), out offMark),
                            () => ForeignCarrier(owner), unkeyOfOurs: true);
                        if (refusedAtWrite == null && off) { _ownTuneRequested = false; NoteOwnTuneOffWritten(offMark); }
                        else
                            JJTrace.Tracing.TraceLine("TuneWrite: the " + what + " off NOT sent at its write - "
                                + (refusedAtWrite ?? "the transport did not take it"), System.Diagnostics.TraceLevel.Info);
                        if (latest) _tuneOnQueued = false;
                        return;
                    }
                    var radio = theRadio;
                    if (value)
                    {
                        // Our tune ON goes out when the cache read off; a report may
                        // clear the cache again at once, so the request is what counts.
                        bool wasOn = radio.TXTune;
                        // Whether it really went out (repair round 10, B16): a pin
                        // with no question of its own, only to read the outcome.
                        var tuneConnection = radio.CurrentCommandConnection;
                        using (var pin = tuneConnection != null ? Flex.Smoothlake.FlexLib.CommandWritePin.Pin(tuneConnection) : null)
                        {
                            radio.TXTune = true;
                            tuneWritten = pin != null && pin.Written > 0;
                        }
                        if (!wasOn) _ownTuneRequested = true;
                    }
                    // An ordinary OFF, the emergency kill's included
                    // (TransmitKillSwitch.DropCarrier), is forced whenever a tune
                    // may be up, whatever FlexLib's cache reads (round 8, B14).
                    else if (radio.TXTune || _ownTuneRequested)
                    {
                        if (WriteOwnOff(radio, r => r.SendTuneOff(), out long offMark)) { _ownTuneRequested = false; NoteOwnTuneOffWritten(offMark); }
                        else radio.TXTune = false;
                    }
                    if (latest) _tuneOnQueued = false;
                }
                }
                finally
                {
                    // Settle the asked tune (round 11): written, or continuing a
                    // tune of ours still on (as a key continues one, round 10's
                    // S6), or never gone out.
                    if (tuneKey != null)
                    {
                        var tc = connection as Flex.Smoothlake.FlexLib.CommandConnection;
                        if (tuneWritten || tuneKey.Previous?.OffUnwrittenOn(tc) == true) tuneKey.NoteOnWritten(tc);
                        else tuneKey.NoteNeverWent();
                    }
                }
            }), "TXTune");
        }

        /// <summary>The connection is gone: nothing queued for it is ours to count.</summary>
        private void ForgetTuneWrites()
        {
            lock (_tuneWriteGate)
            {
                _tuneWriteGen++;
                _tuneOnQueued = false;
            }
        }

        /// <summary>
        /// The radio or FlexLib has dropped the tune flag, however that came
        /// about (#860). For whoever has something to retire the moment tune
        /// ends. Raised on FlexLib's read loop, so a subscriber must not wait.
        /// </summary>
        public event Action TuneCarrierFell;

        private void OnTuneFlagFell()
        {
            NoteTuneRequest(false);
            TuneCarrierFell?.Invoke();
        }

        /// <summary>
        /// This client's tune carrier is on the air, by the radio's own report
        /// (#860): <see cref="TuneFact.CarrierUp"/>. Read-only, and safe with no
        /// radio.
        /// </summary>
        public bool TuneCarrierUp
        {
            get
            {
                Radio radio = theRadio;
                return radio != null && TuneFact.CarrierUp(radio.TXTune, ReadTuneStanding());
            }
        }

        /// <summary>
        /// A tune of ours is up or on its way, or an ATU cycle of ours is
        /// running: the air carries a plain carrier, so nothing can modulate
        /// it (#860). A tune the radio denied stops counting once it has had
        /// time to answer.
        /// </summary>
        public bool TuningNow
        {
            get
            {
                Radio radio = theRadio;
                if (radio == null) return false;
                TuneReading now = ReadTuneStanding();
                if (now.AnotherClient) return false;
                if (radio.ATUTuneStatus == ATUTuneStatus.InProgress) return true;
                bool requested;
                long at;
                lock (_tuneFactGate)
                {
                    requested = _tuneRequested;
                    at = _tuneRequestedAtMs;
                }
                return TuneFact.CarrierUp(radio.TXTune, now)
                    || TuneFact.Pending(requested, TuneFactClock() - at, now);
            }
        }

        /// <summary>
        /// A tune carrier of ours is up, or an ON for it is still in the command
        /// queue (2026-11-02): FlexLib's tune flag, or a queued ON that has
        /// neither run nor been revoked by a later write. The getter of
        /// <see cref="TxTune"/> alone misses the second, because the queued write
        /// changes it only when the command worker runs; and no timeout stands in
        /// for that, because a queued ON can key the radio for as long as it sits
        /// there. FlexLib sets its flag as the ON is sent, so there is no gap
        /// between the two. A denied tune leaves the flag true, so it counts until
        /// an off is written: the safe direction. An ATU cycle is not counted.
        /// Read-only; safe with no radio.
        /// </summary>
        public bool TuneAskedOrUp
        {
            get
            {
                Radio radio = theRadio;
                if (radio == null) return false;
                lock (_tuneWriteGate)
                {
                    return _tuneOnQueued || radio.TXTune;
                }
            }
        }

        /// <summary>
        /// The radio's own word on transmitting right now. Read-only: nothing
        /// here is a command and nothing here gates one.
        /// </summary>
        public TuneReading ReadTuneStanding()
        {
            Radio radio = theRadio;
            if (radio == null) return TuneReading.Unknown;
            InterlockState state = radio.InterlockState;
            uint txClient = radio.TXClientHandle;
            return new TuneReading(
                Transmitting: state == InterlockState.Transmitting,
                Busy: RadioInterlockReportsTransmit,
                TxAllowed: _txAllowedReported ? radio.TXAllowed : (bool?)null,
                NotReady: state == InterlockState.NotReady,
                Reason: radio.InterlockReason,
                Source: radio.PTTSource,
                AnotherClient: txClient != 0 && txClient != clientHandle,
                RawSource: RawPttSource,
                TxClient: txClient,
                OwnClient: clientHandle);
        }
    }
}
