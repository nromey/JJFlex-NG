#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using JJTrace;
using Radios.Speech;

namespace Radios
{
    /// <summary>
    /// What kind of fact a connect produced. The numeric order is the order
    /// the composed statement speaks them in: hazards on the radio first
    /// (profiles, then the mic profile — both decide whether the operator
    /// can be heard on the air), then the audio path into this computer,
    /// then instrumentation. The order is fixed here so two facts arriving
    /// in a different order on a slow radio still read the same way.
    /// </summary>
    public enum ConnectFactKind
    {
        /// <summary>What was done about the profiles on the radio.</summary>
        ProfileStewardship = 0,
        /// <summary>The radio's mic-profile selection: repaired, or empty and warned about.</summary>
        MicProfileOnRadio = 1,
        /// <summary>What the per-radio PC audio policy did.</summary>
        PcAudio = 2,
        /// <summary>Instrumentation running that the operator cannot see.</summary>
        RunningInstrumentation = 3,
    }

    /// <summary>
    /// One thing a connect emitter has to say. Carries BOTH forms: the
    /// sentence the emitter would have spoken on its own (which is what the
    /// reference shows, and what is spoken when no connect is in flight),
    /// and the clause it contributes to the composed connect statement —
    /// null when the fact is a courtesy the operator can look up rather
    /// than something that changes what they do next.
    /// </summary>
    public sealed class ConnectFact
    {
        public ConnectFactKind Kind { get; }

        /// <summary>The full sentence — the reference text, and the late form.</summary>
        public string Full { get; }

        /// <summary>
        /// The clause for the composed statement. Null means "not
        /// volunteered at connect": recorded in the reference, never spoken
        /// in the connect burst.
        /// </summary>
        public string? Brief { get; }

        /// <summary>Verbosity gate, exactly as the emitter declared it.</summary>
        public VerbosityLevel Level { get; }

        /// <summary>The <see cref="SpeechSubject"/> the emitter declared.</summary>
        public string Subject { get; }

        /// <summary>The warning alarm earcon precedes this fact when it is spoken.</summary>
        public bool Alarm { get; }

        /// <summary>
        /// Where the fact came from, so the speech trace keeps naming the
        /// emitter rather than this file. A drop line reading
        /// "ConnectBriefing.cs" for every connect fact would throw away the
        /// one thing the trace is for.
        /// </summary>
        public string OriginFile { get; }
        public int OriginLine { get; }
        public string OriginMember { get; }

        public ConnectFact(
            ConnectFactKind kind,
            string full,
            string? brief,
            VerbosityLevel level,
            string subject,
            bool alarm = false,
            [CallerFilePath] string originFile = "",
            [CallerLineNumber] int originLine = 0,
            [CallerMemberName] string originMember = "")
        {
            if (string.IsNullOrEmpty(full)) throw new ArgumentException("A connect fact needs its full sentence.", nameof(full));
            if (string.IsNullOrEmpty(subject)) throw new ArgumentException("A connect fact needs a SpeechSubject.", nameof(subject));
            Kind = kind;
            Full = full;
            Brief = brief;
            Level = level;
            Subject = subject;
            Alarm = alarm;
            OriginFile = originFile;
            OriginLine = originLine;
            OriginMember = originMember;
        }
    }

    /// <summary>One thing the briefing hands to the speech channel.</summary>
    public readonly record struct BriefingUtterance(
        string Text,
        VerbosityLevel Level,
        string Subject,
        string OriginFile,
        int OriginLine,
        string OriginMember);

