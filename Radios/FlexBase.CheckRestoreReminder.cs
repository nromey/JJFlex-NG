#nullable enable
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the reminder that an audio check did not put this radio's
    /// power and antenna settings back at a disconnect (#835, fix round
    /// 2026-10-29, item 2). See <see cref="CheckRestoreReminderStore"/> for why
    /// it is said again at the next connect.
    /// </summary>
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

        /// <summary>
        /// A teardown before a disconnect did not put this radio back
        /// (<paramref name="reason"/> is <see cref="CheckRestoreWithheld"/> or
        /// <see cref="CheckRestoreUnconfirmed"/>): remember to say so at the next
        /// connect to it. Called by Check your audio while the radio is still
        /// this FlexBase's. Never throws.
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
            CheckRestoreReminders.Add(serial, reason);
            Tracing.TraceLine("Check restore reminder (" + reason + ") kept for the next connect to " + serial, TraceLevel.Info);
        }

        /// <summary>
        /// At connect, once the command loop runs: if an audio check left this
        /// radio unrestored at an earlier disconnect, hand the reminder to the
        /// connect briefing with the warning alarm, then forget it. While the
        /// radio layer is silenced it is kept for a connect that can speak.
        /// </summary>
        private void AnnounceCheckRestoreReminder()
        {
            string? serial = theRadio?.Serial;
            CheckRestoreReminderStore store = CheckRestoreReminders;
            string? reason = store.ReasonFor(serial);
            if (reason == null) return;
            if (SuppressSpeech)
            {
                Tracing.TraceLine("Check restore reminder for " + serial + " kept: speech is suppressed on this connect", TraceLevel.Info);
                return;
            }
            // DRAFT PROSE (#835): awaiting Noel.
            string text = Lexicon.Get(reason == CheckRestoreUnconfirmed
                ? "audio.check.teardown_unconfirmed_reminder"
                : "audio.check.teardown_withheld_reminder");
            // A hazard on the radio, so it sorts with the other station facts,
            // first in the composed statement, and it carries the alarm.
            ConnectBriefing.Current.Note(new ConnectFact(
                ConnectFactKind.ProfileStewardship, text, text,
                VerbosityLevel.Critical, Speech.SpeechSubject.CheckSettingsNotRestored, alarm: true));
            store.Remove(serial);
            Tracing.TraceLine("Check restore reminder (" + reason + ") for " + serial + " handed to the connect briefing", TraceLevel.Info);
        }
    }
}
