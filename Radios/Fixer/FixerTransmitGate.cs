using System;
using System.Collections.Generic;
using System.Globalization;

namespace Radios.Fixer
{
    /// <summary>
    /// What the operator said is on the antenna socket, CLASSIFIED (#244).
    /// The words go into the report; this decides what the gate may do. The
    /// mapping from a page choice to one of these belongs to the stage set —
    /// the gate owns what each kind MEANS, never how a page spells it.
    /// </summary>
    public enum FixerLoadKind
    {
        /// <summary>Nothing declared yet. The gate stays shut.</summary>
        None = 0,

        /// <summary>A dummy load: normal test power, nothing radiates.</summary>
        DummyLoad,

        /// <summary>A real antenna: every transmit is an on-air emission, so
        /// the checks run at LOW power only — enough for the meters to read,
        /// no more (#180).</summary>
        Antenna,

        /// <summary>An amplifier is in the path. Low power only, like an
        /// antenna — and every reading describes the amplifier's INPUT, which
        /// the report must carry or the numbers cannot be read (#223).</summary>
        Amplifier,

        /// <summary>"Nothing, or I am not sure" — an EXPLICIT refusal, not a
        /// silent wait. The operator has told us something and deserves an
        /// answer; the gate stays shut and says exactly why (#244).</summary>
        NothingOrUnsure,
    }

    /// <summary>
    /// The one place a request to key the transmitter is granted or refused.
    /// The page asks; this decides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The engine cannot transmit, and neither can the page.</b> That is
    /// arranged in <see cref="FixerStage.Execute"/> by absence — a stage whose
    /// executor the host never supplied simply records that it could not run.
    /// Absence is a good boundary but a fragile one: it holds only for as long
    /// as nobody adds a convenient shortcut. This class makes the boundary a
    /// DECISION instead, so that adding a shortcut means visibly bypassing
    /// something rather than quietly filling in a gap.
    /// </para>
    /// <para>
    /// <b>THE RULE: the host never takes the caller's word for a safety fact.</b>
    /// A request may carry whatever it likes; every fact that decides whether
    /// RF goes out is held HERE, by the host, or read from the radio itself. If
    /// a request says "not currently keyed" and the rig is keyed, the rig wins.
    /// If it says "the load is declared", that is ignored — a declaration is an
    /// event this gate recorded once, not a flag a caller re-asserts per
    /// request. The distinction matters because the realistic failure is not an
    /// attacker; it is a double-bound event handler, a stale run id after a
    /// re-run, or a retry loop in a surface that only ever runs when something
    /// is already broken.
    /// </para>
    /// <para>
    /// <b>Every refusal is speakable.</b> A blind operator who presses a button
    /// and hears nothing has no way to tell "refused" from "broken" from "still
    /// working". <see cref="Decision.Explanation"/> is written to be spoken as
    /// it stands, and the caller is expected to say it.
    /// </para>
    /// <para>
    /// Pure and clock-injected, so every rule below is testable without a
    /// WebView, a screen reader or a radio.
    /// </para>
    /// </remarks>
    public sealed class FixerTransmitGate
    {
        // ---- runaway guard ----
        //
        // Not a thermal model — that is the radio's business and its own task.
        // This catches the shape of a BUG: a person re-running a stage cannot
        // press a button four times in three seconds; a repeating handler does
        // it in milliseconds. The rolling window is therefore the sharp
        // instrument, and the run totals are backstops.

        /// <summary>How many transmits may start within <see cref="BurstWindowSeconds"/>.</summary>
        public const int BurstLimit = 4;

        /// <summary>The rolling window the burst limit applies over.</summary>
        public const int BurstWindowSeconds = 3;

        /// <summary>
        /// Total key-down seconds allowed in one run. Deliberately generous: an
        /// operator adjusting microphone gain may legitimately re-run a
        /// transmitting stage many times, and a guard that fires during honest
        /// work teaches operators to distrust guards.
        /// </summary>
        public const int RunKeyDownBudgetSeconds = 300;

        /// <summary>Total transmits allowed in one run, as a second backstop.</summary>
        public const int RunTransmitLimit = 60;