    /// <summary>
    /// The thing that composes the connect narration (#510, #511).
    ///
    /// <para><b>The measurement this exists to answer.</b> On 2026-09-02, at
    /// the radio, one connect emitted seven announcements — 711 characters —
    /// between ticks 135096 and 140099, five seconds. At the arbiter's own 80
    /// ms per character that is 56.9 seconds of speech handed to a channel
    /// that had five: an 11.4-to-1 overcommit. Seven emitters each behaved as
    /// though it was the only one talking; none knew the others existed, and
    /// nothing composed them. The queue could not drain, and every leader
    /// command afterwards salvaged and re-spoke the survivors — five of them
    /// on the first press, four again on the second.</para>
    ///
    /// <para><b>What composes.</b> During a connect the emitters hand their
    /// FACTS here instead of speaking. At the settle moment — the point the
    /// census already spoke from, 1.5 s after power-on, which is the last
    /// event of a connect — one composed statement goes out: a short lead
    /// naming the radio, then only the clauses that change what the operator
    /// does next, then (and only then) the Home arrival. Everything else
    /// stays reachable: every fact's full sentence is kept in
    /// <see cref="Reference"/>, which the Status dialog shows, and the slice
    /// census remains a keypress away. Nothing is dropped; the channel is
    /// simply not handed a minute of speech in five seconds.</para>
    ///
    /// <para><b>Why the clauses go out as separate utterances rather than one
    /// string.</b> The ledger keys supersession by subject (#503): "PC audio
    /// on" must be retired by "PC audio off", and a mic-profile verdict by the
    /// next one. Folding four subjects into one utterance would make the
    /// composed statement a lie the moment any one of its facts changed, and
    /// a salvage would re-speak the lie. So the composition is in the CONTENT
    /// and the TIMING — one moment, one order, one voice — while each clause
    /// keeps the subject its emitter declared. To the ear, queued back to
    /// back, it is one statement.</para>
    ///
    /// <para><b>Home arrives last, once it settles (#511, Noel's ruling).</b>
    /// The arrival used to speak when Home was REALISED — 250 ms after the
    /// shell was shown, which put it in the middle of the connect narration
    /// with profile talk on both sides of it, and the same reflex spoke the
    /// landing prefix at the instant a menu closed to start a discovery. A
    /// window announcing itself while the thing behind it is still assembling
    /// tells a blind operator that navigation is ready when it is not. So the
    /// arrival is HELD while a connect flow is in flight and released behind
    /// the composed statement; the landing prefix asks
    /// <see cref="InFlight"/> before speaking; and a flow that ends with no
    /// radio releases the arrival at that moment instead.</para>
    ///
    /// <para><b>No timers.</b> The flight begins at the connect door and ends
    /// at settle, at a flow that finishes without choosing a radio, or when
    /// the chosen radio goes away. Each of those is an event the application
    /// already raises; none is a delay of this class's own. A hold on the
    /// salvage train belongs to the arbiter's settle window, not here.</para>
    ///
    /// <para>An instance class with an injected sink so the composition is
    /// testable exactly, with no window and no voice.
    /// <see cref="Current"/> is the process-wide instance wired to the speech
    /// channel. Thread-safe: facts arrive from FlexBase's worker threads and
    /// the settle from the dispatcher.</para>
    /// </summary>
    public sealed class ConnectBriefing
    {
        /// <summary>The process-wide briefing, speaking through the arbiter.</summary>
        public static ConnectBriefing Current { get; } = new ConnectBriefing(
            u => ScreenReaderOutput.Speak(
                u.Text, SpeechIntent.Queue, u.Level, subject: u.Subject,
                callerFile: u.OriginFile, callerLine: u.OriginLine, callerMember: u.OriginMember),
            () => ScreenReaderOutput.PlayWarningAlarmEarcon?.Invoke());

        private readonly object _gate = new();
        private readonly Action<BriefingUtterance> _emit;
        private readonly Action _alarm;

        private bool _inFlight;
        private bool _radioChosen;
        private BriefingUtterance? _heldArrival;
        private readonly List<ConnectFact> _facts = new();
        private readonly List<string> _reference = new();

        public ConnectBriefing(Action<BriefingUtterance> emit, Action alarm)
        {
            _emit = emit ?? throw new ArgumentNullException(nameof(emit));
            _alarm = alarm ?? throw new ArgumentNullException(nameof(alarm));
        }

        /// <summary>
        /// True while a connect flow owns the narration: from the door
        /// (menu command, rescue button, or the radio-events wire for
        /// auto-connect and retry legs) until the flow settles, finishes
        /// with no radio, or loses the radio it chose. Home's
        /// self-announcements consult this.
        /// </summary>
        public bool InFlight
        {
            get { lock (_gate) return _inFlight; }
        }

