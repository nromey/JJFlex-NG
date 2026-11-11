#nullable enable
using System.Globalization;

namespace Radios.Alarms
{
    /// <summary>
    /// The alarm journal's one-sentence readout for Settings, Diagnostics:
    /// whether a journal is running, and if it is, its rate and what it has
    /// written and dropped, in words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Displayed, never spoken.</b> The line is status the operator reads
    /// on the tab; nothing here goes to the screen reader on the application's
    /// own account, so it stays off the verbosity dial.
    /// </para>
    /// <para>
    /// <b>Where the rate comes from.</b> <see cref="AlarmJournalStats"/>
    /// carries counts, not a rate, and the tab has no timer to difference two
    /// readings. But it does carry the in-memory ring: every record of the last
    /// two minutes (<see cref="AlarmJournal.RingSeconds"/>), with the seconds
    /// between its oldest and newest. Records in the ring, less one, over that
    /// span is the rate across the last two minutes, from one reading. With
    /// under a second of span there is no honest rate yet, and the line says
    /// so rather than inventing one.
    /// </para>
    /// <para>
    /// <b>Three states, and a fourth.</b> No radio (the alarm host is not
    /// attached); a radio with nothing armed (no alarm or recorded meter, so
    /// the journal receives nothing); recording. The fourth is a radio with no
    /// journal because the settings folder was not found.
    /// </para>
    /// </remarks>
    public static class AlarmJournalReadout
    {
        /// <summary>
        /// Records a second over the ring's span, or null when the span is
        /// under a second or holds fewer than two records.
        /// </summary>
        public static double? RecordsPerSecond(AlarmJournalStats stats)
        {
            if (stats == null || stats.RingRecords < 2 || stats.RetainedRingSeconds < 1.0) return null;
            return (stats.RingRecords - 1) / stats.RetainedRingSeconds;
        }

        /// <summary>The sentence for the tab.</summary>
        /// <param name="radioAttached">The alarm host is attached to a radio.</param>
        /// <param name="recordedMeters">Meters the journal receives on this connection.</param>
        /// <param name="stats">The journal's own figures, or null when there is no journal.</param>
        public static string Sentence(bool radioAttached, int recordedMeters, AlarmJournalStats? stats)
        {
            if (!radioAttached) return Lexicon.Get("settings.diagnostics.alarm_journal.no_radio");
            if (stats == null) return Lexicon.Get("settings.diagnostics.alarm_journal.unavailable");
            if (recordedMeters <= 0) return Lexicon.Get("settings.diagnostics.alarm_journal.nothing_armed");

            double? perSecond = RecordsPerSecond(stats);
            string rate = perSecond.HasValue
                ? Lexicon.Get("settings.diagnostics.alarm_journal.rate",
                    ("perSecond", perSecond.Value.ToString(perSecond.Value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture)))
                : Lexicon.Get("settings.diagnostics.alarm_journal.rate_not_yet");
            string written = stats.RecordsWritten.ToString("N0", CultureInfo.CurrentCulture);
            string dropped = stats.Dropped.ToString("N0", CultureInfo.CurrentCulture);

            if (stats.Healthy)
                return Lexicon.Get("settings.diagnostics.alarm_journal.recording",
                    ("rate", rate), ("written", written), ("dropped", dropped));

            return Lexicon.Get("settings.diagnostics.alarm_journal.recording_failing",
                ("rate", rate), ("written", written), ("dropped", dropped),
                ("lost", stats.RecordsLostToWriteFailure.ToString("N0", CultureInfo.CurrentCulture)),
                ("waiting", stats.RecordsRetainedUnwritten.ToString("N0", CultureInfo.CurrentCulture)),
                ("detail", string.IsNullOrWhiteSpace(stats.HealthDetail) ? "" : stats.HealthDetail.Trim()));
        }
    }
}