        /// <summary>Why a request was refused.</summary>
        public enum Refusal
        {
            /// <summary>It was not refused.</summary>
            None = 0,
            /// <summary>No radio to transmit with.</summary>
            NoRadio,
            /// <summary>No run is open, so nothing legitimately needs to key.</summary>
            NoRun,
            /// <summary>The request names a different run — a stale caller.</summary>
            WrongRun,
            /// <summary>The operator has not said what the antenna socket is
            /// connected to.</summary>
            LoadNotDeclared,
            /// <summary>Something is already transmitting.</summary>
            AlreadyInFlight,
            /// <summary>The run was abandoned.</summary>
            RunAborted,
            /// <summary>This stage already transmitted and no re-run was asked for.</summary>
            StageAlreadyTransmitted,
            /// <summary>Requests are arriving faster than a person can press a button.</summary>
            TooFast,
            /// <summary>This run has spent its key-down budget.</summary>
            BudgetSpent,
            /// <summary>The stage was not declared as one that transmits.</summary>
            StageDoesNotTransmit,
            /// <summary>The operator declared nothing connected, or that they
            /// are not sure — the one case where refusing is plainly correct
            /// rather than timid (#180).</summary>
            LoadForbidsTransmit,
            /// <summary>A real antenna (or an amplifier) is declared and the
            /// radio's power is above the low-power ceiling — or could not be
            /// read at all (#180, #244).</summary>
            PowerTooHighForLoad,
        }

        /// <summary>Granted, or refused with something worth saying out loud.</summary>
        public readonly struct Decision
        {
            public readonly bool Allowed;
            public readonly Refusal Why;

            /// <summary>Written to be spoken as it stands. Empty only when allowed.</summary>
            public readonly string Explanation;

            private Decision(bool allowed, Refusal why, string explanation)
            { Allowed = allowed; Why = why; Explanation = explanation ?? ""; }

            internal static Decision Grant() => new Decision(true, Refusal.None, "");

            internal static Decision Refuse(Refusal why, string explanation)
                => new Decision(false, why, explanation);
        }