        /// <summary>
        /// The full sentence of every fact the last connect produced, in the
        /// order the composed statement uses, plus the full slice census when
        /// there was one. This is where a fact that stopped being spoken at
        /// connect can still be found.
        /// </summary>
        public IReadOnlyList<string> Reference
        {
            get { lock (_gate) return _reference.ToArray(); }
        }

        // ── The flow ────────────────────────────────────────────────────

        /// <summary>
        /// A connect flow has begun — the operator asked to connect, or a
        /// connect leg is starting. Idempotent; the first call wins.
        /// </summary>
        public void FlowBegan()
        {
            lock (_gate)
            {
                if (_inFlight) return;
                _inFlight = true;
                _radioChosen = false;
            }
            Tracing.TraceLine("ConnectBriefing: flow began — Home is not settled", System.Diagnostics.TraceLevel.Info);
        }

        /// <summary>
        /// A radio has been chosen and is starting (the radio-events wire).
        /// Opens the flight if no door did, and starts a fresh briefing for
        /// this radio: the previous connect's facts and reference are
        /// cleared here, not at the door, so a discovery that is cancelled
        /// leaves the last radio's reference readable.
        /// </summary>
        public void RadioChosen()
        {
            lock (_gate)
            {
                _inFlight = true;
                _radioChosen = true;
                _facts.Clear();
                _reference.Clear();
            }
            Tracing.TraceLine("ConnectBriefing: radio chosen — collecting facts until settle", System.Diagnostics.TraceLevel.Info);
        }

        /// <summary>
        /// The chosen radio went away before settle (teardown, a failed
        /// start, a retry leg unwiring). The flight stays open — a retry may
        /// choose again — but the facts describe a radio that is gone.
        /// </summary>
        public void RadioGone()
        {
            lock (_gate)
            {
                if (!_inFlight) return;
                _radioChosen = false;
                _facts.Clear();
            }
            Tracing.TraceLine("ConnectBriefing: radio gone before settle — facts discarded", System.Diagnostics.TraceLevel.Info);
        }

        /// <summary>
        /// The flow finished without a radio powering on — the picker was
        /// cancelled, the connect failed, or the quiet scope's door closed.
        /// Ends the flight and releases the held Home arrival ONLY when no
        /// chosen radio is still starting: the menu door closes as soon as
        /// the Connecting window does, seconds before power-on, and that is
        /// not the end of the connect.
        /// </summary>
        public void FlowEndedWithoutRadio()
        {
            BriefingUtterance? arrival;
            lock (_gate)
            {
                if (!_inFlight) return;
                if (_radioChosen)
                {
                    Tracing.TraceLine(
                        "ConnectBriefing: flow ended without power-on but a chosen radio is still "
                        + "starting — staying in flight until settle", System.Diagnostics.TraceLevel.Info);
                    return;
                }
                _inFlight = false;
                arrival = _heldArrival;
                _heldArrival = null;
                _facts.Clear();
            }
            Tracing.TraceLine("ConnectBriefing: flow ended with no radio — Home is settled", System.Diagnostics.TraceLevel.Info);
            if (arrival != null) _emit(arrival.Value);
        }

        // ── The facts ───────────────────────────────────────────────────

        /// <summary>
        /// An emitter has something to say about this connect. In flight, it
        /// is collected and composed at settle; otherwise — no connect in
        /// flight, or the fact arrived after settle on a slow radio — it is
        /// spoken now, in full, exactly as the emitter would have spoken it,
        /// so nothing is ever lost to the composer's timing.
        /// </summary>
        public void Note(ConnectFact fact)
        {
            if (fact == null) throw new ArgumentNullException(nameof(fact));
            lock (_gate)
            {
                if (_inFlight && _radioChosen)
                {
                    // One verdict per kind: a later verdict on the same
                    // subject replaces an earlier one, the way the ledger
                    // would have superseded it.
                    _facts.RemoveAll(f => f.Subject == fact.Subject);
                    _facts.Add(fact);
                    _reference.RemoveAll(r => r == fact.Full);
                    _reference.Add(fact.Full);
                    Tracing.TraceLine(
                        $"ConnectBriefing: collected {fact.Kind} for the composed statement"
                        + (fact.Brief == null ? " (reference only — not volunteered at connect)" : ""),
                        System.Diagnostics.TraceLevel.Info);
                    return;
                }
                _reference.RemoveAll(r => r == fact.Full);
                _reference.Add(fact.Full);
            }
            Tracing.TraceLine(
                $"ConnectBriefing: {fact.Kind} arrived with no connect in flight — spoken in full now",
                System.Diagnostics.TraceLevel.Info);
            if (fact.Alarm) _alarm();
            _emit(Utterance(fact.Full, fact));
        }

