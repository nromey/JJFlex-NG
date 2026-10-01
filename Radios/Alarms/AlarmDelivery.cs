#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;
using Radios.Speech;

namespace Radios.Alarms
{
    /// <summary>The speech side of delivery, as the delivery sees it. Production is <see cref="ScreenReaderAlarmSpeaker"/>.</summary>
    public interface IAlarmSpeaker
    {
        /// <summary>The warning: Critical level, Urgent intent, under the alarm-aware priority contract.</summary>
        /// <param name="notDelivered">
        /// Called, at most once, if the speech layer gives this warning up
        /// without the reader ever taking it: refused by the backend, let go
        /// from the waiting set, or its condition could not be re-read. The
        /// alarm then says it again on its own next fresh sample. Never called
        /// for a warning the operator silenced — that is theirs to keep quiet
        /// until the condition worsens (#617).
        /// </param>
        void SpeakWarning(string text, string subject, Func<string?> refresh, Action? notDelivered = null);

        /// <summary>A state update: queued, never interrupting, at the given level.</summary>
        void SpeakStatus(string text, VerbosityLevel level, string subject);
    }

    /// <summary>Production speech: <see cref="ScreenReaderOutput.SpeakAlarm"/> and a queued <see cref="ScreenReaderOutput.Speak"/>.</summary>
    public sealed class ScreenReaderAlarmSpeaker : IAlarmSpeaker
    {
        public void SpeakWarning(string text, string subject, Func<string?> refresh, Action? notDelivered = null) =>
            ScreenReaderOutput.SpeakAlarm(text, subject, refresh, notDelivered);

        public void SpeakStatus(string text, VerbosityLevel level, string subject) =>
            ScreenReaderOutput.Speak(text, SpeechIntent.Queue, level, subject: subject);
    }

    /// <summary>What delivery did with one event, for the journal and the trace.</summary>
    public sealed record AlarmDeliveryReport(
        AlarmEvent Event,
        string Sentence,
        bool SoundRequested,
        bool SpeechRequested,
        int ToneLeadMs,
        bool IsPreview);

    /// <summary>
    /// Sound then speech, on the dispatch worker, for every warning the
    /// service concludes (design section 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tone first, the sentence after it.</b> The warning earcon is the
    /// app's existing <c>EarconPlayer.WarningAlarmTone</c>, 750 ms with its own
    /// receive-audio duck; it is injected as an action because this project
    /// does not reference the WPF one. Speech is handed over on a timer
    /// continuation 750 ms later, never by sleeping; a newer warning for the
    /// same alarm inside that window replaces the waiting one, and a resolved
    /// episode cancels it — a delayed stop instruction after clearing is
    /// exactly the stale speech this exists to avoid.
    /// </para>
    /// <para>
    /// <b>What speech gets: Critical AND Urgent</b>, through
    /// <see cref="ScreenReaderOutput.SpeakAlarm"/>. Critical because the level
    /// is checked before the intent and a Terse Urgent is still dropped;
    /// Urgent because it must get past stale speech; tagged with the alarm's
    /// subject so a cut announcement wins and two alarms take turns.
    /// </para>
    /// <para>
    /// <b>The refresh.</b> Every warning carries a way to re-read itself: the
    /// service's snapshot for that alarm. Before its FIRST hand-over on either
    /// path — after the tone, or at once with the sound off — and when a
    /// deferred warning's turn comes, or the one bounded retry fires, the
    /// warning is asked, and it answers with the CURRENT value or with nothing
    /// — never the value it was queued with. The event reached this class
    /// through an asynchronous dispatch queue, and "current when concluded"
    /// is not "current when spoken".
    /// </para>
    /// <para>
    /// <b>Cleared is a state update</b>: queued, Terse, no tone, and no
    /// permission to transmit. Data lost while transmitting is a warning in
    /// its own right; in receive it is a queued Critical line.
    /// </para>
    /// <para>
    /// <b>THE SEAM WITH THE FACT STORE, mapped here on purpose (ruled by Noel
    /// 2026-09-30, #566 and #611).</b> This class is the alarm's OWN delivery
    /// route: the alarm service concludes a warning, this hands it to the
    /// tone and to <see cref="ScreenReaderOutput.SpeakAlarm"/>, and the
    /// arbiter takes the safety speaking turn for it. Integration also carries
    /// the fact store, which already models the same obligation —
    /// <c>Radios.Facts.DeliveryPriority.OperatorAlarm</c> is a slot class, and
    /// <c>Radios.Facts.FactStoreCapacity</c> reserves one slot per enabled
    /// alarm — and NOTHING here goes through it. That is the ruling, not an
    /// oversight: <i>"we're not using the fact store yet, that'll come soon,
    /// but by all means, we need the temp etc. to be useful."</i> So two owners
    /// of "what alarm information is owed to the operator" ship side by side,
    /// knowingly, and the later move has one place to start from:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="Warn"/> is where a concluded warning becomes a
    /// delivery attempt. Under the fact store it becomes a producer publishing
    /// onto the alarm's reserved slot, and the slot's lifecycle, not this
    /// class's timer, decides what is still owed.</description></item>
    /// <item><description><see cref="Refresh"/> is the condition check before each
    /// attempt — the "is the current rendering still justified" question the
    /// fact-store design asks per attempt. It moves with the producer.</description></item>
    /// <item><description>The record of an undelivered warning is the alarm
    /// service's snapshot and the alarms list, read through
    /// <see cref="AlarmPhrasing.ActiveSummary"/>; under the store it is the
    /// disconnected-reachable list the #617 design specifies.</description></item>
    /// </list>
    /// </remarks>
    public sealed class AlarmDelivery : IDisposable
    {
        /// <summary>The warning tone's length, which is also how long speech waits behind it.</summary>
        public const int ToneLeadMs = 750;

