#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the reminder that an audio check did not put this radio's
    /// power and antenna settings back at a disconnect (#835, fix round
    /// 2026-10-29, item 2), and, since fix round 1030, the put-back itself: the
    /// next connect puts back each setting that still holds the check's value.
    /// See <see cref="CheckRestoreReminderStore"/> for why it waits for the next
    /// connect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ruling (Noel, 2026-10-29 21:07 and 21:13).</b> On the bench a
    /// disconnect came while the hand mic held the radio keyed during Loopback;
    /// the restores were rightly withheld, and the next connect sounded the
    /// alarm and asked him to check his settings. "Couldn't we keep track of
    /// previous settings somehow and if this alarm happens just reset to the
    /// last known good state?" Then: "I think so. We could still alarm for
    /// informational purposes."
    /// </para>
    /// <para>
    /// <b>What is put back, and what is not.</b> Only a setting that STILL
    /// holds the value the check set. Anything else means the reconnect's own
    /// profile load or another client has changed it since, and that newer
    /// value is not the check's to overwrite. Nothing is written while the
    /// radio reports transmit; then the reminder is said exactly as before and
    /// the record waits for the next connect.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>The radio was still transmitting, so the restores were held back.</summary>
        public const string CheckRestoreWithheld = "withheld";

        /// <summary>The restores could not be confirmed before the disconnect.</summary>
        public const string CheckRestoreUnconfirmed = "unconfirmed";

        /// <summary>Where the reminders are kept, per radio serial, under the settings root.</summary>
        internal CheckRestoreReminderStore CheckRestoreReminders =>
            CheckRestoreRemindersOverride ?? new CheckRestoreReminderStore(RadioConfig.AppDataRoot);

        /// <summary>For tests: a store in a folder of their own.</summary>
        internal CheckRestoreReminderStore? CheckRestoreRemindersOverride { get; set; }

        /// <summary>The connect briefing the reminder goes through; tests give it one of their own.</summary>
        internal ConnectBriefing CheckRestoreBriefing => CheckRestoreBriefingOverride ?? ConnectBriefing.Current;

        /// <summary>For tests: a briefing of their own, so a connect's whole life can be driven.</summary>
        internal ConnectBriefing? CheckRestoreBriefingOverride { get; set; }

        /// <summary>The put-back started by the last connect, for tests and the trace. Never faults.</summary>
        internal Task CheckPutBackTask { get; private set; } = Task.CompletedTask;

        // ── What each restore would put back ─────────────────────────────

        private readonly object _checkPutBackGate = new();
        private readonly List<(CheckRestorations.Entry Entry, CheckPutBack Item)> _checkPutBackPlan = new();

        /// <summary>
        /// Say what <paramref name="entry"/>, a restore an audio check has
        /// registered with <see cref="CheckRestorations"/>, puts back: each
        /// station-wide setting it changed, with its original value and the
        /// value the check set. Kept beside the entry; if a disconnect comes
        /// before the entry has put its setting back, the reminder records it
        /// for the next connect (<see cref="NoteCheckRestoreReminder"/>). A
        /// setting the check did not really change is ignored. Never throws.
        /// </summary>
        public void DescribeCheckPutBack(CheckRestorations.Entry? entry, params CheckPutBack[] items)
        {
            if (entry == null || items == null) return;
            lock (_checkPutBackGate)
            {
                // An entry that has put its setting back owes nothing any more.
                _checkPutBackPlan.RemoveAll(p => PutBackDone(p.Entry));
                foreach (var item in items)
                    if (item != null && item.Changed) _checkPutBackPlan.Add((entry, item));
            }
        }

        /// <summary>The settings whose restore has not yet put them back: still waiting, failed, or abandoned.</summary>
        internal IReadOnlyList<CheckPutBack> CheckPutBacksOwed()
        {
            lock (_checkPutBackGate)
                return _checkPutBackPlan.Where(p => !PutBackDone(p.Entry)).Select(p => p.Item).ToList();
        }

        private static bool PutBackDone(CheckRestorations.Entry entry) =>
            entry.Task.IsCompletedSuccessfully && entry.Task.Result;

        // ── At the disconnect ────────────────────────────────────────────

        /// <summary>
        /// A teardown before a disconnect did not put this radio back
        /// (<paramref name="reason"/> is <see cref="CheckRestoreWithheld"/> or
        /// <see cref="CheckRestoreUnconfirmed"/>): remember to say so at the next
        /// connect to it, and remember each setting still owed, with the value it
        /// had and the value the check set (fix round 1030). Called by Check your
        /// audio while the radio is still this FlexBase's. Never throws.
        /// </summary>
        public void NoteCheckRestoreReminder(string reason)
        {
            string? serial = theRadio?.Serial;
            if (string.IsNullOrEmpty(serial)) serial = ConnectedSerial;
            if (string.IsNullOrEmpty(serial))
            {
                Tracing.TraceLine("Check restore reminder: no radio serial, so nothing can be kept for the next connect", TraceLevel.Warning);
                return;
            }
            var store = CheckRestoreReminders;
            var owed = CheckPutBacksOwed();
            if (owed.Count > 0)
            {
                store.AddPutBacks(serial, owed);
                foreach (var item in owed)
                    Tracing.TraceLine("Check put-back recorded for the next connect to " + serial + ": " + item, TraceLevel.Info);
            }
            else
                Tracing.TraceLine("Check put-back: no station setting is recorded as owed to " + serial, TraceLevel.Info);
            store.Add(serial, reason);
            Tracing.TraceLine("Check restore reminder (" + reason + ") kept for the next connect to " + serial, TraceLevel.Info);
        }

        // ── At the next connect ──────────────────────────────────────────

        /// <summary>
        /// At connect, once the command loop runs: if an audio check left this
        /// radio unrestored at an earlier disconnect, put back what it recorded
        /// (fix round 1030), then hand what happened to the connect briefing
        /// with the warning alarm. With nothing recorded to put back, the
        /// reminder is said as it always was.
        /// </summary>
        /// <remarks>
        /// <b>A connect with speech suppressed puts back too, and says nothing</b>
        /// (ruled by Noel 2026-10-29 23:52: "Easy, put back, even if silent. Why
        /// not, you lose nothing though a profile restore could do it anyway ...
        /// I see no negative with the extra insurance."). Same reads, same
        /// transmit question, same confirmation. What it did is kept as the
        /// reminder's reason, so the next connect that can speak says it, with
        /// the alarm, as information. Until fix round 1030 P2 a silent connect
        /// put nothing back and kept everything.
        /// </remarks>
        private void AnnounceCheckRestoreReminder()
        {
            Radio? radio = theRadio;
            string? serial = radio?.Serial;
            CheckRestoreReminderStore store = CheckRestoreReminders;
            // An off-air take's record joins the put-back first (fix round 1030 P2, ruling (a)).
            TakeOffAirRecoveryIntoPutBack(serial, store);
            string? reason = store.ReasonFor(serial);
            IReadOnlyList<CheckPutBack> owed = store.PutBacksFor(serial);
            if (reason == null && owed.Count == 0) return;
            bool silent = SuppressSpeech;
            if (owed.Count == 0)
            {
                if (silent)
                {
                    Tracing.TraceLine("Check restore reminder (" + reason + ") for " + serial
                        + " kept: speech is suppressed on this connect, so it is said at the next connect that can speak", TraceLevel.Info);
                    return;
                }
                // DRAFT PROSE (#835): awaiting Noel.
                NoteCheckRestoreSentence(serial!, store, reason!, SentenceForCheckRestore(reason!));
                return;
            }
            if (!BeginPutBack(serial!))
            {
                Tracing.TraceLine("Check put-back for " + serial + ": another connection in this process is already putting it back; "
                    + "this connect leaves it to that one and keeps the record", TraceLevel.Info);
                return;
            }
            Tracing.TraceLine("Check put-back: " + owed.Count + " setting(s) recorded for " + serial
                + " at an earlier disconnect (" + (reason ?? "reminder already said") + "); putting back what still holds the check's value"
                + (silent ? ", on a connect with speech suppressed: nothing is said now, and what it does is said at the next connect that can speak" : ""),
                TraceLevel.Info);
            // Off this thread: the command loop this connect runs has not
            // started yet, and every write below waits for it.
            // The connection is pinned here, with the radio: every send below
            // asks at its run that it is still this one (Codex round 1030, B1).
            CommandConnection? connection = radio?.CurrentCommandConnection;
            CheckPutBackTask = Task.Run(async () =>
            {
                try { await PutBackAtConnectAsync(radio!, connection, serial!, reason, owed, store, silent).ConfigureAwait(false); }
                finally { EndPutBack(serial!); }
            });
        }

        /// <summary>The radios a put-back is running for in this process, so two connections to one radio never put back at once.</summary>
        private static readonly HashSet<string> s_putBackInFlight = new(StringComparer.OrdinalIgnoreCase);

        private static bool BeginPutBack(string serial)
        {
            lock (s_putBackInFlight) return s_putBackInFlight.Add(serial);
        }

        private static void EndPutBack(string serial)
        {
            lock (s_putBackInFlight) s_putBackInFlight.Remove(serial);
        }

        /// <summary>
        /// The put-back itself. Waits for the command loop to reach it (so the
        /// station's own queued work has gone out first), refuses to write
        /// anything while the radio reports transmit, reads each setting, and
        /// sends back only the ones still at the check's value. Each send asks
        /// again at the moment it runs, and a setting leaves the record only on
        /// its own command's success reply (Codex round 1030, B1 to B3; see
        /// <see cref="SendCheckPutBackAsync"/>).
        /// </summary>
        private async Task PutBackAtConnectAsync(Radio radio, CommandConnection? connection, string serial, string? reason,
            IReadOnlyList<CheckPutBack> owed, CheckRestoreReminderStore store, bool silent)
        {
            try
            {
                if (!await DrainCommandQueueAsync(RestoreWriteWait).ConfigureAwait(false))
                {
                    Tracing.TraceLine("Check put-back: the command loop did not run, so nothing was put back; kept for the next connect",
                        TraceLevel.Warning);
                    if (silent) { KeptForASpeakingConnect(serial); return; }
                    // DRAFT PROSE (#835): awaiting Noel.
                    NoteCheckRestoreSentence(serial, store, reason ?? CheckRestoreUnconfirmed, SentenceWhenNothingPutBack(reason, owed));
                    return;
                }
                if (!PutBackConnectionLive(radio, connection))
                {
                    Tracing.TraceLine("Check put-back: the connection went before anything was put back; kept for the next connect",
                        TraceLevel.Warning);
                    return;
                }
                if (CheckPutBackTransmitting())
                {
                    Tracing.TraceLine("Check put-back: the radio reports transmit at connect, so nothing is put back; "
                        + "the reminder is said as before and the record kept for the next connect", TraceLevel.Warning);
                    if (silent) { KeptForASpeakingConnect(serial); return; }
                    // DRAFT PROSE (#835): awaiting Noel.
                    NoteCheckRestoreSentence(serial, store, reason ?? CheckRestoreWithheld, SentenceWhenNothingPutBack(reason, owed));
                    return;
                }

                var done = new List<CheckPutBack>();
                var left = new List<CheckPutBack>();
                var notDone = new List<CheckPutBack>();
                bool stopped = false;
                foreach (var item in owed)
                {
                    if (stopped || !PutBackConnectionLive(radio, connection)) { notDone.Add(item); continue; }
                    string? now = ReadCheckPutBackSetting(radio, item);
                    if (now != null && item.Same(now, item.Original))
                    {
                        store.RemovePutBack(serial, item);
                        Tracing.TraceLine("Check put-back " + item + ": now=" + now
                            + "; already holds its original, nothing to write", TraceLevel.Info);
                        continue;
                    }
                    if (now == null && !(item.Setting == CheckPutBack.XvtrDrive && DriveBand(radio, item) == null))
                    {
                        // Not read is not "changed by something else": no
                        // transmit slice yet, or the read threw. Kept, and said,
                        // so the next connect tries again. A transverter band
                        // that no longer exists has no drive left to put back,
                        // and is left alone below.
                        notDone.Add(item);
                        Tracing.TraceLine("Check put-back " + item + ": could not be read; kept for the next connect",
                            TraceLevel.Warning);
                        continue;
                    }
                    if (now == null || !item.Same(now, item.CheckSet))
                    {
                        store.RemovePutBack(serial, item);
                        left.Add(item);
                        Tracing.TraceLine("Check put-back " + item + ": now=" + (now ?? "(could not be read)")
                            + "; not the check's value, so something else has changed it since (the profile load or another client); left alone",
                            TraceLevel.Info);
                        continue;
                    }
                    // An early answer only; the send asks again at its run.
                    if (CheckPutBackTransmitting())
                    {
                        stopped = true;
                        notDone.Add(item);
                        Tracing.TraceLine("Check put-back " + item + ": the radio began transmitting; this and the rest are kept for the next connect",
                            TraceLevel.Warning);
                        continue;
                    }
                    var (sent, detail) = await SendCheckPutBackAsync(radio, connection, item).ConfigureAwait(false);
                    switch (sent)
                    {
                        case PutBackSend.Accepted:
                            store.RemovePutBack(serial, item);
                            done.Add(item);
                            Tracing.TraceLine("Check put-back " + item + ": now=" + now + "; put back, the radio accepted " + detail
                                + "; now reads " + (ReadCheckPutBackSetting(radio, item) ?? "(could not be read)"), TraceLevel.Info);
                            break;
                        case PutBackSend.Changed:
                            store.RemovePutBack(serial, item);
                            left.Add(item);
                            Tracing.TraceLine("Check put-back " + item + ": at the write's run it read " + detail
                                + ", no longer the check's value, so something else changed it after the decision; nothing written, left alone",
                                TraceLevel.Info);
                            break;
                        case PutBackSend.Transmitting:
                        case PutBackSend.ConnectionGone:
                        case PutBackSend.Unanswered:
                            stopped = true;
                            notDone.Add(item);
                            Tracing.TraceLine("Check put-back " + item + ": " + detail + "; this and the rest are kept for the next connect",
                                TraceLevel.Warning);
                            break;
                        default:
                            notDone.Add(item);
                            Tracing.TraceLine("Check put-back " + item + ": " + detail + "; not confirmed, kept for the next connect",
                                TraceLevel.Warning);
                            break;
                    }
                }

                var thisConnect = new CheckPutBackOutcome(done.Select(i => i.Setting), left.Select(i => i.Setting), notDone.Select(i => i.Setting));
                // A put-back an earlier silent connect did, and nobody has heard
                // yet, is said together with this one (fix round 1030 P2).
                var result = CheckPutBackOutcome.TryParse(reason, out var earlier)
                    ? CheckPutBackOutcome.Merge(earlier, thisConnect, owed.Select(i => i.Setting))
                    : thisConnect;
                Tracing.TraceLine("Check put-back for " + serial + " finished: " + thisConnect.Reason
                    + (ReferenceEquals(result, thisConnect) ? "" : "; with what an earlier connect did and nobody has heard yet: " + result.Reason),
                    TraceLevel.Info);
                // The outcome is what is owed to the operator now, until it is said.
                store.Add(serial, result.Reason);
                if (silent) { KeptForASpeakingConnect(serial); return; }
                // DRAFT PROSE (#835): awaiting Noel.
                NoteCheckRestoreSentence(serial, store, result.Reason, result.Sentence());
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check put-back failed; the record is kept for the next connect: " + ex, TraceLevel.Error);
            }
        }

        /// <summary>Still the radio and the command connection the put-back started on.</summary>
        private bool PutBackConnectionLive(Radio radio, CommandConnection? connection) =>
            radio != null && connection != null && ReferenceEquals(theRadio, radio) && radio.Connected &&
            ReferenceEquals(radio.CurrentCommandConnection, connection);

        /// <summary>The owner's own question: our transmit state or the radio's interlock.</summary>
        private bool CheckPutBackTransmitting()
        {
            try { return Transmit || RadioInterlockReportsTransmit; }
            catch { return true; }
        }

        /// <summary>The setting's current value as the radio reports it, in <see cref="CheckPutBack"/>'s spelling, or null when it cannot be read.</summary>
        private string? ReadCheckPutBackSetting(Radio radio, CheckPutBack item)
        {
            try
            {
                switch (item.Setting)
                {
                    case CheckPutBack.RfPower: return CheckPutBack.Num(radio.RFPower);
                    case CheckPutBack.TunePower: return CheckPutBack.Num(radio.TunePower);
                    case CheckPutBack.Monitor: return CheckPutBack.Bit(radio.TXMonitor);
                    case CheckPutBack.FullDuplex: return CheckPutBack.Bit(FullDuplexState(radio, radio.CurrentCommandConnection));
                    case CheckPutBack.TxAntenna: return SliceForTransmitSetting()?.TXAnt;
                    case CheckPutBack.XvtrDrive:
                        return DriveBand(radio, item) is { } band
                            ? band.MaxPower.ToString("R", CultureInfo.InvariantCulture) : null;
                    default: return null;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check put-back " + item.Setting + ": could not read it: " + ex.Message, TraceLevel.Warning);
                return null;
            }
        }

        private static Xvtr? DriveBand(Radio radio, CheckPutBack item) =>
            int.TryParse(item.Target, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
            radio.FindXvtrByIndex(index) is { Valid: true } band ? band : null;

        /// <summary>What one put-back send came to.</summary>
        private enum PutBackSend
        {
            /// <summary>The radio answered the setting's own command with success: the only evidence that retires it.</summary>
            Accepted,
            /// <summary>The radio answered the command with a failure code.</summary>
            Rejected,
            /// <summary>The command went out and no answer came in time.</summary>
            Unanswered,
            /// <summary>Never sent: the loop never reached it, the transport refused it, or nothing here can write the setting.</summary>
            NotSent,
            /// <summary>At the send's run the setting could not be read; nothing was sent.</summary>
            Unread,
            /// <summary>At the send's run our Mox or the radio's interlock said transmit; nothing was sent.</summary>
            Transmitting,
            /// <summary>At the send's run the setting no longer held the check's value; nothing was sent.</summary>
            Changed,
            /// <summary>At the send's run the radio or the connection was no longer the put-back's; nothing was sent.</summary>
            ConnectionGone,
        }

        /// <summary>
        /// Put one setting back, deciding at the moment the command loop runs
        /// the send, not before it was queued (Codex round 1030, B1 and B2):
        /// still the same radio and connection, neither our Mox nor the radio's
        /// interlock saying transmit, and the setting still at the check's
        /// value. Only then is the setting's own command sent, through the
        /// reply-bearing path, and only a success reply to THAT command counts
        /// as put back (B3). A ping answered after it proves the radio
        /// processed the command, not that it took it, and FlexLib's getter
        /// echoes whatever its setter cached, so neither is evidence here.
        /// Full duplex goes the same way; until this round it had no transmit
        /// question at its run at all.
        /// </summary>
        /// <remarks>
        /// The command is sent raw, not through FlexLib's setter, because the
        /// setter discards the reply and caches the requested value first. So
        /// FlexLib's cache moves only when the radio reports the new value,
        /// and a rejected write cannot read back as restored. What the setters
        /// announced is still announced: power, tune power and the antenna are
        /// marked as the application's own writes
        /// (<see cref="NoteRadioSettingWritten"/>, for the hold after a
        /// reflected-power cut), the antenna request is recorded for its
        /// trace, and an accepted full duplex becomes the reply-proven state
        /// the full duplex control keeps. Never throws.
        /// </remarks>
        private async Task<(PutBackSend Result, string Detail)> SendCheckPutBackAsync(Radio radio, CommandConnection? connection, CheckPutBack item)
        {
            var queue = q;
            if (queue == null || !queue.MainLoop) return (PutBackSend.NotSent, "the command loop is not running");
            var decided = new TaskCompletionSource<(PutBackSend, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new TaskCompletionSource<CommandReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 0 waiting; 1 the loop has taken it and decides; 2 too late, so the loop must do nothing.
            var claim = new int[1];
            try
            {
                queue.Enqueue((FunctionDel)(() =>
                {
                    if (System.Threading.Interlocked.CompareExchange(ref claim[0], 1, 0) != 0) return;
                    try { decided.TrySetResult(DecideAndSendPutBack(radio, connection, item, reply)); }
                    catch (Exception ex) { decided.TrySetResult((PutBackSend.NotSent, "the send threw: " + ex.Message)); }
                }), "CheckPutBack " + item.Setting);
            }
            catch (Exception ex) { return (PutBackSend.NotSent, "it could not be queued: " + ex.Message); }

            var first = await Task.WhenAny(decided.Task, Task.Delay(RestoreWriteWait)).ConfigureAwait(false);
            if (!ReferenceEquals(first, decided.Task) &&
                System.Threading.Interlocked.CompareExchange(ref claim[0], 2, 0) == 0)
                return (PutBackSend.NotSent, "the command loop did not reach the write in time, and now never will");
            var (result, detail) = await decided.Task.ConfigureAwait(false);
            if (result != PutBackSend.Accepted) return (result, detail);

            // Sent: wait for the radio's own answer to this command.
            var answered = await Task.WhenAny(reply.Task, Task.Delay(RestoreWriteWait)).ConfigureAwait(false);
            if (!ReferenceEquals(answered, reply.Task))
                return (PutBackSend.Unanswered, "'" + detail + "' went out and the radio did not answer it");
            CommandReply r = reply.Task.Result;
            return r.Acknowledged
                ? (PutBackSend.Accepted, "'" + detail + "'")
                : (PutBackSend.Rejected, "the radio rejected '" + detail + "' (" + r.Code.ToString("X8", CultureInfo.InvariantCulture)
                    + (r.Text.Length > 0 ? " " + r.Text : "") + ")");
        }

        /// <summary>
        /// On the command loop: the questions, then the send. Accepted here
        /// means only that the command went out, carrying its text; the reply
        /// decides. Anything else says why nothing was sent.
        /// </summary>
        private (PutBackSend, string) DecideAndSendPutBack(Radio radio, CommandConnection? connection, CheckPutBack item,
            TaskCompletionSource<CommandReply> reply)
        {
            if (!PutBackConnectionLive(radio, connection))
                return (PutBackSend.ConnectionGone, "at the write's run the radio or its connection was no longer the one this put-back started on; nothing sent");
            if (CheckPutBackTransmitting())
                return (PutBackSend.Transmitting, "at the write's run the radio reported transmit ("
                    + (Transmit ? "our Mox" : "its interlock " + radio.InterlockState) + "); nothing sent");
            string? now = ReadCheckPutBackSetting(radio, item);
            if (now == null)
            {
                if (item.Setting == CheckPutBack.XvtrDrive && DriveBand(radio, item) == null)
                    return (PutBackSend.Changed, "no transverter band " + item.Target);
                return (PutBackSend.Unread, "at the write's run it could not be read; nothing sent");
            }
            if (!item.Same(now, item.CheckSet)) return (PutBackSend.Changed, now);

            string v = item.Original;
            static bool Int(string s, out int n) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            string? command = null;
            Action? announce = null;
            Action? accepted = null;
            switch (item.Setting)
            {
                case CheckPutBack.RfPower when Int(v, out int power):
                    command = "transmit set rfpower=" + power.ToString(CultureInfo.InvariantCulture);
                    announce = () => NoteRadioSettingWritten("XmitPower", v);
                    break;
                case CheckPutBack.TunePower when Int(v, out int tune):
                    command = "transmit set tunepower=" + tune.ToString(CultureInfo.InvariantCulture);
                    announce = () => NoteRadioSettingWritten("TunePower", v);
                    break;
                case CheckPutBack.Monitor:
                    command = "transmit set mon=" + (v == "1" ? "1" : "0");
                    break;
                case CheckPutBack.TxAntenna when v.Length > 0:
                    if (SliceForTransmitSetting(out string basis) is not { } slice)
                        return (PutBackSend.Unread, "at the write's run there was no transmit slice; nothing sent");
                    command = "slice set " + slice.Index.ToString(CultureInfo.InvariantCulture) + " txant=" + v;
                    announce = () =>
                    {
                        Tracing.TraceLine($"Antenna:TX request slice {slice.Index} ({basis}): '{slice.TXAnt}' -> '{v}' (check put-back)", TraceLevel.Info);
                        NoteRadioSettingWritten("TXAntennaName", v);
                        _txAntRequested = v;
                        _txAntRequestedSlice = slice.Index;
                    };
                    break;
                case CheckPutBack.FullDuplex:
                    bool duplex = v == "1";
                    command = "radio set full_duplex_enabled=" + (duplex ? "1" : "0");
                    // As the full duplex control does: the reply-proven state, pinned to this connection.
                    accepted = () => AcceptFullDuplexReply(radio, connection!, duplex);
                    break;
                case CheckPutBack.XvtrDrive when DriveBand(radio, item) is { } band &&
                    double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbm):
                    command = "xvtr set " + band.Index.ToString(CultureInfo.InvariantCulture) + " max_power="
                        + dbm.ToString("F2", CultureInfo.InvariantCulture);
                    break;
            }
            if (command == null) return (PutBackSend.NotSent, "nothing here can write it");
            if (announce != null) using (AppInitiatedSettingChanges()) announce();
            string? refusal = SendRadioCommandWithReply(radio, command, r =>
            {
                if (r.Acknowledged) accepted?.Invoke();
                reply.TrySetResult(r);
            });
            return refusal == null ? (PutBackSend.Accepted, command) : (PutBackSend.NotSent, "'" + command + "' was not sent: " + refusal);
        }

        /// <summary>The sentence a recorded reason calls for when nothing is (or can be) put back.</summary>
        private static string SentenceForCheckRestore(string reason) =>
            CheckPutBackOutcome.TryParse(reason, out var outcome) ? outcome.Sentence() : ReminderAsBefore(reason);

        /// <summary>
        /// The sentence when this connect could put nothing back: the reminder as
        /// before, or, when an earlier silent connect's put-back has not been
        /// heard yet, what that one did, with what is still owed named as not
        /// put back (fix round 1030 P2).
        /// </summary>
        private static string SentenceWhenNothingPutBack(string? reason, IReadOnlyList<CheckPutBack> owed) =>
            CheckPutBackOutcome.TryParse(reason, out var earlier)
                ? CheckPutBackOutcome.Merge(earlier,
                    new CheckPutBackOutcome(Array.Empty<string>(), Array.Empty<string>(), owed.Select(i => i.Setting)),
                    owed.Select(i => i.Setting)).Sentence()
                : ReminderAsBefore(reason);

        /// <summary>A silent connect's put-back is kept, record and outcome, for the next connect that can speak.</summary>
        private static void KeptForASpeakingConnect(string serial) =>
            Tracing.TraceLine("Check put-back for " + serial + ": speech is suppressed on this connect, so nothing is said; "
                + "the next connect that can speak says it, with the alarm", TraceLevel.Info);

        /// <summary>The reminder as it was before fix round 1030: check these settings yourself.</summary>
        private static string ReminderAsBefore(string? reason) => Lexicon.Get(reason == CheckRestoreWithheld
            ? "audio.check.teardown_withheld_reminder"
            : "audio.check.teardown_unconfirmed_reminder");

        /// <summary>
        /// Hand <paramref name="text"/> to the connect briefing with the warning
        /// alarm, and forget the reason only once it has been handed to speech.
        /// Settings still owed stay in the record either way.
        /// </summary>
        private void NoteCheckRestoreSentence(string serial, CheckRestoreReminderStore store, string reason, string text)
        {
            // Forgotten only once the briefing has handed it to speech (Codex
            // round 1, F1): during a connect a note is only collected, and a
            // radio that goes away before the settle discards it. Until then
            // the record stays, and the next connect notes it again. The
            // listener captures the briefing, the store and the serial, never
            // this FlexBase, so a failed connect's rig is not kept alive by it.
            ConnectBriefing briefing = CheckRestoreBriefing;
            System.Action<BriefingUtterance>? delivered = null;
            delivered = u =>
            {
                if (u.Subject != Speech.SpeechSubject.CheckSettingsNotRestored || u.Text != text) return;
                lock (CheckRestoreDeliveryGate)
                {
                    briefing.Emitted -= delivered;
                    if (s_checkRestoreDelivery is { } current && ReferenceEquals(current.Listener, delivered))
                        s_checkRestoreDelivery = null;
                }
                store.RemoveReason(serial);
                Tracing.TraceLine("Check restore reminder (" + reason + ") for " + serial + " handed to speech; forgotten", TraceLevel.Info);
            };
            // One listener at a time, process-wide: the sentence names no radio,
            // so a listener left from an earlier connect to another radio must
            // not take this one's delivery as its own.
            lock (CheckRestoreDeliveryGate)
            {
                Unsubscribe(s_checkRestoreDelivery);
                briefing.Emitted += delivered;
                s_checkRestoreDelivery = (briefing, delivered);
            }
            // A hazard on the radio, so it sorts with the other station facts,
            // first in the composed statement, and it carries the alarm. The
            // alarm stays when everything was put back: it is information now
            // (Noel, 21:13, "We could still alarm for informational purposes").
            briefing.Note(new ConnectFact(
                ConnectFactKind.ProfileStewardship, text, text,
                VerbosityLevel.Critical, Speech.SpeechSubject.CheckSettingsNotRestored, alarm: true));
            Tracing.TraceLine("Check restore reminder (" + reason + ") for " + serial
                + " noted in the connect briefing; the record is kept until it is handed to speech", TraceLevel.Info);
        }

        /// <summary>The listener waiting for the last noted reminder to be spoken, if any.</summary>
        private static (ConnectBriefing Briefing, System.Action<BriefingUtterance> Listener)? s_checkRestoreDelivery;
        private static readonly object CheckRestoreDeliveryGate = new();

        private static void Unsubscribe((ConnectBriefing Briefing, System.Action<BriefingUtterance> Listener)? pending)
        {
            if (pending is { } p) p.Briefing.Emitted -= p.Listener;
        }
    }
}