        // ── Home ────────────────────────────────────────────────────────

        /// <summary>
        /// Home wants to announce its arrival. Held while a flow is in
        /// flight and released last at settle (or when the flow ends with no
        /// radio); spoken at once when Home is already the settled place the
        /// operator is.
        /// </summary>
        public void RequestHomeArrival(
            string text,
            VerbosityLevel level,
            [CallerFilePath] string originFile = "",
            [CallerLineNumber] int originLine = 0,
            [CallerMemberName] string originMember = "")
        {
            if (string.IsNullOrEmpty(text)) return;
            var u = new BriefingUtterance(text, level, SpeechSubject.WhereYouAre, originFile, originLine, originMember);
            lock (_gate)
            {
                if (_inFlight)
                {
                    _heldArrival = u;
                    Tracing.TraceLine("ConnectBriefing: Home arrival held until the connect settles (#511)",
                        System.Diagnostics.TraceLevel.Info);
                    return;
                }
            }
            _emit(u);
        }

        // ── Settle ──────────────────────────────────────────────────────

        /// <summary>
        /// The connect has settled: speak the composed statement, then the
        /// held Home arrival, and end the flight.
        /// </summary>
        /// <param name="lead">
        /// The one sentence naming the radio — "Connected to FLEX-8600,
        /// SmartLink, 4 slices." Spoken first, Critical, subject
        /// <see cref="SpeechSubject.ConnectLead"/>.
        /// </param>
        /// <param name="censusFull">
        /// The full slice census the lead stands in for, kept in the
        /// reference so the capability moved rather than vanished. Null when
        /// no slice had arrived by the settle.
        /// </param>
        /// <returns>What was emitted, in order, for the trace and for tests.</returns>
        public IReadOnlyList<BriefingUtterance> Settle(
            string lead,
            string? censusFull,
            [CallerFilePath] string originFile = "",
            [CallerLineNumber] int originLine = 0,
            [CallerMemberName] string originMember = "")
        {
            if (string.IsNullOrEmpty(lead)) throw new ArgumentException("The settle needs its lead sentence.", nameof(lead));

            var emitted = new List<BriefingUtterance>();
            bool alarm = false;
            BriefingUtterance? arrival;
            lock (_gate)
            {
                emitted.Add(new BriefingUtterance(lead, VerbosityLevel.Critical, SpeechSubject.ConnectLead,
                    originFile, originLine, originMember));

                foreach (var fact in _facts.OrderBy(f => (int)f.Kind))
                {
                    if (fact.Brief == null) continue;
                    if (fact.Alarm) alarm = true;
                    emitted.Add(Utterance(fact.Brief, fact));
                }

                if (censusFull != null)
                {
                    _reference.RemoveAll(r => r == censusFull);
                    _reference.Insert(0, censusFull);
                }

                arrival = _heldArrival;
                _heldArrival = null;
                _facts.Clear();
                _inFlight = false;
                _radioChosen = false;
            }

            int chars = emitted.Sum(u => u.Text.Length) + (arrival?.Text.Length ?? 0);
            Tracing.TraceLine(
                $"ConnectBriefing: settled — {emitted.Count} composed utterance(s)"
                + (arrival != null ? " then the Home arrival" : "")
                + $", {chars} characters, about {chars * SpeechArbiter.SalvageMsPerCharacter / 1000.0:F1} s "
                + "at the arbiter's estimate. Home is settled.",
                System.Diagnostics.TraceLevel.Info);

            if (alarm) _alarm();
            foreach (var u in emitted) _emit(u);
            if (arrival != null)
            {
                _emit(arrival.Value);
                emitted.Add(arrival.Value);
            }
            return emitted;
        }

        private static BriefingUtterance Utterance(string text, ConnectFact fact)
            => new(text, fact.Level, fact.Subject, fact.OriginFile, fact.OriginLine, fact.OriginMember);
    }
}