        private readonly AlarmService _service;
        private readonly IAlarmSpeaker _speaker;
        private readonly Action? _sound;
        private readonly ISpeechClock _clock;
        private readonly Func<bool> _warningsSoundEnabled;
        private readonly Func<bool> _speechSuppressed;
        private readonly Func<bool> _speechAvailable;

        /// <summary>
        /// The speech layer's quiet cohort (#182, #611). Read when the tone
        /// continuation is ARMED and compared when it fires.
        ///
        /// <para><b>Why the cue stage needs this at all.</b> The warning tone
        /// runs for 750 ms and the sentence is handed to speech only after it,
        /// so for that whole window the warning exists nowhere the arbiter can
        /// see. A silence policy implemented inside the arbiter's ledger
        /// therefore cannot revoke this pending continuation, and the operator
        /// who presses Ctrl during the tone hears the warning start anyway —
        /// which reads as the shut-up key not working. Comparing the cohort
        /// across the seam is the barrier one stage earlier: an event captured
        /// before the instruction but processed after it inherits the
        /// pause.</para>
        ///
        /// <para>Nothing is erased by it. The alarm's condition, its snapshot
        /// and its place in the alarms list are untouched; only this
        /// particular automatic presentation is stood down.</para>
        /// </summary>
        private readonly Func<long> _quietGeneration;

        private readonly object _gate = new object();

        /// <summary>
        /// A warning waiting out the tone. The timer is armed ONCE, by the
        /// first warning; a fresher warning for the same alarm inside the lead
        /// replaces the words and keeps the deadline, because restarting the
        /// timer on every worsening postponed speech by 750 ms each time and
        /// a steadily worsening meter could postpone it indefinitely (Astra's
        /// Track I review, additional observations).
        /// </summary>
        private sealed class PendingWarning
        {
            public ISpeechTimer Timer = null!;
            public int Generation;
            public long Cohort;
            public string Sentence = "";
            public string Subject = "";
            public Func<string?> Refresh = () => null;
            public Action? NotDelivered;
        }

        private readonly Dictionary<string, PendingWarning> _continuations =
            new Dictionary<string, PendingWarning>(StringComparer.Ordinal);
        private int _generation;
        private bool _disposed;

        /// <param name="sound">Start the warning tone. Null when no sound is wired (tests, the voice lab).</param>
        /// <param name="warningsSoundEnabled">Whether the Warnings earcon category is on, for the preview's honesty line.</param>
        internal AlarmDelivery(AlarmService service, IAlarmSpeaker speaker, Action? sound, ISpeechClock clock,
            Func<bool>? warningsSoundEnabled = null, Func<bool>? speechSuppressed = null, Func<bool>? speechAvailable = null,
            Func<long>? quietGeneration = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _speaker = speaker ?? throw new ArgumentNullException(nameof(speaker));
            _sound = sound;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _warningsSoundEnabled = warningsSoundEnabled ?? (() => true);
            _speechSuppressed = speechSuppressed ?? (() => false);
            _speechAvailable = speechAvailable ?? (() => true);
            _quietGeneration = quietGeneration ?? (() => 0L);
            _service.EventDispatched += OnEvent;
            _service.DeliveryUnavailable += OnDeliveryUnavailable;
        }