        private readonly Func<DateTime> _clock;
        private readonly List<DateTime> _window = new List<DateTime>();
        private readonly HashSet<string> _transmitted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Watts at or below which a transmit into a DECLARED ANTENNA (or an
        /// amplifier) is allowed (#180). Ten: the same boundary #224 draws
        /// between "worth telling you" and "worth stopping for", and the
        /// AudioCheckLowPowerWatts default. Enough for every meter to read;
        /// not enough to be a bad neighbour on a live band.
        /// </summary>
        public const int LowPowerCeilingWatts = 10;

        private string _runId = "";
        private bool _aborted;
        private bool _inFlight;
        private string _loadDeclaration = "";
        private FixerLoadKind _loadKind = FixerLoadKind.None;
        private bool _loadDeclaredRemotely;
        private double _keyDownSeconds;
        private int _transmitCount;
        private DateTime _keyedAt;

        public FixerTransmitGate(Func<DateTime> clockUtc = null)
            => _clock = clockUtc ?? (() => DateTime.UtcNow);

        /// <summary>The run this gate is open for, or empty.</summary>
        public string RunId => _runId;

        /// <summary>
        /// What the operator said the antenna socket is connected to, or empty
        /// when nothing was said. Goes into the report: a measurement whose load
        /// is unrecorded cannot be read later, and FlexRadio will ask.
        /// </summary>
        public string LoadDeclaration => _loadDeclaration;

        /// <summary>What kind of load that declaration described (#244).</summary>
        public FixerLoadKind LoadKind => _loadKind;

        /// <summary>
        /// True when the declaration was made over a remote session (#247) —
        /// by someone who cannot see the socket. Recorded so the report can
        /// weight it, never used to refuse: a remote operator's declaration
        /// is still a declaration.
        /// </summary>
        public bool LoadDeclaredRemotely => _loadDeclaredRemotely;

        /// <summary>
        /// The declaration as the REPORT should carry it: the operator's own
        /// words, with the remote provenance appended when it applies. One
        /// home for the phrasing, because a Flex support reader needs to
        /// weight a declaration made a thousand miles from the socket
        /// differently from one made in the room (#247).
        /// </summary>
        public string LoadDeclarationForReport
            => _loadDeclaration.Length == 0
                ? ""
                : _loadDeclaration + (_loadDeclaredRemotely
                    ? " (declared over a remote session, by an operator not at the station)"
                    : "");

        /// <summary>Cumulative key-down this run, in seconds.</summary>
        public double KeyDownSeconds => _keyDownSeconds;

        /// <summary>Transmits started this run. The run total, not the window.</summary>
        public int TransmitCount => _transmitCount;

        /// <summary>True while a transmit granted by this gate is in flight.</summary>
        public bool InFlight => _inFlight;

        /// <summary>True when the run was abandoned.</summary>
        public bool Aborted => _aborted;

        /// <summary>
        /// Open the gate for a run. Everything resets — INCLUDING the load
        /// declaration, on purpose. A declaration is a statement about the
        /// station as it stood when it was made; carrying it into a later run
        /// would let a run inherit a fact nobody restated, which is exactly how
        /// an operator ends up transmitting into an antenna the app still
        /// believes is a dummy load.
        /// </summary>
        public void BeginRun(string runId)
        {
            _runId = runId ?? "";
            _aborted = false;
            _inFlight = false;
            _loadDeclaration = "";
            _loadKind = FixerLoadKind.None;
            _loadDeclaredRemotely = false;
            _keyDownSeconds = 0;
            _transmitCount = 0;
            _window.Clear();
            _transmitted.Clear();
        }

        /// <summary>
        /// Record what the operator says the antenna socket is connected to.
        /// Recorded ONCE, here, by the host — never accepted as part of a
        /// transmit request, because a fact re-asserted per request is a fact
        /// the caller controls.
        /// </summary>
        /// <param name="what">The answer in the operator's own words. Blank
        /// clears the declaration and shuts the gate.</param>
        /// <param name="kind">What the answer MEANS to the gate (#244). The
        /// caller maps the page's choice id; an unmappable choice must arrive
        /// as <see cref="FixerLoadKind.NothingOrUnsure"/> so an unknown
        /// answer fails closed, never open.</param>
        /// <param name="declaredRemotely">True when the declaring operator is
        /// on a remote session and cannot see the socket (#247). Recorded for
        /// the report; never a refusal.</param>
        public void DeclareLoad(string what, FixerLoadKind kind, bool declaredRemotely = false)
        {
            _loadDeclaration = string.IsNullOrWhiteSpace(what) ? "" : what.Trim();
            _loadKind = _loadDeclaration.Length == 0 ? FixerLoadKind.None : kind;
            _loadDeclaredRemotely = _loadDeclaration.Length > 0 && declaredRemotely;
        }

        /// <summary>Abandon the run. No further transmit until a new one opens.</summary>
        public void AbortRun() => _aborted = true;

        /// <summary>
        /// The operator explicitly asked to run this stage again, so its
        /// once-only flag is cleared. Explicit, because that flag exists to kill
        /// double-fires and a double-fire never announces itself as a re-run.
        /// </summary>
        public void AllowReRun(string stageId)
        {
            if (!string.IsNullOrWhiteSpace(stageId)) _transmitted.Remove(stageId);
        }

        /// <summary>
        /// Decide whether this request may key the transmitter.
        /// </summary>
        /// <param name="runId">The run the caller believes it is in.</param>
        /// <param name="stageId">The stage asking.</param>
        /// <param name="stageTransmits">
        /// From the stage set: was this stage DECLARED as one that transmits? A
        /// request from a stage that was not is refused rather than obeyed. The
        /// page says next to the run control whether a step transmits, so a step
        /// that keys without having said so is a blind operator being surprised
        /// by their own radio.
        /// </param>
        /// <param name="radioReachable">Host-observed, never caller-asserted.</param>
        /// <param name="rigIsKeyed">
        /// Read from the RADIO. If the rig is keyed by anything at all — a foot
        /// pedal, another client on a MultiFlex station, a previous stage that
        /// did not come down — this gate does not stack a transmit on top of it.
        /// </param>
        /// <param name="transmitPowerWatts">
        /// The power THIS transmit would use, read from the radio by the
        /// boundary — tune power for the tune probe, transmit power for the
        /// audio stages. Only consulted when the declared load demands the
        /// low-power ceiling (#180); -1 means it could not be read, which
        /// REFUSES under those loads rather than hoping, because a transmit
        /// into a live antenna at a power nobody could read is the exact
        /// gamble this gate exists to prevent.
        /// </param>
        public Decision Request(string runId, string stageId, bool stageTransmits,
                                bool radioReachable, bool rigIsKeyed,
                                int transmitPowerWatts = -1)
        {
            if (!stageTransmits)
                return Decision.Refuse(Refusal.StageDoesNotTransmit,
                    "That step is not meant to transmit, so nothing was sent.");

            if (_aborted)
                return Decision.Refuse(Refusal.RunAborted,
                    "The test was stopped, so nothing was transmitted. "
                    + "Start it again if you want to carry on.");

            if (_runId.Length == 0)
                return Decision.Refuse(Refusal.NoRun,
                    "There is no test running, so nothing was transmitted.");

            if (!string.Equals(_runId, runId ?? "", StringComparison.Ordinal))
                return Decision.Refuse(Refusal.WrongRun,
                    "That request belongs to an earlier test, so nothing was transmitted. "
                    + "Close this and start again.");

            if (!radioReachable)
                return Decision.Refuse(Refusal.NoRadio,
                    "The radio is not reachable, so nothing was transmitted.");

            if (rigIsKeyed)
                return Decision.Refuse(Refusal.AlreadyInFlight,
                    "The radio is already transmitting, so nothing more was sent. "
                    + "Let it finish, or press Stop.");

            if (_inFlight)
                return Decision.Refuse(Refusal.AlreadyInFlight,
                    "A transmit is already running, so nothing more was sent.");

            if (_loadDeclaration.Length == 0)
                return Decision.Refuse(Refusal.LoadNotDeclared,
                    "Nothing was transmitted, because you have not said yet what the antenna "
                    + "socket is connected to. Say what is connected, and this step will run.");

            // "Nothing, or I am not sure" is an EXPLICIT answer and gets an
            // explicit refusal — an unattended transmit into an unknown load
            // is the one case where refusing is plainly correct (#180, #244).
            if (_loadKind == FixerLoadKind.NothingOrUnsure)
                return Decision.Refuse(Refusal.LoadForbidsTransmit,
                    "Nothing was transmitted. You said nothing is connected, or that you "
                    + "are not sure — and this tool never transmits into an unknown load. "
                    + "Connect a dummy load or an antenna, answer the antenna question "
                    + "again, and this step will run.");

            // A real antenna, or an amplifier, caps the power (#180): every
            // transmit there is an on-air emission, or drive into a stage
            // that multiplies it. The ceiling is ENFORCED, not recorded — a
            // declaration the gate does not act on is a note, and notes do
            // not stop transmitters.
            if (_loadKind == FixerLoadKind.Antenna || _loadKind == FixerLoadKind.Amplifier)
            {
                string into = _loadKind == FixerLoadKind.Antenna
                    ? "a real antenna" : "an amplifier";

                if (transmitPowerWatts < 0)
                    return Decision.Refuse(Refusal.PowerTooHighForLoad,
                        "Nothing was transmitted. You declared " + into + ", and the "
                        + "radio's power for this step could not be read — into " + into
                        + " these checks only transmit when the power is known to be "
                        + LowPowerCeilingWatts.ToString(CultureInfo.InvariantCulture)
                        + " watts or less.");

                if (transmitPowerWatts > LowPowerCeilingWatts)
                    return Decision.Refuse(Refusal.PowerTooHighForLoad,
                        "Nothing was transmitted. The radio's power for this step is "
                        + transmitPowerWatts.ToString(CultureInfo.InvariantCulture)
                        + " watts, and you declared " + into + ". Into " + into
                        + " these checks transmit at "
                        + LowPowerCeilingWatts.ToString(CultureInfo.InvariantCulture)
                        + " watts or less — turn the power down, or declare a dummy "
                        + "load, and this step will run.");
            }

            if (_transmitted.Contains(stageId ?? ""))
                return Decision.Refuse(Refusal.StageAlreadyTransmitted,
                    "That step has already transmitted once. "
                    + "Choose Run again if you meant to repeat it.");

            DateTime now = _clock();
            TrimWindow(now);
            if (_window.Count >= BurstLimit)
                return Decision.Refuse(Refusal.TooFast,
                    "Transmit requests are arriving faster than they should be, so this one was "
                    + "refused. That usually means something is repeating itself rather than "
                    + "anything you did.");

            if (_keyDownSeconds >= RunKeyDownBudgetSeconds)
                return Decision.Refuse(Refusal.BudgetSpent,
                    "This test has transmitted for about "
                    + Math.Round(_keyDownSeconds).ToString(CultureInfo.InvariantCulture)
                    + " seconds altogether, which is as much as one run allows. "
                    + "Start a new test to carry on.");

            if (_transmitCount >= RunTransmitLimit)
                return Decision.Refuse(Refusal.BudgetSpent,
                    "This test has transmitted as many times as one run allows. "
                    + "Start a new test to carry on.");

            return Decision.Grant();
        }

        /// <summary>
        /// The transmit granted for <paramref name="stageId"/> has begun.
        /// </summary>
        /// <remarks>
        /// Called at KEY-DOWN, not at grant. A grant that never keyed must not
        /// spend the budget, and the two are separated by however long the radio
        /// takes to confirm — which, on a queued write, is not nothing.
        /// </remarks>
        public void NoteKeyed(string stageId)
        {
            _inFlight = true;
            _keyedAt = _clock();
            _window.Add(_keyedAt);
            _transmitCount++;
            if (!string.IsNullOrWhiteSpace(stageId)) _transmitted.Add(stageId);
        }

        /// <summary>
        /// The transmit has ended, however it ended.
        /// </summary>
        /// <remarks>
        /// Safe to call twice, and safe to call with no matching
        /// <see cref="NoteKeyed"/>. An unkey path that throws on a double call
        /// is an unkey path a caller can be tempted to guard with an if — and
        /// the unkey is the one step that must never be skippable.
        /// </remarks>
        public void NoteUnkeyed()
        {
            if (!_inFlight) return;
            _inFlight = false;

            double held = (_clock() - _keyedAt).TotalSeconds;
            if (held > 0 && !double.IsNaN(held) && !double.IsInfinity(held))
                _keyDownSeconds += held;
        }

        private void TrimWindow(DateTime now)
        {
            DateTime cutoff = now.AddSeconds(-BurstWindowSeconds);
            for (int i = _window.Count - 1; i >= 0; i--)
                if (_window[i] < cutoff) _window.RemoveAt(i);
        }
    }
}
