#nullable enable
using System.Globalization;

namespace Radios.Alarms
{
    /// <summary>
    /// The alarm journal's readout for Settings, Diagnostics: a snapshot of
    /// what the journal reports when the tab reads it, in words. It is not
    /// proof of a live stream, and where a service or journal is absent it
    /// says so without naming a cause.
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
    /// readings. But it does carry the in-memory ring of retained meter
    /// readings (bounded by time, <see cref="AlarmJournal.RingSeconds"/>, and
    /// by size, so not an exact window), with the seconds between its oldest
    /// and newest. Readings in the ring, less one, over that span is the
    /// recorded rate, from one reading. The ring holds measured observations
    /// only, while the written count also includes events. With fewer than two
    /// readings, or under a second of span, there is no honest rate, and the
    /// line says so rather than inventing one.
    /// </para>
    /// <para>
    /// <b>The states, in order.</b> No alarm service (absent after a
    /// disconnect or a failed attachment alike, so no cause is named); a
    /// service with no journal stats (no cause named either); no recorded
    /// meters resolved on this connection (none chosen, none resolved, or an
    /// empty inventory, which does not prove no alarm is enabled); and
    /// otherwise the journal's counts and rate, with the write losses and the
    /// last write error when it reports itself unhealthy. Counts are
    /// cumulative for the journal instance.
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

        /// <summary>The readout for the tab.</summary>
        /// <param name="radioAttached">The alarm service is present (the host's Service is not null).</param>
        /// <param name="recordedMeters">Recorded meters resolved in the feed inventory on this connection.</param>
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
