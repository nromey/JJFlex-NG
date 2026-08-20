using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JJFlex.RigSurface
{
    /// <summary>What happened to one field when the harness tried to put it back.</summary>
    public enum RestoreStatus
    {
        /// <summary>Never moved. Nothing to do.</summary>
        Unchanged,

        /// <summary>Moved, was written back, and the radio confirmed the old value.</summary>
        Restored,

        /// <summary>Moved, was written back, and the radio did NOT come back to the old value.</summary>
        DidNotStick,

        /// <summary>Moved, and the radio refused the write.</summary>
        Refused,

        /// <summary>Moved, but there is no write path for it. Reported, not attempted.</summary>
        NotWritable,

        /// <summary>Belongs to another client. Never written, on principle.</summary>
        ForeignObject,

        /// <summary>The object no longer exists, so the field cannot be put back.</summary>
        ObjectGone,

        /// <summary>A slice the harness created, released again.</summary>
        CreatedObjectRemoved,

        /// <summary>Something threw while restoring this field.</summary>
        Error,
    }

    /// <summary>One line of the restore report.</summary>
    public sealed record RestoreOutcome(
        RigField Field,
        string? From,
        string? To,
        RestoreStatus Status,
        string Detail)
    {
        public bool IsProblem => Status is RestoreStatus.DidNotStick
                                        or RestoreStatus.Refused
                                        or RestoreStatus.NotWritable
                                        or RestoreStatus.ObjectGone
                                        or RestoreStatus.Error;
    }

    /// <summary>The result of a restore pass.</summary>
    public sealed class RestoreReport
    {
        private readonly List<RestoreOutcome> _outcomes = new();

        public IReadOnlyList<RestoreOutcome> Outcomes => _outcomes;

        public int RestoredCount => _outcomes.Count(o => o.Status == RestoreStatus.Restored);

        public int ProblemCount => _outcomes.Count(o => o.IsProblem);

        /// <summary>True when the radio was left exactly as it was found.</summary>
        public bool Clean => ProblemCount == 0;

        internal void Add(RestoreOutcome outcome) => _outcomes.Add(outcome);

        /// <summary>
        /// Plain text, bullets, no table. Everything this project prints ends up
        /// being read aloud by a screen reader.
        /// </summary>
        public string ToPlainText(bool includeUnchanged = false)
        {
            var lines = new List<string>();

            IEnumerable<RestoreOutcome> interesting = includeUnchanged
                ? _outcomes
                : _outcomes.Where(o => o.Status != RestoreStatus.Unchanged);

            var shown = interesting.ToList();

            lines.Add(Clean
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Restore clean. {RestoredCount} field(s) put back, nothing left changed.")
                : string.Create(CultureInfo.InvariantCulture,
                    $"Restore INCOMPLETE. {RestoredCount} field(s) put back, {ProblemCount} problem(s) below. " +
                    $"The radio is not exactly as it was found."));

            if (shown.Count == 0)
            {
                lines.Add("Nothing had changed, so nothing needed restoring.");
                return string.Join(Environment.NewLine, lines);
            }

            foreach (RestoreOutcome o in shown.OrderBy(o => o.Status).ThenBy(o => o.Field.ToString(), StringComparer.Ordinal))
            {
                string movement = o.From is null && o.To is null
                    ? ""
                    : $" was '{o.From ?? "(absent)"}', found '{o.To ?? "(absent)"}'.";
                lines.Add($"  - {o.Field}: {Describe(o.Status)}.{movement} {o.Detail}".TrimEnd());
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string Describe(RestoreStatus status) => status switch
        {
            RestoreStatus.Unchanged => "unchanged",
            RestoreStatus.Restored => "restored",
            RestoreStatus.DidNotStick => "WRITE DID NOT STICK",
            RestoreStatus.Refused => "THE RADIO REFUSED THE WRITE",
            RestoreStatus.NotWritable => "CHANGED AND CANNOT BE PUT BACK — no write path exists",
            RestoreStatus.ForeignObject => "belongs to another client, left alone",
            RestoreStatus.ObjectGone => "THE OBJECT NO LONGER EXISTS",
            RestoreStatus.CreatedObjectRemoved => "created by this run, released again",
            RestoreStatus.Error => "ERRORED WHILE RESTORING",
            _ => status.ToString(),
        };
    }

    /// <summary>Knobs for a capture. Defaults are the safe ones.</summary>
    public sealed record RigStateScopeOptions
    {
        /// <summary>Restrict what gets captured. Null captures everything.</summary>
        public Func<RigField, bool>? Include { get; init; }

        /// <summary>How long to wait for the radio to confirm a restored value.</summary>
        public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long to wait for the radio to finish its initial status dump
        /// before considering the snapshot complete.
        /// </summary>
        public TimeSpan SettleQuietFor { get; init; } = TimeSpan.FromMilliseconds(400);

        /// <summary>Upper bound on settling.</summary>
        public TimeSpan SettleMax { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Where the restore report goes if it is not clean. Defaults to standard
        /// error, so a failed restore is loud even when nobody reads the return
        /// value.
        /// </summary>
        public Action<string>? Report { get; init; }
    }

    /// <summary>
    /// Snapshot the radio, do something, put the radio back — including when the
    /// something throws.
    ///
    /// <para><b>This is Noel's own station.</b> A harness that abandons a
    /// half-configured radio is worse than no harness at all, because the next
    /// person to key it does not know what changed. So restore is a
    /// <see cref="IDisposable"/> scope rather than a call at the end of a happy
    /// path: the only way to skip it is to skip the using statement.</para>
    ///
    /// <para><b>Restore is verified, not assumed.</b> Every write is followed by
    /// waiting for the RADIO to report the old value back. A write that the
    /// radio silently ignores — and several of them exist, see the notes in
    /// <see cref="OwnershipTable"/> — is reported as DID NOT STICK rather than
    /// counted as a success.</para>
    ///
    /// <para><b>What it will not do.</b> It will not write a field that has no
    /// documented write path, it will not touch an object belonging to another
    /// client, and it will not recreate a slice that vanished. That last one is
    /// deliberate and worth stating: slice changes are known not to persist, a
    /// released slice comes back on reconnect from the radio's own global
    /// profile, and the harness must not "fix" that by writing a profile.
    /// Writing station profiles is not this tool's business.</para>
    /// </summary>
    public sealed class RigStateScope : IDisposable
    {
        private readonly RigStateScopeOptions _options;
        private readonly List<int> _createdSlices = new();
        private RestoreReport? _report;

        private RigStateScope(RigWire wire, IReadOnlyDictionary<RigField, string> before, RigStateScopeOptions options)
        {
            Wire = wire;
            Before = before;
            _options = options;
        }

        public RigWire Wire { get; }

        /// <summary>The radio's state at capture time, exactly as the radio reported it.</summary>
        public IReadOnlyDictionary<RigField, string> Before { get; }

        /// <summary>True once <see cref="Restore"/> has run.</summary>
        public bool AlreadyRestored => _report is not null;

        /// <summary>
        /// Captures everything the radio has told us about itself.
        ///
        /// <para>Subscribes if needed, waits for the status stream to go quiet,
        /// then flattens the model. Note that re-subscribing does NOT make the
        /// radio resend its full state — subscriptions emit once and deltas
        /// afterwards — so the snapshot is only as complete as the live model,
        /// which is why settling matters.</para>
        /// </summary>
        public static RigStateScope Capture(RigWire wire, RigStateScopeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(wire);
            options ??= new RigStateScopeOptions();

            wire.SubscribeAll();
            wire.Settle(options.SettleQuietFor, options.SettleMax);

            IReadOnlyDictionary<RigField, string> all = wire.State.Flatten();
            Dictionary<RigField, string> captured = options.Include is null
                ? new Dictionary<RigField, string>(all)
                : all.Where(kv => options.Include(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

            return new RigStateScope(wire, captured, options);
        }

        /// <summary>
        /// Tells the scope that this run created a slice, so restore releases it.
        /// Call it immediately after the create succeeds, not at the end.
        /// </summary>
        public void TrackCreatedSlice(int index)
        {
            if (!_createdSlices.Contains(index)) _createdSlices.Add(index);
        }

        /// <summary>
        /// Puts the radio back. Idempotent — calling it twice returns the first
        /// report rather than writing anything again.
        /// </summary>
        public RestoreReport Restore()
        {
            if (_report is not null) return _report;

            var report = new RestoreReport();
            _report = report;

            try
            {
                ReleaseCreatedSlices(report);
                RestoreCompositePairs(report);
                RestoreSimpleFields(report);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                report.Add(new RestoreOutcome(
                    default, null, null, RestoreStatus.Error,
                    $"The restore pass itself failed: {ex.Message}. The radio may not be as it was found."));
            }

            if (!report.Clean)
            {
                Action<string> sink = _options.Report ?? Console.Error.WriteLine;
                sink(report.ToPlainText());
            }

            return report;
        }

        private void ReleaseCreatedSlices(RestoreReport report)
        {
            foreach (int index in _createdSlices)
            {
                var marker = RigField.Slice(index, "in_use");
                try
                {
                    WireReply reply = Wire.Send(string.Create(CultureInfo.InvariantCulture, $"slice remove {index}"));
                    if (!reply.Ok)
                    {
                        report.Add(new RestoreOutcome(marker, "created by this run", "still present",
                            RestoreStatus.Refused, $"The radio refused the release: {reply.Code} {reply.Message}."));
                        continue;
                    }

                    bool gone = Wire.WaitFor(marker, v => v is null, _options.VerifyTimeout);
                    report.Add(new RestoreOutcome(marker, "created by this run", gone ? "released" : "still present",
                        gone ? RestoreStatus.CreatedObjectRemoved : RestoreStatus.DidNotStick,
                        gone ? "" : "The radio accepted the release but the slice is still reported."));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    report.Add(new RestoreOutcome(marker, "created by this run", null, RestoreStatus.Error, ex.Message));
                }
            }
        }

        /// <summary>
        /// Filter edges must be written as a pair. Writing them one at a time has
        /// a genuine failure mode: the intermediate state is inverted, the write
        /// is dropped, and the passband is left wrong with nothing reported.
        /// </summary>
        private void RestoreCompositePairs(RestoreReport report)
        {
            foreach ((RigTarget target, string lowKey, string highKey) in OwnershipTable.CompositePairs)
            {
                foreach (int index in IndicesFor(target))
                {
                    var lowField = new RigField(target, index, lowKey);
                    var highField = new RigField(target, index, highKey);

                    if (!Before.TryGetValue(lowField, out string? wasLow)) continue;
                    if (!Before.TryGetValue(highField, out string? wasHigh)) continue;

                    string? nowLow = Wire.State.Get(lowField);
                    string? nowHigh = Wire.State.Get(highField);

                    bool moved = !string.Equals(wasLow, nowLow, StringComparison.Ordinal)
                              || !string.Equals(wasHigh, nowHigh, StringComparison.Ordinal);

                    if (!moved)
                    {
                        report.Add(new RestoreOutcome(lowField, wasLow, nowLow, RestoreStatus.Unchanged, ""));
                        report.Add(new RestoreOutcome(highField, wasHigh, nowHigh, RestoreStatus.Unchanged, ""));
                        continue;
                    }

                    if (!MayWrite(target, index, out string refusal))
                    {
                        report.Add(new RestoreOutcome(lowField, wasLow, nowLow, RestoreStatus.ForeignObject, refusal));
                        continue;
                    }

                    try
                    {
                        WireReply reply = Wire.Send(OwnershipTable.CompositeCommand(target, index, wasLow, wasHigh));
                        if (!reply.Ok)
                        {
                            report.Add(new RestoreOutcome(lowField, wasLow, nowLow, RestoreStatus.Refused,
                                $"Filter pair write refused: {reply.Code} {reply.Message}."));
                            continue;
                        }

                        bool lowOk = Wire.WaitForValue(lowField, wasLow, _options.VerifyTimeout);
                        bool highOk = Wire.WaitForValue(highField, wasHigh, _options.VerifyTimeout);

                        report.Add(new RestoreOutcome(lowField, wasLow, Wire.State.Get(lowField),
                            lowOk ? RestoreStatus.Restored : RestoreStatus.DidNotStick, ""));
                        report.Add(new RestoreOutcome(highField, wasHigh, Wire.State.Get(highField),
                            highOk ? RestoreStatus.Restored : RestoreStatus.DidNotStick, ""));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        report.Add(new RestoreOutcome(lowField, wasLow, nowLow, RestoreStatus.Error, ex.Message));
                    }
                }
            }
        }

        private void RestoreSimpleFields(RestoreReport report)
        {
            var composites = OwnershipTable.CompositePairs
                .SelectMany(p => new[] { (p.Target, p.LowKey), (p.Target, p.HighKey) })
                .ToHashSet();

            foreach (KeyValuePair<RigField, string> entry in Before)
            {
                RigField field = entry.Key;
                string was = entry.Value;

                if (composites.Contains((field.Target, field.Key))) continue;

                string? now = Wire.State.Get(field);
                if (string.Equals(was, now, StringComparison.Ordinal))
                {
                    report.Add(new RestoreOutcome(field, was, now, RestoreStatus.Unchanged, ""));
                    continue;
                }

                RigFieldSpec spec = OwnershipTable.Lookup(field);

                if (spec.Ownership == StateOwnership.Telemetry)
                {
                    // Telemetry moving is the radio doing its job, not damage.
                    report.Add(new RestoreOutcome(field, was, now, RestoreStatus.Unchanged,
                        "Telemetry — the radio reporting on itself, nothing to restore."));
                    continue;
                }

                if (now is null && Wire.State.GetObject(field.Target, field.Index) is null)
                {
                    report.Add(new RestoreOutcome(field, was, null, RestoreStatus.ObjectGone,
                        "The object it lived on is gone. Not recreated on purpose — releasing and recreating " +
                        "slices is the operator's business, not the harness's."));
                    continue;
                }

                if (!MayWrite(field.Target, field.Index, out string refusal))
                {
                    report.Add(new RestoreOutcome(field, was, now, RestoreStatus.ForeignObject, refusal));
                    continue;
                }

                string? command = OwnershipTable.SetCommand(field, was);
                if (command is null)
                {
                    report.Add(new RestoreOutcome(field, was, now, RestoreStatus.NotWritable, spec.Notes));
                    continue;
                }

                try
                {
                    WireReply reply = Wire.Send(command);
                    if (!reply.Ok)
                    {
                        report.Add(new RestoreOutcome(field, was, now, RestoreStatus.Refused,
                            $"The radio refused '{command}': {reply.Code} {reply.Message}."));
                        continue;
                    }

                    bool ok = Wire.WaitForValue(field, was, _options.VerifyTimeout);
                    report.Add(new RestoreOutcome(field, was, Wire.State.Get(field),
                        ok ? RestoreStatus.Restored : RestoreStatus.DidNotStick,
                        ok ? "" : $"Sent '{command}' and the radio accepted it, but never reported the old value back."));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    report.Add(new RestoreOutcome(field, was, now, RestoreStatus.Error, ex.Message));
                }
            }
        }

        /// <summary>
        /// Never write to an object owned by somebody else. Station-global
        /// objects have no owner and pass; client-owned objects must be ours.
        /// </summary>
        private bool MayWrite(RigTarget target, int index, out string refusal)
        {
            RigObject? obj = Wire.State.GetObject(target, index);
            if (obj is null)
            {
                refusal = "";
                return true;
            }

            if (Guards.IsOurs(Wire, obj))
            {
                refusal = "";
                return true;
            }

            refusal = $"Owned by client {obj.OwnerHandle}, not by us ({Wire.ClientHandle}). " +
                      "The harness never writes another client's objects.";
            return false;
        }

        private IEnumerable<int> IndicesFor(RigTarget target)
        {
            var seen = new HashSet<int>();
            foreach (RigField field in Before.Keys)
            {
                if (field.Target == target) seen.Add(field.Index);
            }
            return seen;
        }

        /// <summary>
        /// Restores. Never throws, because this frequently runs while an
        /// exception is already unwinding and masking the original failure would
        /// hide the reason the run went wrong in the first place.
        /// </summary>
        public void Dispose()
        {
            try
            {
                Restore();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Console.Error.WriteLine(
                    "The restore pass threw while disposing, which means the radio may be left changed: " + ex.Message);
            }
        }
    }
}