        /// <summary>Production: the system clock, and the live speech layer's quiet cohort.</summary>
        public static AlarmDelivery Attach(AlarmService service, IAlarmSpeaker speaker, Action? sound,
            Func<bool>? warningsSoundEnabled = null)
            => new AlarmDelivery(service, speaker, sound, new SystemSpeechClock(), warningsSoundEnabled,
                () => ScreenReaderOutput.SuppressSpeech, () => ScreenReaderOutput.IsAvailable,
                () => ScreenReaderOutput.SafetyQuietGeneration);

        /// <summary>Every delivery, after the decision, for the journal.</summary>
        public event Action<AlarmDeliveryReport>? Reported;

        private void OnEvent(AlarmEvent e)
        {
            if (_disposed) return;
            try
            {
                if (e.IsWarning)
                {
                    Warn(e);
                    return;
                }

                switch (e.Kind)
                {
                    case AlarmEventKind.DataStale when e.Definition.Enabled:
                        if (e.Transmitting) Warn(e, AlarmPhrasing.DataLost(e));
                        else Status(e, AlarmPhrasing.DataLost(e), VerbosityLevel.Critical);
                        return;

                    case AlarmEventKind.Cleared:
                        Cancel(e.Definition.Id);
                        Status(e, AlarmPhrasing.Cleared(e), VerbosityLevel.Terse);
                        return;

                    case AlarmEventKind.ConfigurationChanged:
                    case AlarmEventKind.Disabled:
                    case AlarmEventKind.DataMissing:
                        Cancel(e.Definition.Id);
                        return;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AlarmDelivery: " + e.Kind + " for '" + e.Definition.Name + "' failed — " + ex.Message,
                    TraceLevel.Error);
            }
        }

        private void Warn(AlarmEvent e, string? sentenceOverride = null)
        {
            bool preview = e.Detail == "preview";
            string real = sentenceOverride ?? AlarmPhrasing.Warning(e);
            string sentence = preview ? PreviewSentence(e, real) : real;
            string subject = SpeechSubject.OperatorAlarm(e.Definition.Id);
            string alarmId = e.Definition.Id;

            bool soundOn = _sound != null && _warningsSoundEnabled();
            int lead = soundOn ? ToneLeadMs : 0;
            if (_sound != null)
            {
                // The earcon gates itself on its category; calling it when the
                // category is off is a no-op, and calling it is what proves the
                // path is wired.
                try { _sound(); } catch (Exception ex)
                { Tracing.TraceLine("AlarmDelivery: the warning tone threw — " + ex.Message, TraceLevel.Warning); }
            }

            Func<string?> refresh = preview
                ? () => sentence
                : () => Refresh(e, sentenceOverride != null);
            // The speech layer tells the alarm when it gave the warning up
            // without the reader taking it, and the alarm's next fresh sample
            // says it again. A preview is a test and is not re-raised.
            Action? notDelivered = preview ? null : () => _service.WarningNotDelivered(alarmId);

            bool speechRequested = true;
            lock (_gate)
            {
                if (_disposed) return;
                if (lead == 0)
                {
                    CancelLocked(alarmId);

                    // **Re-read before the first hand-over, exactly as the tone
                    // continuation does (Astra's Track IJK review, blocker 3).**
                    // This branch spoke the EVENT's own sentence, and the event
                    // came through an asynchronous dispatch queue: a worker
                    // delayed past the readings that cleared the episode, or
                    // past the operator disabling or editing the alarm, spoke
                    // the old measurement as current. The refresh judges the
                    // live episode, the definition's enabled state and
                    // revision (an edit closes the episode, so its id no
                    // longer matches) and the acknowledgement against the
                    // event's revision. A preview's refresh returns itself.
                    string? current = refresh();
                    if (current == null)
                    {
                        speechRequested = false;
                        Tracing.TraceLine("AlarmDelivery: warning withdrawn before its first hand-over, no longer current when "
                            + "the dispatch worker reached it [" + alarmId + "]: '" + sentence + "'", TraceLevel.Info);
                    }
                    else
                    {
                        _speaker.SpeakWarning(current, subject, refresh, notDelivered);
                    }
                }
                else if (_continuations.TryGetValue(alarmId, out PendingWarning? waiting))
                {
                    // A fresher warning for the same alarm, inside the lead:
                    // the WORDS are replaced and the DEADLINE is kept. The
                    // cohort is re-read, because this is a new instruction —
                    // a worsening after the operator's Ctrl is a new fact and
                    // speaks (#617, ruled 2026-09-24), while a Ctrl after this
                    // instruction still stands it down.
                    waiting.Sentence = sentence;
                    waiting.Subject = subject;
                    waiting.Refresh = refresh;
                    waiting.NotDelivered = notDelivered;
                    waiting.Cohort = SafeQuietGeneration();
                }
                else
                {
                    // The cohort is captured HERE, at the instruction that
                    // started the tone, not read when the timer fires — see
                    // _quietGeneration. Reading it at the far end would ask
                    // "is the operator quiet now", which is a different and
                    // weaker question.
                    int generation = ++_generation;
                    var pending = new PendingWarning
                    {
                        Generation = generation,
                        Cohort = SafeQuietGeneration(),
                        Sentence = sentence,
                        Subject = subject,
                        Refresh = refresh,
                        NotDelivered = notDelivered,
                    };
                    pending.Timer = _clock.StartTimer(lead, () => Continue(alarmId, generation));
                    _continuations[alarmId] = pending;
                }
            }

            Report(new AlarmDeliveryReport(e, sentence, soundOn, speechRequested, lead, preview));
        }

