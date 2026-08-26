#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// How loudly a running cost deserves to be mentioned when the operator
    /// reaches a boundary — closing the application above all.
    /// </summary>
    public enum RunningCostWeight
    {
        /// <summary>
        /// On, costs something, but its presence is already obvious or its cost
        /// is the baseline everybody pays. The always-on diagnostic log and the
        /// meter tones live here: the log is on for every operator by default,
        /// and the tones are audible by definition. Both are still answered by
        /// the on-demand read — "what is running" that omits things that are
        /// running is not an answer — but neither raises a prompt at exit.
        ///
        /// The distinction is not about size. It is about whether a reasonable
        /// operator could be UNAWARE of it, which is the whole reason this
        /// register exists.
        /// </summary>
        Routine = 0,

        /// <summary>
        /// On, costs something, and nothing else in the application says so.
        /// This is the class Noel named on 2026-08-25 — "we haven't built in
        /// the warning machinery to remind me to turn that crap off" — and it
        /// is the class that raises the boundary prompt.
        /// </summary>
        Notable = 1
    }

    /// <summary>
    /// One expensive thing, as it describes itself to the register: a name, a
    /// running cost, and the means to stop it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constructed rather than object-initialized into existence because the
    /// two things every registration must have — an id and a name — are the two
    /// things a half-filled registration would be useless without. Everything
    /// else is genuinely optional and stays a settable property so VB call
    /// sites can use <c>With { }</c>.
    /// </para>
    /// <para><b>The id is not the name.</b> The id is stable and internal: it
    /// keys threshold bookkeeping and survives rewording. The name is spoken.
    /// Changing the name is a copy edit; changing the id resets what the
    /// register remembers about that thing.</para>
    /// </remarks>
    public sealed class RunningCost
    {
        public RunningCost(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A running cost needs an id.", nameof(id));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A running cost needs a spoken name.", nameof(name));
            Id = id;
            Name = name;
        }

        /// <summary>Stable internal key. Never spoken.</summary>
        public string Id { get; }

        /// <summary>
        /// What it is called out loud — a noun phrase, capitalised, no trailing
        /// stop: "Meter stream recording", "Detailed diagnostic capture".
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Whether it is running right now. Null means "running for as long as
        /// it is registered", which is the shape a transient thing wants.
        /// </summary>
        /// <remarks>
        /// A predicate rather than a flag the feature sets, wherever the feature
        /// has a readable state. The status line learned this lesson already:
        /// it re-reads reality instead of caching a copy, because a cached copy
        /// is how the retired trace dialog came to announce "Start tracing" for
        /// a trace that was already running. A register that has to be TOLD when
        /// something stops is a register that will eventually be wrong, and
        /// being wrong here means saying nothing is running when something is.
        /// </remarks>
        public Func<bool>? IsRunning { get; set; }

        /// <summary>
        /// What it has cost so far, as a spoken fragment with no leading capital
        /// and no trailing stop: "4.7 megabytes", "218,000 meter lines into the
        /// log". Null or empty when the thing genuinely has no measurable cost,
        /// in which case the reading is just its name.
        /// </summary>
        public Func<string?>? DescribeCost { get; set; }

        /// <summary>
        /// The same cost as a number, for <see cref="Thresholds"/>. Units are
        /// this registration's own business — bytes for a file, lines for a
        /// stream, seconds for a tone — because the thresholds are declared in
        /// the same units by the same author.
        /// </summary>
        public Func<long>? Measure { get; set; }

        /// <summary>
        /// Ascending bounds on <see cref="Measure"/>. Crossing one announces
        /// ONCE and never again for that run; a run ends when
        /// <see cref="IsRunning"/> goes false, which is what makes the second
        /// bench session say it again.
        /// </summary>
        /// <remarks>
        /// Thresholds, never elapsed time. Noel's ruling on 2026-08-25 and the
        /// same reasoning CLAUDE.md applies to keeping
        /// <c>dotnet list package --outdated</c> out of the daily seal:
        /// periodic nagging trains the operator to ignore the channel, which
        /// costs more than it saves. A bound is crossed because something
        /// actually grew.
        /// </remarks>
        public IReadOnlyList<long>? Thresholds { get; set; }

        /// <summary>
        /// Render a threshold value for speech. Defaults to the plain number
        /// with digit grouping.
        /// </summary>
        public Func<long, string>? DescribeThreshold { get; set; }

        /// <summary>
        /// Stop it. Null when this registration has no safe programmatic stop —
        /// in which case <see cref="StopHow"/> should say where the switch is.
        /// </summary>
        public Action? Stop { get; set; }

        /// <summary>
        /// Where the operator turns it off themselves, spoken: "Control J, then
        /// Control D", "Settings, Diagnostics". Present even when
        /// <see cref="Stop"/> is, because an operator who declines the offer at
        /// a boundary still needs to know the route.
        /// </summary>
        public string? StopHow { get; set; }

        /// <summary>
        /// True when this is a persisted switch that will STILL BE ON at the
        /// next launch. The single most useful fact the exit prompt carries,
        /// and the exact shape of the 2026-08-25 incident: meter recording was
        /// on across sessions and nothing ever said so.
        /// </summary>
        public bool SurvivesRestart { get; set; }

        /// <summary>See <see cref="RunningCostWeight"/>.</summary>
        public RunningCostWeight Weight { get; set; } = RunningCostWeight.Routine;
    }

    /// <summary>
    /// A registration as read at one instant — flattened on purpose, so a
    /// caller reads a snapshot rather than holding delegates it might invoke
    /// twice and get two different answers from.
    /// </summary>
    public sealed class RunningCostReading
    {
        internal RunningCostReading(RunningCost source, string? cost)
        {
            Id = source.Id;
            Name = source.Name;
            Cost = string.IsNullOrWhiteSpace(cost) ? null : cost;
            StopHow = source.StopHow;
            SurvivesRestart = source.SurvivesRestart;
            Weight = source.Weight;
            CanStop = source.Stop != null;
        }

        public string Id { get; }
        public string Name { get; }
        public string? Cost { get; }
        public string? StopHow { get; }
        public bool SurvivesRestart { get; }
        public RunningCostWeight Weight { get; }
        public bool CanStop { get; }

        /// <summary>
        /// One sentence: name, cost, and whether it outlives this session.
        /// Shared by the spoken read and the boundary dialog so the two cannot
        /// describe the same thing differently.
        /// </summary>
        public string Sentence()
        {
            var sb = new StringBuilder(Name);
            if (Cost != null) sb.Append(", ").Append(Cost);
            if (SurvivesRestart) sb.Append(", and it will still be on the next time you start");
            sb.Append('.');
            return sb.ToString();
        }
    }

    /// <summary>Raised when a registration's measure crosses a declared bound.</summary>
    public sealed class RunningCostThresholdEventArgs : EventArgs
    {
        internal RunningCostThresholdEventArgs(RunningCostReading reading, long threshold, string sentence)
        {
            Reading = reading;
            Threshold = threshold;
            Sentence = sentence;
        }

        public RunningCostReading Reading { get; }
        public long Threshold { get; }

        /// <summary>The whole thing to say, already assembled.</summary>
        public string Sentence { get; }
    }

    /// <summary>
    /// The one place that knows what expensive things are currently running —
    /// task #253.
    /// </summary>
    /// <remarks>
    /// <para><b>Why one register rather than a reminder per feature.</b> Noel,
    /// 2026-08-25, on a diagnostic capture that came to 4.7 MB dominated by
    /// meter packets: <i>"that's because I didn't turn meter off because we
    /// haven't built in the warning machinery to remind me to turn that crap
    /// off if I don't need it."</i> N features each inventing their own
    /// reminder produces N vocabularies and most of them get forgotten at
    /// birth. This is the same one-home-for-a-rule principle already applied to
    /// <see cref="SMeterReading"/> and the diagnostics bridge.</para>
    ///
    /// <para><b>This is an accessibility requirement, not tidiness.</b> A
    /// sighted operator has a recording indicator in the corner of the screen,
    /// a visibly moving meter, a panel obviously open. Noel has none of that. A
    /// setting that persists across restarts, changes what the application
    /// writes, and has no perceptible presence is invisible in exactly the way
    /// this project exists to fix.</para>
    ///
    /// <para><b>Read three ways, and only three.</b> On demand (Ctrl+J, O); at
    /// a boundary — exit is the ruled priority boundary; and on a threshold.
    /// <b>Never on a timer.</b> <see cref="Poll"/> exists so something can
    /// SAMPLE the measures, and sampling is not the same as announcing: a poll
    /// that finds nothing crossed says nothing at all, forever, however often
    /// it runs.</para>
    ///
    /// <para><b>Nothing here speaks.</b> The register composes sentences and
    /// raises <see cref="ThresholdCrossed"/>; the application decides how to
    /// deliver them. That keeps this type testable without a screen reader and
    /// keeps the verbosity policy where the rest of the verbosity policy
    /// lives.</para>
    /// </remarks>
    public static class RunningCostRegister
    {
        private static readonly object Gate = new object();
        private static readonly List<RunningCost> Registered = new List<RunningCost>();

        /// <summary>
        /// Highest bound already announced, per registration id. Cleared for a
        /// registration the moment it stops running, so the next bench session
        /// gets its warnings again rather than inheriting the last one's
        /// silence.
        /// </summary>
        private static readonly Dictionary<string, long> Announced =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// Raised by <see cref="Poll"/> when a measure crosses a declared bound.
        /// At most once per bound per run.
        /// </summary>
        public static event EventHandler<RunningCostThresholdEventArgs>? ThresholdCrossed;

        /// <summary>
        /// Add a registration. Re-registering the same id REPLACES the previous
        /// one rather than doubling it — a surface that wires itself twice
        /// should not make the operator hear everything twice.
        /// </summary>
        /// <returns>
        /// A token that unregisters on dispose, for transient things that start
        /// and stop. Standing registrations can ignore it.
        /// </returns>
        public static IDisposable Register(RunningCost cost)
        {
            if (cost == null) throw new ArgumentNullException(nameof(cost));
            lock (Gate)
            {
                Registered.RemoveAll(c => string.Equals(c.Id, cost.Id, StringComparison.Ordinal));
                Registered.Add(cost);
                Announced.Remove(cost.Id);
            }
            return new Token(cost.Id);
        }

        /// <summary>Remove a registration by id. Silent if it was never there.</summary>
        public static void Unregister(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (Gate)
            {
                Registered.RemoveAll(c => string.Equals(c.Id, id, StringComparison.Ordinal));
                Announced.Remove(id);
            }
        }

        /// <summary>Drop everything. For tests, and for nothing else.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Registered.Clear();
                Announced.Clear();
            }
        }

        /// <summary>Whether an id is currently registered, running or not.</summary>
        public static bool IsRegistered(string id)
        {
            lock (Gate)
            {
                return Registered.Any(c => string.Equals(c.Id, id, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// Everything running right now, Notable first, each in registration
        /// order within its weight. A registration whose predicate throws is
        /// treated as not running and traced — a broken probe must never take
        /// the answer down with it, because the answer is most wanted when
        /// something is already going wrong.
        /// </summary>
        public static IReadOnlyList<RunningCostReading> Snapshot()
        {
            List<RunningCost> copy;
            lock (Gate) copy = new List<RunningCost>(Registered);

            var readings = new List<RunningCostReading>();
            foreach (RunningCost c in copy)
            {
                if (!SafeIsRunning(c)) continue;
                readings.Add(new RunningCostReading(c, SafeCost(c)));
            }

            return readings
                .OrderByDescending(r => (int)r.Weight)
                .ToList();
        }

        /// <summary>Anything running that nothing else in the app announces.</summary>
        public static bool AnyNotable => Snapshot().Any(r => r.Weight == RunningCostWeight.Notable);

        /// <summary>
        /// The on-demand answer, assembled. One sentence per running thing, and
        /// a plain statement when there is nothing to report — an empty answer
        /// is still an answer, and silence would read as the key not working.
        /// </summary>
        public static string DescribeForSpeech()
        {
            IReadOnlyList<RunningCostReading> readings = Snapshot();
            if (readings.Count == 0) return Lexicon.Get("logging.running.nothing");

            var sb = new StringBuilder();
            foreach (RunningCostReading r in readings)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(r.Sentence());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Sample every measure and raise <see cref="ThresholdCrossed"/> for any
        /// bound crossed since the last look. Cheap, side-effect-free when
        /// nothing has grown, and safe to call from anywhere.
        /// </summary>
        /// <remarks>
        /// Also the reset point: a registration that has STOPPED running has its
        /// bookkeeping cleared here, so bounds fire again next time it starts.
        /// </remarks>
        public static void Poll()
        {
            List<RunningCost> copy;
            lock (Gate) copy = new List<RunningCost>(Registered);

            foreach (RunningCost c in copy)
            {
                bool running = SafeIsRunning(c);
                if (!running)
                {
                    lock (Gate) Announced.Remove(c.Id);
                    continue;
                }

                if (c.Thresholds == null || c.Thresholds.Count == 0 || c.Measure == null) continue;

                long value;
                try { value = c.Measure(); }
                catch (Exception ex) { TraceProbeFailure(c, "measure", ex); continue; }

                long crossed = 0;
                foreach (long bound in c.Thresholds)
                {
                    if (value >= bound && bound > crossed) crossed = bound;
                }
                if (crossed == 0) continue;

                lock (Gate)
                {
                    if (Announced.TryGetValue(c.Id, out long already) && already >= crossed) continue;
                    Announced[c.Id] = crossed;
                }

                var reading = new RunningCostReading(c, SafeCost(c));
                string rendered = RenderThreshold(c, crossed);
                string sentence = Lexicon.Get("logging.running.threshold",
                    ("name", reading.Name), ("amount", rendered));
                if (!string.IsNullOrWhiteSpace(c.StopHow))
                {
                    sentence += " " + Lexicon.Get("logging.running.threshold_stop", ("how", c.StopHow!));
                }

                try
                {
                    ThresholdCrossed?.Invoke(null, new RunningCostThresholdEventArgs(reading, crossed, sentence));
                }
                catch (Exception ex)
                {
                    // A subscriber's failure must not stop the next registration
                    // being polled.
                    TraceProbeFailure(c, "threshold subscriber", ex);
                }
            }
        }

        /// <summary>
        /// Stop everything running that CAN be stopped, optionally only the
        /// Notable ones. Returns the names it actually stopped, in the order it
        /// stopped them, so the caller can report rather than assert.
        /// </summary>
        /// <remarks>
        /// Returns names rather than a count on purpose: "two things turned
        /// off" is not something an operator can check, and this is the one
        /// action in the whole feature that changes state on their behalf.
        /// </remarks>
        public static IReadOnlyList<string> StopAll(bool notableOnly = true)
        {
            List<RunningCost> copy;
            lock (Gate) copy = new List<RunningCost>(Registered);

            var stopped = new List<string>();
            foreach (RunningCost c in copy)
            {
                if (c.Stop == null) continue;
                if (notableOnly && c.Weight != RunningCostWeight.Notable) continue;
                if (!SafeIsRunning(c)) continue;

                try
                {
                    c.Stop();
                    stopped.Add(c.Name);
                }
                catch (Exception ex)
                {
                    TraceProbeFailure(c, "stop", ex);
                }
            }
            return stopped;
        }

        // ── internals ────────────────────────────────────────────────────

        private static bool SafeIsRunning(RunningCost c)
        {
            if (c.IsRunning == null) return true;
            try { return c.IsRunning(); }
            catch (Exception ex) { TraceProbeFailure(c, "is-running", ex); return false; }
        }

        private static string? SafeCost(RunningCost c)
        {
            if (c.DescribeCost == null) return null;
            try { return c.DescribeCost(); }
            catch (Exception ex) { TraceProbeFailure(c, "cost", ex); return null; }
        }

        private static string RenderThreshold(RunningCost c, long value)
        {
            if (c.DescribeThreshold != null)
            {
                try { return c.DescribeThreshold(value); }
                catch (Exception ex) { TraceProbeFailure(c, "threshold text", ex); }
            }
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static void TraceProbeFailure(RunningCost c, string what, Exception ex)
        {
            try
            {
                Tracing.TraceLine(
                    "RunningCostRegister: " + c.Id + " " + what + " failed: " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
            }
            catch
            {
                // Tracing is one of the things this register reports on. If it
                // is the thing that is broken, there is nowhere left to say so.
            }
        }

        private sealed class Token : IDisposable
        {
            private string? _id;
            internal Token(string id) => _id = id;

            public void Dispose()
            {
                string? id = _id;
                _id = null;
                if (id != null) Unregister(id);
            }
        }
    }
}
