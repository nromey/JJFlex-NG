#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
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
        /// reminder is said as it always was. While the radio layer is silenced
        /// everything is kept for a connect that can speak.
        /// </summary>
        private void AnnounceCheckRestoreReminder()
        {
            Radio? radio = theRadio;
            string? serial = radio?.Serial;
            CheckRestoreReminderStore store = CheckRestoreReminders;
            string? reason = store.ReasonFor(serial);
            IReadOnlyList<CheckPutBack> owed = store.PutBacksFor(serial);
            if (reason == null && owed.Count == 0) return;
            if (SuppressSpeech)
            {
                Tracing.TraceLine("Check restore reminder for " + serial + " kept: speech is suppressed on this connect"
                    + (owed.Count > 0 ? ", and nothing is put back on it" : ""), TraceLevel.Info);
                return;
            }
            if (owed.Count == 0)
            {
                // DRAFT PROSE (#835): awaiting Noel.
                NoteCheckRestoreSentence(serial!, store, reason!, SentenceForCheckRestore(reason!));
                return;
            }
            Tracing.TraceLine("Check put-back: " + owed.Count + " setting(s) recorded for " + serial
                + " at an earlier disconnect (" + (reason ?? "reminder already said") + "); putting back what still holds the check's value",
                TraceLevel.Info);
            // Off this thread: the command loop this connect runs has not
            // started yet, and every write below waits for it.
            CheckPutBackTask = Task.Run(() => PutBackAtConnectAsync(radio!, serial!, reason, owed, store));
        }

        /// <summary>
        /// The put-back itself. Waits for the command loop to reach it (so the
        /// station's own queued work has gone out first), refuses to write
        /// anything while the radio reports transmit, reads each setting, and
        /// writes back only the ones still at the check's value. One ping after
        /// the writes, which the radio answers in order, confirms them; only
        /// then does a setting leave the record.
        /// </summary>
        private async Task PutBackAtConnectAsync(Radio radio, string serial, string? reason,
            IReadOnlyList<CheckPutBack> owed, CheckRestoreReminderStore store)
        {
            try
            {
                if (!await DrainCommandQueueAsync(RestoreWriteWait).ConfigureAwait(false))
                {
                    Tracing.TraceLine("Check put-back: the command loop did not run, so nothing was put back; kept for the next connect",
                        TraceLevel.Warning);
                    // DRAFT PROSE (#835): awaiting Noel.
                    NoteCheckRestoreSentence(serial, store, reason ?? CheckRestoreUnconfirmed, ReminderAsBefore(reason));
                    return;
                }
                if (!ReferenceEquals(theRadio, radio))
                {
                    Tracing.TraceLine("Check put-back: the connection went before anything was put back; kept for the next connect",
                        TraceLevel.Warning);
                    return;
                }
                if (CheckPutBackTransmitting())
                {
                    Tracing.TraceLine("Check put-back: the radio reports transmit at connect, so nothing is put back; "
                        + "the reminder is said as before and the record kept for the next connect", TraceLevel.Warning);
                    // DRAFT PROSE (#835): awaiting Noel.
                    NoteCheckRestoreSentence(serial, store, reason ?? CheckRestoreWithheld, ReminderAsBefore(reason));
                    return;
                }

                var done = new List<CheckPutBack>();
                var left = new List<CheckPutBack>();
                var notDone = new List<CheckPutBack>();
                var written = new List<CheckPutBack>();
                bool stopped = false;
                foreach (var item in owed)
                {
                    if (stopped || !ReferenceEquals(theRadio, radio)) { notDone.Add(item); continue; }
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
                    // Asked again immediately before each write, as the restore owner does.
                    if (CheckPutBackTransmitting())
                    {
                        stopped = true;
                        notDone.Add(item);
                        Tracing.TraceLine("Check put-back " + item + ": the radio began transmitting; this and the rest are kept for the next connect",
                            TraceLevel.Warning);
                        continue;
                    }
                    RestoreOutcome outcome = await WriteCheckPutBackAsync(radio, item).ConfigureAwait(false);
                    Tracing.TraceLine("Check put-back " + item + ": now=" + now + "; write " + outcome, TraceLevel.Info);
                    if (outcome == RestoreOutcome.Done) written.Add(item);
                    else
                    {
                        notDone.Add(item);
                        if (outcome == RestoreOutcome.Retry) stopped = true;
                    }
                }

                if (written.Count > 0)
                {
                    bool answered = ReferenceEquals(theRadio, radio) &&
                        await ConfirmWritesReachedRadioAsync(RestoreWriteWait).ConfigureAwait(false);
                    foreach (var item in written)
                    {
                        if (answered)
                        {
                            store.RemovePutBack(serial, item);
                            done.Add(item);
                            Tracing.TraceLine("Check put-back " + item + ": put back, the radio answered; now reads "
                                + (ReadCheckPutBackSetting(radio, item) ?? "(could not be read)"), TraceLevel.Info);
                        }
                        else
                        {
                            notDone.Add(item);
                            Tracing.TraceLine("Check put-back " + item + ": written but not confirmed; kept for the next connect",
                                TraceLevel.Warning);
                        }
                    }
                }

                var result = new CheckPutBackOutcome(done.Select(i => i.Setting), left.Select(i => i.Setting), notDone.Select(i => i.Setting));
                Tracing.TraceLine("Check put-back for " + serial + " finished: " + result.Reason, TraceLevel.Info);
                // The outcome is what is owed to the operator now, until it is said.
                store.Add(serial, result.Reason);
                // DRAFT PROSE (#835): awaiting Noel.
                NoteCheckRestoreSentence(serial, store, result.Reason, result.Sentence());
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check put-back failed; the record is kept for the next connect: " + ex, TraceLevel.Error);
            }
        }

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

        /// <summary>
        /// Write the original back through the same paths the check's own
        /// restores use: queued setters under <see cref="RestoreWritesAtReceiveAsync"/>,
        /// which refuses a queued write at its run if the radio then reports
        /// transmit, and the reply-confirmed full duplex request.
        /// </summary>
        private async Task<RestoreOutcome> WriteCheckPutBackAsync(Radio radio, CheckPutBack item)
        {
            string name = "check put-back: " + item.Setting;
            string v = item.Original;
            static bool Int(string s, out int n) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            try
            {
                switch (item.Setting)
                {
                    case CheckPutBack.RfPower when Int(v, out int power):
                        return await RestoreWritesAtReceiveAsync(name, () =>
                        {
                            using (AppInitiatedSettingChanges()) XmitPower = power;
                        }).ConfigureAwait(false);
                    case CheckPutBack.TunePower when Int(v, out int tune):
                        return await RestoreWritesAtReceiveAsync(name, () =>
                        {
                            using (AppInitiatedSettingChanges()) TunePower = tune;
                        }).ConfigureAwait(false);
                    case CheckPutBack.Monitor:
                        return await RestoreWritesAtReceiveAsync(name, () =>
                            Monitor = v == "1" ? OffOnValues.on : OffOnValues.off).ConfigureAwait(false);
                    case CheckPutBack.TxAntenna when v.Length > 0:
                        return await RestoreWritesAtReceiveAsync(name, () =>
                        {
                            using (AppInitiatedSettingChanges()) TXAntennaName = v;
                        }).ConfigureAwait(false);
                    case CheckPutBack.FullDuplex:
                        return await SetFullDuplexConfirmedAsync(v == "1", radio, radio.CurrentCommandConnection,
                                note => Tracing.TraceLine("Check put-back full duplex: " + note, TraceLevel.Info)).ConfigureAwait(false)
                            ? RestoreOutcome.Done : RestoreOutcome.Failed;
                    case CheckPutBack.XvtrDrive when DriveBand(radio, item) is { } band &&
                        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbm):
                        return await RestoreWritesAtReceiveAsync(name, () =>
                            q.Enqueue((FunctionDel)(() => { band.MaxPower = dbm; }), "CheckPutBackDrive")).ConfigureAwait(false);
                    default:
                        Tracing.TraceLine("Check put-back " + item + ": nothing here can write it", TraceLevel.Warning);
                        return RestoreOutcome.Failed;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check put-back " + item + ": the write failed: " + ex.Message, TraceLevel.Warning);
                return RestoreOutcome.Failed;
            }
        }

        /// <summary>The sentence a recorded reason calls for when nothing is (or can be) put back.</summary>
        private static string SentenceForCheckRestore(string reason) =>
            CheckPutBackOutcome.TryParse(reason, out var outcome) ? outcome.Sentence() : ReminderAsBefore(reason);

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