        private void Continue(string alarmId, int generation)
        {
            PendingWarning pending;
            lock (_gate)
            {
                if (_disposed) return;
                if (!_continuations.TryGetValue(alarmId, out PendingWarning? found) || found.Generation != generation) return;
                pending = found;
                _continuations.Remove(alarmId);
                try { pending.Timer.Dispose(); } catch { }
            }

            // The operator asked for quiet while the tone was still playing.
            // Stand the automatic presentation down — the condition, its
            // snapshot and its place in the alarms list are untouched, nothing
            // is acknowledged, and an explicit read still says it (#182).
            long now = SafeQuietGeneration();
            if (now != pending.Cohort)
            {
                Tracing.TraceLine("AlarmDelivery: the operator silenced speech during the warning tone, so the "
                    + "sentence behind it is not spoken automatically; the alarm is unchanged and still in the "
                    + "alarms list [" + alarmId + "]", TraceLevel.Info);
                return;
            }

            // Re-read once more at the moment of speaking: the episode may
            // have cleared, or been acknowledged, during the tone.
            string? current = pending.Refresh();
            if (current == null)
            {
                Tracing.TraceLine("AlarmDelivery: warning withdrawn during the tone, no longer current [" + alarmId + "]",
                    TraceLevel.Info);
                return;
            }
            _speaker.SpeakWarning(current, pending.Subject, pending.Refresh, pending.NotDelivered);
        }

        /// <summary>
        /// The speech layer's quiet cohort, or the last one successfully read.
        ///
        /// <para>A read that fails must not INVENT a difference: the two reads
        /// either side of the tone are compared, so a thrown exception at one
        /// end would silence a warning for a reason that has nothing to do
        /// with the operator. Falling back to the last known value means only
        /// a real advance can separate them, and the failure is traced.</para>
        /// </summary>
        private long SafeQuietGeneration()
        {
            try
            {
                long g = _quietGeneration();
                _lastKnownCohort = g;
                return g;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AlarmDelivery: could not read the speech layer's quiet cohort — " + ex.Message,
                    TraceLevel.Warning);
                return _lastKnownCohort;
            }
        }

        private long _lastKnownCohort;

