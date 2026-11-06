using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// What a door does when it keys a transmit slice that is not the
    /// selected one (#863). Ruled by Noel 2026-11-03: three choices, speak and
    /// tone the default. Stored as an int in the operator's audio settings.
    /// </summary>
    public enum TransmitElsewhereNotice
    {
        /// <summary>The sentence, once per split, and the tri-tones at every key. The default.</summary>
        SpeakAndTone = 0,

        /// <summary>The tri-tones only.</summary>
        ToneOnly = 1,

        /// <summary>Neither. Check your audio still speaks it (see <see cref="TransmitSliceNotice"/>).</summary>
        Off = 2,
    }

    /// <summary>
    /// Where a door is about to transmit, when that is not the selected slice
    /// (#863). Built once, used by every door that keys: Check your audio
    /// (Start and Ctrl+Enter, every method that keys), the box sends, voice
    /// message sends with the box off, and push to talk inside a dialog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel 2026-11-02 17:36: "Say it, then go".</b> On 2026-11-02 a
    /// 1 W check keyed slice A on 14.100, the international beacon frequency,
    /// for about six seconds while slice B had been the slice listened to, and
    /// nothing said where. And for the other doors, <i>"Yes, all of them"</i>.
    /// </para>
    /// <para>
    /// <b>Ruled 2026-11-03.</b> Push to talk inside a dialog keeps "go, then
    /// say" (17:55: a held key under JAWS arrives as pairs and would never key
    /// if it waited). Voice message sends with the box off say it too (18:03).
    /// And (18:04) <b>once per split, not per key</b>: the sentence is said the
    /// first time a door keys after the transmit slice stops being the
    /// selected slice, and again only when the split changes (a different
    /// transmit slice, a different selected slice, or the transmit slice's
    /// frequency moved since it was last said); every other key while that
    /// split stands gets the tri-tones only (<see cref="PlaysTones"/>). The
    /// wording follows verbosity (<see cref="Wording"/>), and a setting has
    /// three choices (<see cref="TransmitElsewhereNotice"/>). Check your audio
    /// says it whatever the setting and every time, because it keys the radio
    /// itself, after a countdown, from a window that does not show the slice
    /// (the <c>mandatory</c> door). Not built, on purpose: any announcement
    /// when the operator merely changes slices.
    /// </para>
    /// <para>
    /// <b>What was announced is what keys</b> (Codex's check, B2). A
    /// <see cref="Notice"/> carries the place it decided about; at the moment
    /// of keying the door asks <see cref="StillTheAnnouncedPlace"/>, which
    /// reads the slices again. A place that changed and now owes a sentence is
    /// refused, never keyed unannounced.
    /// </para>
    /// <para>
    /// <b>Heard means heard whole</b> (B3): the sentence goes out on its own
    /// subject through <see cref="Speech.CountdownSpeech.SayWhole"/>, where a
    /// cut part-way is a failure, not a hearing.
    /// </para>
    /// </remarks>
    public static class TransmitSliceNotice
    {
        /// <summary>The operator's choice, pushed here from the audio settings.</summary>
        public static TransmitElsewhereNotice Setting { get; set; } = TransmitElsewhereNotice.SpeakAndTone;

        /// <summary>The longest a door waits for the sentence: the countdown's own ceiling.</summary>
        public const int CeilingMs = CountdownRun.SpeechCeilingMs;

        /// <summary>How often a door asks again when the reader gives no news.</summary>
        public const int PollMs = CountdownRun.SpeechPollMs;

        // ── The words ──────────────────────────────────────────────────────

        /// <summary>
        /// The sentence for a split, or null when the transmit slice is the
        /// selected one or there is none. Pure. By verbosity: Terse and Off
        /// "B 14.100"; Chatty and Diagnostic "Transmitting on slice B, 14.100",
        /// every time it is said. RULED by Noel 2026-11-04 06:50 ("call a
        /// spade a spade"): the longer first-of-session sentence and the
        /// "Transmit slice B" wording are gone, so there is no first-time
        /// branch any more. When it is said is unchanged: once per split.
        /// </summary>
        public static string Wording(SliceRoles roles, VerbosityLevel level, Func<ulong, string> format)
        {
            if (!roles.TransmitsElsewhere) return null;
            if (format == null) throw new ArgumentNullException(nameof(format));
            var slice = ("slice", (object)roles.TransmitLetter);
            var freq = ("freq", (object)format(roles.TransmitHz));
            if (level <= VerbosityLevel.Terse) return Lexicon.Get("audio.ptt.transmit_elsewhere_terse", slice, freq);
            return Lexicon.Get("audio.ptt.transmit_elsewhere", slice, freq);
        }

        // ── The rules ──────────────────────────────────────────────────────

        /// <summary>
        /// True when <paramref name="now"/> is a split nobody has been told
        /// about: elsewhere, and not the place last spoken. Pure.
        /// </summary>
        public static bool SplitIsNew(SliceRoles now, SliceRoles? lastSpoken) =>
            now.TransmitsElsewhere && (lastSpoken == null || !now.SamePlaceAs(lastSpoken.Value));

        /// <summary>
        /// Whether a door speaks the sentence. A mandatory door (Check your
        /// audio) speaks whenever it keys elsewhere, whatever the setting; any
        /// other door only under speak-and-tone, and only for a new split. Pure.
        /// </summary>
        public static bool Speaks(TransmitElsewhereNotice setting, bool mandatory, SliceRoles now, SliceRoles? lastSpoken)
        {
            if (!now.TransmitsElsewhere) return false;
            if (mandatory) return true;
            return setting == TransmitElsewhereNotice.SpeakAndTone && SplitIsNew(now, lastSpoken);
        }

        /// <summary>
        /// Whether a key here gets the tri-tones: elsewhere, the setting not
        /// off, and push to talk's chirps on. Pure. RULED by Noel 2026-11-04
        /// 06:51: unchecking "Play chirp tones on transmit and receive"
        /// silences the tri-tones too, as it does the two-tone pair; the
        /// <paramref name="chirps"/> passed is that same PTT setting, never a
        /// new one. The spoken notice is not governed by it.
        /// </summary>
        public static bool PlaysTones(TransmitElsewhereNotice setting, bool chirps, SliceRoles roles) =>
            chirps && roles.TransmitsElsewhere && setting != TransmitElsewhereNotice.Off;

        /// <summary>The tri-tones for this rig now, under the operator's setting and chirp switch.</summary>
        public static bool PlaysTones(FlexBase rig, bool chirps)
        {
            if (rig == null || !chirps) return false;
            try { return PlaysTones(Setting, chirps, SliceRoles.Of(rig)); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice: could not read the slices for the tones: " + ex.Message, TraceLevel.Warning);
                return false;
            }
        }

        // ── The memory, per connection ─────────────────────────────────────

        private sealed class Memory
        {
            public SliceRoles? Spoken;
            /// <summary>Bumped by every key, so a sentence finishing late commits only if nothing keyed since.</summary>
            public long Generation;
        }

        /// <summary>One per rig, reset at every new command connection (<see cref="ForgetFor"/>): a new connection starts with nothing said.</summary>
        private static readonly ConditionalWeakTable<FlexBase, Memory> Memories = new();
        private static readonly object MemoryLock = new();

        private static Memory MemoryOf(FlexBase rig) => Memories.GetValue(rig, _ => new Memory());

        // ── A door's notice ────────────────────────────────────────────────

        /// <summary>
        /// What one door decided before it keys: the place it read, whether it
        /// spoke, and the words to wait on.
        /// </summary>
        public sealed class Notice
        {
            internal Notice(FlexBase rig, string door, SliceRoles announced, bool mandatory, bool spoke,
                ICountdownWords words)
            {
                Rig = rig;
                Door = door ?? "";
                Announced = announced;
                Mandatory = mandatory;
                Spoke = spoke;
                Words = words;
            }

            public FlexBase Rig { get; }
            public string Door { get; }
            /// <summary>The place this notice read, spoken or not.</summary>
            public SliceRoles Announced { get; }
            /// <summary>Check your audio: speaks whatever the setting.</summary>
            public bool Mandatory { get; }
            /// <summary>True when the sentence was said and must be heard before the key.</summary>
            public bool Spoke { get; }
            /// <summary>The words to wait on, or null when nothing was said.</summary>
            public ICountdownWords Words { get; }
        }

        /// <summary>
        /// Read the slices for a door about to key, decide, and say the
        /// sentence when it is owed. Null only with no rig. Never throws.
        /// </summary>
        /// <param name="rig">The radio about to be keyed.</param>
        /// <param name="door">Which door, for the trace.</param>
        /// <param name="mandatory">Check your audio: speaks whatever the setting, every time.</param>
        public static Notice Prepare(FlexBase rig, string door, bool mandatory)
        {
            if (rig == null) return null;
            SliceRoles roles;
            try { roles = SliceRoles.Of(rig); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice (" + door + "): could not read the slices: " + ex.Message, TraceLevel.Warning);
                return new Notice(rig, door, default, mandatory, false, null);
            }
            Memory memory = MemoryOf(rig);
            SliceRoles? last;
            lock (MemoryLock) last = memory.Spoken;
            if (!Speaks(Setting, mandatory, roles, last))
                return new Notice(rig, door, roles, mandatory, false, null);

            VerbosityLevel level = ScreenReaderOutput.CurrentVerbosity;
            string sentence;
            try { sentence = Wording(roles, level, FormatterFor(rig)); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice (" + door + "): could not word the sentence: " + ex.Message, TraceLevel.Warning);
                return new Notice(rig, door, roles, mandatory, true, CountdownWords.Undelivered);
            }
            Tracing.TraceLine("TransmitSliceNotice (" + door + "): the transmit slice is not the selected one; saying \""
                + sentence + "\" before keying", TraceLevel.Info);
            ICountdownWords words;
            // Ahead of our own ordinary backlog (rulings round two, the bench's
            // Fail 2): stale arrival and salvaged sentences are retired, so the
            // notice is not queued behind them until its gate gives up.
            // Due from before it is asked for until its wait ends (rulings round
            // three): Home's arrival speech holds meanwhile.
            MarkDue();
            try { words = ScreenReaderOutput.CountdownSpeech.SayWhole(sentence, Speech.SpeechSubject.TransmitSliceNotice, ahead: true); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice (" + door + "): could not be said: " + ex.Message, TraceLevel.Warning);
                words = CountdownWords.Undelivered;
            }
            return new Notice(rig, door, roles, mandatory, true, words);
        }

        /// <summary>
        /// Wait until the reader has finished the notice's sentence, whole.
        /// True: heard (or nothing was said), go. False: never delivered, cut
        /// part-way, past the ceiling, or <paramref name="stillWanted"/> went
        /// false; do not key.
        /// </summary>
        public static Task<bool> HeardAsync(Notice notice, Func<bool> stillWanted,
            Func<int, Task> delay = null, int ceilingMs = CeilingMs) =>
            HeardAsync(notice?.Words, stillWanted, delay, ceilingMs);

        /// <summary>
        /// Wait until the reader has finished <paramref name="words"/>. True:
        /// heard, go. False: it was never delivered, it ran past the ceiling,
        /// or <paramref name="stillWanted"/> went false; do not key. Null words
        /// (nothing to say) are true at once.
        /// </summary>
        public static async Task<bool> HeardAsync(ICountdownWords words, Func<bool> stillWanted,
            Func<int, Task> delay = null, int ceilingMs = CeilingMs, int queuedCeilingMs = QueuedCeilingMs)
        {
            if (words == null) return true;
            try { return await HeardCoreAsync(words, stillWanted, delay, ceilingMs, queuedCeilingMs).ConfigureAwait(true); }
            finally { ClearDue(); }
        }

        // ── A notice is due (rulings round three, the second 8600 bench) ──
        //
        // On the bench, as the long-recording question closed, Home's arrival
        // prefix ("JJ Flexible Home, frequency, ...", an interrupt) cut the
        // notice at its first word and the return status ("Listening on ...
        // slice B") was said ahead of it: every notice-bearing send keyed 11 s
        // after Send. Both are where-you-are speech about Home, which the notice
        // supersedes for the moment it is due, so they hold (are not said) while
        // it is: from just before it is asked for until its wait ends, bounded so
        // a wait that never ends cannot silence Home for good.

        private static long _dueUntil;

        /// <summary>A send's transmit-slice notice is about to be said, or is being waited for.</summary>
        public static bool NoticeDue => Environment.TickCount64 < System.Threading.Interlocked.Read(ref _dueUntil);

        internal static void MarkDue() =>
            System.Threading.Interlocked.Exchange(ref _dueUntil, Environment.TickCount64 + CeilingMs + QueuedCeilingMs);

        internal static void ClearDue() => System.Threading.Interlocked.Exchange(ref _dueUntil, 0);

        private static async Task<bool> HeardCoreAsync(ICountdownWords words, Func<bool> stillWanted,
            Func<int, Task> delay, int ceilingMs, int queuedCeilingMs)
        {
            delay ??= Task.Delay;
            int waited = 0, queued = 0;
            while (true)
            {
                if (stillWanted != null && !stillWanted()) return false;
                if (words.Failed) return false;
                Task news = words.NextNews;
                if (words.Finished) return true;
                // The ceiling bounds the reader's own time with the sentence. Time
                // spent not yet begun, behind our OWN speech, is not the reader
                // failing, and a send is not refused for it (rulings round two,
                // the bench's Fail 2); it has its own bound, ten seconds (J27).
                bool begun = words.Started;
                if (begun ? waited >= ceilingMs : queued >= queuedCeilingMs) return false;
                await Task.WhenAny(news, delay(PollMs)).ConfigureAwait(true);
                if (begun) waited += PollMs; else queued += PollMs;
            }
        }

        /// <summary>
        /// How long a notice may wait, not yet begun, behind our own speech
        /// before the door gives up (rulings round two). Escape, a stop or a
        /// closed window ends the wait at once through stillWanted. Ten seconds,
        /// ruled by Noel 2026-11-06 16:50 (J27): "cap it at 10, 30 seems kind of
        /// excessive."
        /// </summary>
        public const int QueuedCeilingMs = 10_000;

        /// <summary>
        /// At the moment of keying: true when the door may key, false when it
        /// must refuse (Codex's check, B2). Reads the slices again. The same
        /// place keys; a place where the transmit slice is now the selected one
        /// keys (nothing is owed); a changed place that would need a sentence
        /// is refused, because it was never announced. A selected slice that a
        /// check made for itself (Loopback's and the off-air take's listening
        /// slice) is not the operator's move and does not count as a change.
        /// </summary>
        public static bool StillTheAnnouncedPlace(Notice notice) => StillTheAnnouncedPlace(notice, out SliceRoles _);

        public static bool StillTheAnnouncedPlace(Notice notice, out SliceRoles now)
        {
            now = default;
            if (notice?.Rig == null) return true;
            try { now = SliceRoles.Of(notice.Rig); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice (" + notice.Door + "): could not read the slices at the key: " + ex.Message,
                    TraceLevel.Warning);
                return false;
            }
            bool ok = StillTheAnnouncedPlace(notice.Announced, now, notice.Mandatory, Setting, LastSpoken(notice.Rig));
            if (!ok)
                Tracing.TraceLine("TransmitSliceNotice (" + notice.Door + "): the place changed while it waited (transmit "
                    + notice.Announced.TransmitLetter + " to " + now.TransmitLetter + ", selected " + notice.Announced.SelectedLetter
                    + " to " + now.SelectedLetter + "); refusing rather than keying a place nobody was told about",
                    TraceLevel.Warning);
            return ok;
        }

        /// <summary>
        /// The sentence a door refuses with when the place changed while it
        /// waited (ruled by Noel 2026-11-04 06:59). The transmit slice itself
        /// changed: "The transmit slice changed, so nothing was transmitted."
        /// (his wording). Only its frequency moved: "The transmit frequency
        /// changed, so nothing was transmitted." (DRAFT, the orchestrator's
        /// extension). Any other change (the selected slice moved, so a new
        /// split nobody was told about): "The selected slice changed, so
        /// nothing was transmitted." (ruled by Noel 2026-11-04 22:20). A
        /// sentence that could not be said keeps the not-announced sentence.
        /// Pure.
        /// </summary>
        public static string RefusalForChangedPlace(SliceRoles announced, SliceRoles now)
        {
            if (now.HasTransmit != announced.HasTransmit
                || !string.Equals(now.TransmitLetter, announced.TransmitLetter, StringComparison.Ordinal))
                return Lexicon.Get("audio.ptt.transmit_slice_changed");
            if (now.TransmitHz != announced.TransmitHz)
                return Lexicon.Get("audio.ptt.transmit_frequency_changed");
            // Only the SELECTED slice changed. Its own key (repair round 3) and
            // its own sentence, ruled by Noel 2026-11-04 22:20: the notice WAS
            // announced, so "could not be announced" would be untrue.
            return Lexicon.Get("audio.ptt.selected_slice_changed");
        }

        /// <summary>
        /// Take back this notice's own sentence if it is still queued (#863,
        /// repair round 3, Codex's second re-check F1): the attempt that said
        /// it was cancelled before it keyed. Only this notice's piece goes; a
        /// newer attempt's notice on the same subject stays. Never throws.
        /// </summary>
        public static void Retire(Notice notice, string why)
        {
            if (notice?.Words == null) return;
            ClearDue();
            try { ScreenReaderOutput.CountdownSpeech.Withdraw(notice.Words, why); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice (" + notice.Door + "): could not take its sentence back: " + ex.Message,
                    TraceLevel.Warning);
            }
        }

        /// <summary>
        /// At the moment of keying, as <see cref="StillTheAnnouncedPlace(Notice)"/>;
        /// when it refuses, <paramref name="refusal"/> is the sentence to say.
        /// </summary>
        public static bool StillTheAnnouncedPlace(Notice notice, out string refusal)
        {
            refusal = null;
            if (StillTheAnnouncedPlace(notice, out SliceRoles now)) return true;
            refusal = notice?.Rig == null
                ? Lexicon.Get("audio.ptt.transmit_slice_not_heard")
                : RefusalForChangedPlace(notice.Announced, now);
            return false;
        }

        /// <summary>The rule above, pure.</summary>
        public static bool StillTheAnnouncedPlace(SliceRoles announced, SliceRoles now, bool mandatory,
            TransmitElsewhereNotice setting, SliceRoles? lastSpoken)
        {
            if (now.SamePlaceAs(announced)) return true;
            if (!now.TransmitsElsewhere) return true;
            if (now.SelectedIsDiagnostic && SameTransmit(now, announced)) return true;
            // A changed place a door would have said nothing about (tone only,
            // off, or a split already spoken) keys as it would have.
            return !Speaks(setting, mandatory, now, lastSpoken);
        }

        private static bool SameTransmit(SliceRoles a, SliceRoles b) =>
            a.HasTransmit == b.HasTransmit
            && string.Equals(a.TransmitLetter, b.TransmitLetter, StringComparison.Ordinal)
            && a.TransmitHz == b.TransmitHz;

        /// <summary>The split last HEARD on this rig, or null. For tests and the key-time rule.</summary>
        internal static SliceRoles? LastSpoken(FlexBase rig)
        {
            Memory memory = MemoryOf(rig);
            lock (MemoryLock) return memory.Spoken;
        }

        /// <summary>
        /// The door keyed. Remember what it told the operator: a split that
        /// was spoken AND HEARD is the split now known, and a key on the
        /// selected slice ends any split, so the next one is new. Only a door
        /// that keyed remembers, so a cancelled attempt cannot silence the
        /// next one.
        /// </summary>
        /// <remarks>
        /// <b>Heard means finished</b> (repair round 2, Codex's re-check R2).
        /// <see cref="Notice.Spoke"/> means the sentence was REQUESTED. A door
        /// that waited has it finished by now and commits at once. Push to
        /// talk in a dialog keys first and says after (ruled), so its sentence
        /// may still be queued, may be cut, or may be retired by the unkey:
        /// the split is then committed only when the sentence finishes whole,
        /// and never when it fails, is cut part-way, or is retired, so the
        /// next door on that split still says it. A key in between (any door)
        /// supersedes a late commit.
        /// </remarks>
        public static void Keyed(Notice notice)
        {
            if (notice?.Rig == null) return;
            Memory memory = MemoryOf(notice.Rig);
            long generation;
            lock (MemoryLock)
            {
                generation = ++memory.Generation;
                if (!notice.Spoke)
                {
                    if (!notice.Announced.TransmitsElsewhere) memory.Spoken = null;
                    return;
                }
                if (notice.Words == null || notice.Words.Finished)
                {
                    memory.Spoken = notice.Announced;
                    return;
                }
                if (notice.Words.Failed) return;   // never heard: still owed
            }
            _ = CommitWhenHeardAsync(notice, memory, generation);
        }

        /// <summary>
        /// Commit a door's split once its sentence has finished whole, unless
        /// another key has happened since. Failed, cut, retired or past the
        /// ceiling: nothing is committed, and the split stays owed.
        /// </summary>
        private static async Task CommitWhenHeardAsync(Notice notice, Memory memory, long generation)
        {
            bool heard;
            try
            {
                heard = await HeardAsync(notice.Words, () =>
                {
                    lock (MemoryLock) return memory.Generation == generation;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice (" + notice.Door + "): waiting for the sentence threw: " + ex.Message,
                    TraceLevel.Warning);
                return;
            }
            lock (MemoryLock)
            {
                if (heard && memory.Generation == generation)
                {
                    memory.Spoken = notice.Announced;
                    return;
                }
            }
            Tracing.TraceLine("TransmitSliceNotice (" + notice.Door + "): the sentence after the key was not heard whole;"
                + " this split is still owed to the next door", TraceLevel.Info);
        }

        /// <summary>
        /// A key that did not come through a notice (Home's push to talk, the
        /// lock, the system-wide lock): on the selected slice it ends any
        /// remembered split, as a door's key there does (Codex's re-check of
        /// #863, F1), so returning to the old split later is said again. It
        /// says nothing itself; whether these doors speak is Noel's question.
        /// </summary>
        public static void KeyedWithoutNotice(FlexBase rig)
        {
            if (rig == null) return;
            SliceRoles roles;
            try { roles = SliceRoles.Of(rig); }
            catch { return; }
            if (roles.TransmitsElsewhere) return;
            Memory memory = MemoryOf(rig);
            lock (MemoryLock)
            {
                memory.Generation++;
                memory.Spoken = null;
            }
        }

        /// <summary>
        /// Push to talk inside a dialog: "go, then say" (ruled 2026-11-03
        /// 17:55). After the key, the sentence when it is owed, queued behind
        /// the key-down line; the split is remembered only once that sentence
        /// has been heard whole (<see cref="Keyed"/>).
        /// </summary>
        public static void SayAfterKey(FlexBase rig, string door)
        {
            Notice notice = Prepare(rig, door, mandatory: false);
            if (notice == null) return;
            Keyed(notice);
        }

        /// <summary>
        /// Forget what a rig has been told: a new command connection starts
        /// with nothing said (integration fix, 2026-11-06). Called by the rig at
        /// every connection change, including a reconnect of the same radio,
        /// which keeps the same rig.
        /// </summary>
        internal static void ForgetFor(FlexBase rig)
        {
            if (rig == null) return;
            lock (MemoryLock) Memories.Remove(rig);
        }

        private static Func<ulong, string> FormatterFor(FlexBase rig)
        {
            var format = rig.Callouts?.FormatFreq;
            if (format != null) return hz => format(hz);
            return hz => (hz / 1_000_000.0).ToString("0.000###", CultureInfo.InvariantCulture);
        }
    }
}