        /// <summary>
        /// The current sentence for the warning <paramref name="e"/> raised, or
        /// null when there is nothing left to say.
        /// </summary>
        /// <remarks>
        /// <para><b>Acknowledgement is judged against the EVENT, not in the
        /// abstract (Astra's Track I review, finding 1).</b> This used to
        /// return null for any acknowledged or snoozed alarm, so a worsening
        /// — the one warning the monitor emits precisely to bypass
        /// acknowledgement — played its tone and then dropped its sentence
        /// here. Now a warning that overrides acknowledgement is spoken when
        /// the acknowledgement PREDATES it (same notification revision), and
        /// withdrawn when the operator answered AFTER it was raised (the
        /// revision moved): those are different acts, and only the second is
        /// an answer to this warning.</para>
        /// <para>And the episode must be the one the event belongs to. A newer
        /// episode's own Fired event speaks for it; an older event's refresh
        /// returning the new value would say it twice.</para>
        /// </remarks>
        private string? Refresh(AlarmEvent e, bool dataLost)
        {
            AlarmSnapshot? s = _service.SnapshotOf(e.Definition.Id);
            if (s == null || !s.Definition.Enabled) return null;
            if (dataLost)
            {
                // Still lost? Then the sentence stands, with the age it has now.
                if (s.Data is AlarmDataState.Stale or AlarmDataState.Missing)
                {
                    var lost = new AlarmEvent
                    {
                        Kind = AlarmEventKind.DataStale, Definition = s.Definition, AtMs = 0,
                        Observation = s.LastFresh, AgeSeconds = s.LastFreshAgeSeconds, Transmitting = s.Transmitting,
                    };
                    return AlarmPhrasing.DataLost(lost);
                }
                return null;
            }
            if (s.Condition != AlarmConditionState.Active) return null;
            if (s.Data is not (AlarmDataState.Fresh or AlarmDataState.Recovering)) return null;
            if (!string.Equals(s.EpisodeId, e.EpisodeId, StringComparison.Ordinal)) return null;
            if (s.Notification is AlarmNotificationState.Acknowledged or AlarmNotificationState.Snoozed)
            {
                if (!e.OverridesAcknowledgement) return null;
                if (s.NotificationRevision != e.NotificationRevision) return null;   // answered AFTER this warning
            }
            return AlarmPhrasing.Warning(AlarmPhrasing.WarningFromSnapshot(s) with { Kind = e.Kind, ReminderReason = e.ReminderReason });
        }

        private string PreviewSentence(AlarmEvent e, string real)
        {
            string s = AlarmPhrasing.Preview(e, real);
            if (_sound == null || !_warningsSoundEnabled()) s += " " + Lexicon.Get("alarms.preview.sound_off");
            if (_speechSuppressed()) s += " " + Lexicon.Get("alarms.preview.speech_suppressed");
            else if (!_speechAvailable()) s += " " + Lexicon.Get("alarms.preview.no_reader");
            return s;
        }

        private void Status(AlarmEvent e, string sentence, VerbosityLevel level)
        {
            _speaker.SpeakStatus(sentence, level, SpeechSubject.OperatorAlarmStatus);
            Report(new AlarmDeliveryReport(e, sentence, false, true, 0, false));
        }

        /// <summary>
        /// The dispatch queue overflowed. Arrives on a thread-pool thread —
        /// AlarmDispatchQueue hands the signal off rather than raising it on
        /// the meter thread under the service lock — so speaking here takes
        /// the arbiter's lock with nothing else held (finding 4).
        /// </summary>
        private void OnDeliveryUnavailable(long dropped)
        {
            try { _speaker.SpeakStatus(AlarmPhrasing.DeliveryUnavailable(), VerbosityLevel.Critical, SpeechSubject.OperatorAlarmStatus); }
            catch (Exception ex)
            {
                Tracing.TraceLine("AlarmDelivery: could not say that delivery is unavailable — " + ex.Message, TraceLevel.Warning);
            }
        }

        private void Cancel(string alarmId)
        {
            lock (_gate) CancelLocked(alarmId);
        }

        private void CancelLocked(string alarmId)
        {
            if (_continuations.TryGetValue(alarmId, out var pending))
            {
                try { pending.Timer.Dispose(); } catch { }
                _continuations.Remove(alarmId);
            }
        }

        private void Report(AlarmDeliveryReport report)
        {
            Tracing.TraceLine("AlarmDelivery: " + (report.IsPreview ? "PREVIEW " : "") + report.Event.Kind + " for '"
                + report.Event.Definition.Name + "': sound=" + report.SoundRequested + " speech=" + report.SpeechRequested
                + " lead=" + report.ToneLeadMs + " ms — '" + report.Sentence + "'", TraceLevel.Info);
            try { Reported?.Invoke(report); }
            catch (Exception ex) { Tracing.TraceLine("AlarmDelivery: a report listener threw — " + ex.Message, TraceLevel.Warning); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var c in _continuations.Values) { try { c.Timer.Dispose(); } catch { } }
                _continuations.Clear();
            }
            _service.EventDispatched -= OnEvent;
            _service.DeliveryUnavailable -= OnDeliveryUnavailable;
        }
    }
}
