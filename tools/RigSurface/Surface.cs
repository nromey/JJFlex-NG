using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace JJFlex.RigSurface
{
    /// <summary>How a single surface check came out.</summary>
    public enum CheckResult
    {
        /// <summary>The radio reported the commanded value back. The chain works.</summary>
        Took,

        /// <summary>The radio accepted the command and never reported the new value.</summary>
        DidNotTake,

        /// <summary>The radio refused the command outright.</summary>
        Refused,

        /// <summary>
        /// The field could not be read at all, so nothing could be asserted.
        /// Reported as its own outcome and never folded into a pass.
        /// </summary>
        Unobservable,

        /// <summary>Not attempted, with a reason.</summary>
        Skipped,
    }

    /// <summary>One assertion about one field.</summary>
    public sealed record SurfaceCheck(
        RigField Field,
        string? Before,
        string? Commanded,
        string? After,
        CheckResult Result,
        StateOwnership Ownership,
        string Detail);

    /// <summary>
    /// The non-transmitting surface: command it, read the radio's own state
    /// back, assert it changed.
    ///
    /// <para><b>Two modes, and the difference is the point.</b></para>
    ///
    /// <para><b>Observe</b> changes nothing. It records what the radio reports,
    /// attributed to the client that owns each object. This is the half of the
    /// composed test that matters: the UI driver presses a key in the real
    /// running application, and this asks the radio whether it happened. Because
    /// slice state is globally observable and only privately owned, this
    /// verifies the APPLICATION'S slices honestly, from a connection that never
    /// writes anything.</para>
    ///
    /// <para><b>Exercise</b> drives the radio from our own connection. It is
    /// strictly weaker and it says so in its own output. For station-global
    /// state it proves the real thing, because there is only one mic gain and
    /// one transmit power. For anything living on a slice it proves only that
    /// the RADIO accepts our command vocabulary — the slice under test is ours,
    /// not the application's, so a green result says nothing whatsoever about
    /// whether JJ Flexible can change a mode. That distinction is the trap this
    /// track was warned about, and it is handled by labelling rather than by
    /// pretending.</para>
    /// </summary>
    internal static class Surface
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("surface needs a subcommand: watch, mark, diff, await, or exercise.");
                return 2;
            }

            return args[0].ToLowerInvariant() switch
            {
                "watch" => Watch(args),
                "mark" => Mark(args),
                "diff" => Diff(args),
                "await" => Await(args),
                "exercise" => Exercise(args),
                _ => Bad(args[0]),
            };
        }

        private static int Bad(string sub)
        {
            Console.Error.WriteLine($"Unknown surface subcommand '{sub}'.");
            return 2;
        }

        // ================================================================ //
        // OBSERVE — the seam with the UI driver. Changes nothing.
        // ================================================================ //

        /// <summary>
        /// Records every change the radio reports for a while, timestamped.
        ///
        /// <para>This is the seam offered to the track that drives the live
        /// application. It deliberately does NOT require the two processes to
        /// take turns: the driver presses keys and logs what it pressed with a
        /// timestamp, this records what the radio said with a timestamp, and the
        /// two are correlated afterwards. One radio connection covers a whole
        /// key sweep, and neither side blocks on the other.</para>
        /// </summary>
        private static int Watch(string[] args)
        {
            double seconds = ParseDouble(Program.Option(args, "--seconds"), 60);
            string? outPath = Program.Option(args, "--out");

            using RigWire wire = Program.Open(args);
            Console.WriteLine();
            Console.WriteLine(Guards.DescribeCensus(wire));
            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Watching for {seconds:F0} seconds. Nothing is being changed by this process."));
            Console.WriteLine("Timestamps are milliseconds since this line.");
            Console.WriteLine();

            IReadOnlyDictionary<RigField, string> previous = wire.State.Flatten();
            var start = DateTimeOffset.UtcNow;
            using TextWriter? file = outPath is null ? null : new StreamWriter(outPath, append: false);

            void Emit(string line)
            {
                Console.WriteLine(line);
                file?.WriteLine(line);
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
                IReadOnlyDictionary<RigField, string> now = wire.State.Flatten();

                foreach (KeyValuePair<RigField, string> kv in now)
                {
                    previous.TryGetValue(kv.Key, out string? was);
                    if (string.Equals(was, kv.Value, StringComparison.Ordinal)) continue;

                    double ms = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                    string owner = OwnerOf(wire, kv.Key);
                    Emit(string.Create(CultureInfo.InvariantCulture,
                        $"{ms,9:F0}  {kv.Key} : {was ?? "(absent)"} -> {kv.Value}   [{owner}]"));
                }

                foreach (RigField gone in previous.Keys.Where(k => !now.ContainsKey(k)))
                {
                    double ms = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                    Emit(string.Create(CultureInfo.InvariantCulture,
                        $"{ms,9:F0}  {gone} : {previous[gone]} -> (gone)"));
                }

                previous = now;
            }

            Console.WriteLine();
            Console.WriteLine("Watch finished. The radio was not touched.");
            return 0;
        }

        /// <summary>Writes the radio's whole observable state to a file.</summary>
        private static int Mark(string[] args)
        {
            string path = Program.Option(args, "--out")
                          ?? throw new HarnessRefusedException("surface mark needs --out <file>.");

            using RigWire wire = Program.Open(args);
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<RigField, string> kv in wire.State.Flatten())
            {
                snapshot[kv.Key.ToString()] = kv.Value;
            }

            var payload = new MarkFile
            {
                TakenAt = DateTimeOffset.Now,
                Host = wire.Host,
                ObserverHandle = wire.ClientHandle,
                Owners = wire.State.AllObjects()
                    .Where(o => o.OwnerHandle is not null)
                    .ToDictionary(o => $"{o.Target.ToString().ToLowerInvariant()}.{o.Index}", o => o.OwnerHandle!, StringComparer.Ordinal),
                Clients = Guards.Census(wire).ToDictionary(c => c.Handle, c => c.Program ?? "(unnamed)", StringComparer.Ordinal),
                Fields = snapshot,
            };

            File.WriteAllText(path, JsonSerializer.Serialize(payload, Json));
            Console.WriteLine($"Marked {snapshot.Count} fields to {path}. The radio was not touched.");
            return 0;
        }

        /// <summary>Reports what moved since a mark, and whose objects moved.</summary>
        private static int Diff(string[] args)
        {
            string path = Program.Option(args, "--since")
                          ?? throw new HarnessRefusedException("surface diff needs --since <file>.");
            string? ownerFilter = Program.Option(args, "--owner");

            MarkFile before = JsonSerializer.Deserialize<MarkFile>(File.ReadAllText(path))
                              ?? throw new HarnessRefusedException($"Could not read a mark file from {path}.");

            using RigWire wire = Program.Open(args);

            string? wantedHandle = ResolveOwnerFilter(wire, ownerFilter);

            var changes = new List<string>();
            IReadOnlyDictionary<RigField, string> now = wire.State.Flatten();

            foreach (KeyValuePair<RigField, string> kv in now.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
            {
                string key = kv.Key.ToString();
                before.Fields.TryGetValue(key, out string? was);
                if (string.Equals(was, kv.Value, StringComparison.Ordinal)) continue;

                string owner = OwnerOf(wire, kv.Key);
                if (wantedHandle is not null
                    && !owner.Contains(wantedHandle, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                changes.Add($"  - {key}: {was ?? "(absent)"} -> {kv.Value}   [{owner}]");
            }

            foreach (string key in before.Fields.Keys.Where(k => !now.Any(n => string.Equals(n.Key.ToString(), k, StringComparison.Ordinal))))
            {
                changes.Add($"  - {key}: {before.Fields[key]} -> (gone)");
            }

            Console.WriteLine();
            if (changes.Count == 0)
            {
                Console.WriteLine(wantedHandle is null
                    ? "Nothing the radio reports has changed since the mark."
                    : $"Nothing owned by {wantedHandle} has changed since the mark.");
                Console.WriteLine();
                Console.WriteLine("If a key was pressed and this says nothing moved, the interesting possibilities are: " +
                                  "the key never reached a handler, the handler never reached the radio, or the change " +
                                  "is one the radio does not report. The third is real — several DSP levels are writable " +
                                  "with no status key at all.");
                return 0;
            }

            Console.WriteLine($"{changes.Count} field(s) changed since the mark:");
            foreach (string line in changes) Console.WriteLine(line);
            return 0;
        }

        /// <summary>
        /// Blocks until the radio reports a field at a value. The synchronous
        /// primitive, for a driver that would rather wait than correlate.
        /// </summary>
        private static int Await(string[] args)
        {
            string spec = Program.Option(args, "--field")
                          ?? throw new HarnessRefusedException("surface await needs --field <target.index.key>.");
            string? equals = Program.Option(args, "--equals");
            double timeoutMs = ParseDouble(Program.Option(args, "--timeout"), 5000);

            if (!RigField.TryParse(spec, out RigField field))
            {
                throw new HarnessRefusedException($"Could not read '{spec}' as a field. Try slice.0.mode or transmit.mic_level.");
            }

            using RigWire wire = Program.Open(args);
            string? start = wire.State.Get(field);

            bool ok = equals is null
                ? wire.WaitForChange(field, start, TimeSpan.FromMilliseconds(timeoutMs))
                : wire.WaitForValue(field, equals, TimeSpan.FromMilliseconds(timeoutMs));

            string? final = wire.State.Get(field);
            Console.WriteLine(ok
                ? $"{field} is now '{final}'."
                : $"{field} never reached the expected value within the timeout. It reads '{final ?? "(absent)"}'.");
            return ok ? 0 : 1;
        }

        private static string? ResolveOwnerFilter(RigWire wire, string? filter)
        {
            if (filter is null) return null;
            if (filter.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return RigWire.NormaliseHandle(filter);

            ConnectedClient? client = Guards.FindClientByProgram(wire, filter);
            if (client is null)
            {
                throw new HarnessRefusedException(
                    $"No connected client's program name contains '{filter}'. Connected right now: " +
                    string.Join("; ", Guards.Census(wire).Select(c => c.Describe())));
            }
            return client.Handle;
        }

        private static string OwnerOf(RigWire wire, RigField field)
        {
            RigObject? obj = wire.State.GetObject(field.Target, field.Index);
            if (obj?.OwnerHandle is null) return "station";
            string handle = RigWire.NormaliseHandle(obj.OwnerHandle);
            if (string.Equals(handle, wire.ClientHandle, StringComparison.OrdinalIgnoreCase)) return "us " + handle;

            ConnectedClient? client = Guards.Census(wire)
                .FirstOrDefault(c => string.Equals(c.Handle, handle, StringComparison.OrdinalIgnoreCase));
            return client?.Program is null ? handle : $"{client.Program} {handle}";
        }

        // ================================================================ //
        // EXERCISE — drives the radio. Refuses under another operator.
        // ================================================================ //

        private static int Exercise(string[] args)
        {
            bool dryRun = Program.Flag(args, "--dry-run");

            if (dryRun)
            {
                PrintPlan();
                return 0;
            }

            using RigWire wire = Program.Open(args);

            Console.WriteLine();
            Console.WriteLine(Guards.DescribeCensus(wire));

            // Both guards, in this order, before anything is written.
            Guards.RequireSoleOperator(wire);
            Guards.RequireNotTransmitting(wire);

            var checks = new List<SurfaceCheck>();

            using (var scope = RigStateScope.Capture(wire))
            {
                Console.WriteLine();
                Console.WriteLine($"Snapshot holds {scope.Before.Count} fields. Everything below is restored afterwards.");
                Console.WriteLine();

                ExerciseStationGlobal(wire, checks);
                ExerciseOwnSlice(wire, scope, checks);

                Console.WriteLine();
                Console.WriteLine("Restoring.");
                RestoreReport report = scope.Restore();
                Console.WriteLine(report.ToPlainText());
            }

            Console.WriteLine();
            PrintCheckReport(checks);

            return checks.Any(c => c.Result is CheckResult.DidNotTake or CheckResult.Refused) ? 1 : 0;
        }

        private static void PrintPlan()
        {
            Console.WriteLine("What 'surface exercise' would do, without doing any of it.");
            Console.WriteLine();
            Console.WriteLine("Before anything: refuse if any other client is connected, refuse if the radio is");
            Console.WriteLine("transmitting or if transmit state cannot be read, then snapshot everything.");
            Console.WriteLine();
            Console.WriteLine("Station-global fields, where driving the radio ourselves proves the real thing,");
            Console.WriteLine("because there is exactly one of each on the whole radio:");
            foreach ((RigField field, string _, string why) in GlobalPlan())
            {
                Console.WriteLine($"  - {field}: {why}");
            }
            Console.WriteLine();
            Console.WriteLine("Then it creates a slice of its own and walks the receiver surface on it: mode,");
            Console.WriteLine("filter edges, AGC, noise blanker, noise reduction, automatic notch, RIT, XIT,");
            Console.WriteLine("receive antenna, tuning step and audio level.");
            Console.WriteLine();
            Console.WriteLine("READ THE LABEL ON THAT SECOND PART. The slice is OURS. A green result there says");
            Console.WriteLine("the radio accepts our command vocabulary and nothing at all about whether JJ");
            Console.WriteLine("Flexible can change a mode. Per-client state is only honestly tested by pressing");
            Console.WriteLine("the key in the real application and observing the application's slice, which is");
            Console.WriteLine("what 'surface watch' and 'surface diff' exist for.");
            Console.WriteLine();
            Console.WriteLine("Not exercised, and why:");
            Console.WriteLine("  - Band and RF gain live on a panadapter, and creating a panadapter of our own");
            Console.WriteLine("    is a much larger intrusion than creating a slice. Observed, never driven.");
            Console.WriteLine("  - Split and VFO A/B are not radio concepts at all. They are application");
            Console.WriteLine("    constructs over two slices, so there is nothing on the wire to assert.");
            Console.WriteLine("  - VOX, because enabling it can key the radio without anyone asking.");
            Console.WriteLine("  - The antenna tuner, because starting it transmits. It lives in the transmit");
            Console.WriteLine("    harness with a relay budget.");
        }

        /// <summary>
        /// Station-global fields worth exercising, with the value to command and
        /// why it is safe. Every one of these is reversible and none of them
        /// affects whether the radio can transmit.
        /// </summary>
        private static IEnumerable<(RigField Field, string Value, string Why)> GlobalPlan()
        {
            yield return (RigField.Radio("nickname"), "RigSurfaceProbe",
                "cosmetic, instantly reversible, and the safest possible proof that the write path works at all");
            yield return (RigField.Transmit("mic_level"), "42",
                "mic gain — reads mic_level and writes miclevel, so it also proves the two vocabularies are wired correctly");
            yield return (RigField.Transmit("compander"), "1", "companding on or off");
            yield return (RigField.Transmit("compander_level"), "55", "companding depth");
            yield return (RigField.Transmit("speech_processor_enable"), "1", "speech processor");
            yield return (RigField.Transmit("speech_processor_level"), "2", "speech processor depth");
            yield return (RigField.Transmit("sb_monitor"), "1",
                "transmit monitor — reads sb_monitor and writes mon, the other vocabulary mismatch worth proving");
            yield return (RigField.Transmit("mon_gain_sb"), "33", "monitor level");
            yield return (RigField.Transmit("am_carrier_level"), "70",
                "AM carrier — reads am_carrier_level and writes am_carrier");
            yield return (RigField.Transmit("tunepower"), "10",
                "tune power, which is a setting and not a transmission");
            yield return (RigField.Transmit("pitch"), "700",
                "CW pitch — reported under transmit, written with the top-level cw verb");
            yield return (RigField.Transmit("speed"), "22",
                "keyer speed — reads speed, writes cw wpm");
            yield return (RigField.Atu("memories_enabled"), "1",
                "the one tuner setting that does not transmit");
            yield return (RigField.Radio("tnf_enabled"), "1", "tracking notch filters, station-wide");
            yield return (RigField.Radio("binaural_rx"), "1", "binaural receive");
        }

        private static void ExerciseStationGlobal(RigWire wire, List<SurfaceCheck> checks)
        {
            Console.WriteLine("Station-global surface. These are the real thing: one value for the whole radio.");
            Console.WriteLine();

            foreach ((RigField field, string wanted, string _) in GlobalPlan())
            {
                checks.Add(RunCheck(wire, field, wanted));
            }
        }

        private static void ExerciseOwnSlice(RigWire wire, RigStateScope scope, List<SurfaceCheck> checks)
        {
            Console.WriteLine();
            Console.WriteLine("Receiver surface, on a slice of OUR OWN. Command-path verification only — this");
            Console.WriteLine("proves the radio accepts these commands, NOT that the application can issue them.");
            Console.WriteLine();

            Guards.RequireNotTransmitting(wire);

            HashSet<int> beforeIndices = wire.State.GetObjects(RigTarget.Slice).Select(s => s.Index).ToHashSet();

            WireReply create;
            try
            {
                create = wire.Send("slice create freq=14.100 mode=USB");
            }
            catch (TimeoutException)
            {
                checks.Add(new SurfaceCheck(RigField.Slice(-1, "create"), null, null, null, CheckResult.Unobservable,
                    StateOwnership.ClientOwned, "The radio never answered 'slice create'."));
                return;
            }

            if (!create.Ok)
            {
                checks.Add(new SurfaceCheck(RigField.Slice(-1, "create"), null, null, null, CheckResult.Refused,
                    StateOwnership.ClientOwned,
                    $"The radio refused 'slice create': {create.Code} {create.Message}. " +
                    "This is a legitimate answer, not a bug — this harness never registers as a GUI client, " +
                    "and a radio may well decline to give slices to one that has not. If so, per-client state " +
                    "can ONLY be verified through the running application, which is the composed mode."));
                Console.WriteLine("  The radio would not give us a slice. Skipping the receiver surface.");
                Console.WriteLine("  " + create.Message);
                return;
            }

            wire.Settle(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3));

            int? ours = wire.State.GetObjects(RigTarget.Slice)
                .Where(s => !beforeIndices.Contains(s.Index) && Guards.IsOurs(wire, s))
                .Select(s => (int?)s.Index)
                .FirstOrDefault();

            if (ours is null)
            {
                checks.Add(new SurfaceCheck(RigField.Slice(-1, "create"), null, null, null, CheckResult.Unobservable,
                    StateOwnership.ClientOwned,
                    "'slice create' was accepted but no new slice carrying our client handle ever appeared."));
                return;
            }

            int index = ours.Value;
            scope.TrackCreatedSlice(index);
            Console.WriteLine($"  Working on slice {index}, which is ours and will be released afterwards.");
            Console.WriteLine();

            // Filter edges are a pair and must be written as one command.
            checks.Add(RunFilterCheck(wire, index, "-2700", "-300"));

            // Antenna selection reads the radio's own legal list first.
            checks.Add(AntennaCheck(wire, index));

            foreach ((string key, string value) in SlicePlan())
            {
                checks.Add(RunCheck(wire, RigField.Slice(index, key), value));
            }
        }

        private static IEnumerable<(string Key, string Value)> SlicePlan()
        {
            yield return ("mode", "CW");
            yield return ("agc_mode", "fast");
            yield return ("agc_threshold", "65");
            yield return ("nb", "1");
            yield return ("nb_level", "40");
            yield return ("wnb", "1");
            yield return ("wnb_level", "35");
            yield return ("nr", "1");
            yield return ("nr_level", "30");
            yield return ("anf", "1");
            yield return ("anf_level", "25");
            yield return ("apf", "1");
            yield return ("apf_level", "20");
            yield return ("rnn", "1");
            yield return ("nrl", "1");
            yield return ("anfl", "1");
            yield return ("nrs", "1");
            yield return ("nrf", "1");
            yield return ("rit_on", "1");
            yield return ("rit_freq", "250");
            yield return ("xit_on", "1");
            yield return ("xit_freq", "-250");
            yield return ("step", "100");
            yield return ("audio_level", "45");
            yield return ("audio_pan", "40");
            yield return ("squelch", "1");
            yield return ("squelch_level", "30");
            yield return ("RF_frequency", "14.200000");

            // Frequency lock goes LAST on purpose. A locked slice refuses to
            // retune, so locking it before the frequency check would fail that
            // check for a reason that has nothing to do with the tune path.
            yield return ("lock", "1");
        }

        /// <summary>
        /// Antenna selection, using the legal values the RADIO reports rather
        /// than a name we invented.
        ///
        /// <para>The antenna list is per slice and per SCU and differs by model,
        /// so guessing a name produces a refusal that looks like a broken
        /// command path when it is really a wrong argument. Reading the list
        /// first is both more honest and the only way to get a meaningful
        /// answer.</para>
        /// </summary>
        private static SurfaceCheck AntennaCheck(RigWire wire, int index)
        {
            var field = RigField.Slice(index, "rxant");
            string? legal = wire.State.Get(RigField.Slice(index, "ant_list"));
            string? current = wire.State.Get(field);

            if (string.IsNullOrEmpty(legal))
            {
                return Report(new SurfaceCheck(field, current, null, current, CheckResult.Unobservable,
                    StateOwnership.ClientOwned,
                    "The radio did not report an antenna list for this slice, so there is no legal value to " +
                    "command. Guessing one would produce a refusal that looks like a broken command path."));
            }

            string? other = legal.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .FirstOrDefault(a => !string.Equals(a, current, StringComparison.OrdinalIgnoreCase));

            if (other is null)
            {
                return Report(new SurfaceCheck(field, current, null, current, CheckResult.Skipped,
                    StateOwnership.ClientOwned,
                    $"The radio offers only one antenna ({legal}), so there is nothing to switch to."));
            }

            return RunCheck(wire, field, other);
        }

        /// <summary>
        /// One check. Read the radio, command a change, wait for the RADIO to
        /// report the new value, and say honestly which of the four things
        /// happened.
        ///
        /// <para>The transmit guard runs inside here rather than once at the top,
        /// because a run walks dozens of fields over a minute or more and the
        /// operator can pick up a microphone at any point during it.</para>
        /// </summary>
        private static SurfaceCheck RunCheck(RigWire wire, RigField field, string wanted)
        {
            Guards.RequireNotTransmitting(wire);

            RigFieldSpec spec = OwnershipTable.Lookup(field);
            string? before = wire.State.Get(field);

            if (before is null)
            {
                return Report(new SurfaceCheck(field, null, wanted, null, CheckResult.Unobservable, spec.Ownership,
                    "The radio has never reported this field, so there is nothing to compare against. " +
                    "Not a pass and not a failure."));
            }

            // Commanding the value it already holds would prove nothing, so move
            // it somewhere else and say so.
            string target = string.Equals(before, wanted, StringComparison.OrdinalIgnoreCase)
                ? Nudge(before)
                : wanted;

            string? command = OwnershipTable.SetCommand(field, target);
            if (command is null)
            {
                return Report(new SurfaceCheck(field, before, null, before, CheckResult.Skipped, spec.Ownership,
                    "No write path. " + spec.Notes));
            }

            WireReply reply;
            try
            {
                reply = wire.Send(command);
            }
            catch (TimeoutException)
            {
                return Report(new SurfaceCheck(field, before, target, before, CheckResult.Unobservable, spec.Ownership,
                    $"The radio never answered '{command}'."));
            }

            if (!reply.Ok)
            {
                return Report(new SurfaceCheck(field, before, target, wire.State.Get(field), CheckResult.Refused,
                    spec.Ownership, $"The radio refused '{command}': {reply.Code} {reply.Message}."));
            }

            bool took = wire.WaitForValue(field, target, TimeSpan.FromSeconds(2));
            string? after = wire.State.Get(field);

            return Report(new SurfaceCheck(field, before, target, after,
                took ? CheckResult.Took : CheckResult.DidNotTake, spec.Ownership,
                took
                    ? ""
                    : $"Sent '{command}' and the radio accepted it, but the radio never reported the new value. " +
                      "Either it silently ignored the command or it does not report this field."));
        }

        private static SurfaceCheck RunFilterCheck(RigWire wire, int index, string low, string high)
        {
            Guards.RequireNotTransmitting(wire);

            var lowField = RigField.Slice(index, "filter_lo");
            var highField = RigField.Slice(index, "filter_hi");
            string? beforeLow = wire.State.Get(lowField);

            if (beforeLow is null)
            {
                return Report(new SurfaceCheck(lowField, null, low, null, CheckResult.Unobservable,
                    StateOwnership.ClientOwned, "The radio has not reported this slice's filter edges."));
            }

            string command = OwnershipTable.CompositeCommand(RigTarget.Slice, index, low, high);
            WireReply reply = wire.Send(command);
            if (!reply.Ok)
            {
                return Report(new SurfaceCheck(lowField, beforeLow, low, wire.State.Get(lowField), CheckResult.Refused,
                    StateOwnership.ClientOwned, $"The radio refused '{command}': {reply.Code} {reply.Message}."));
            }

            bool lowOk = wire.WaitForValue(lowField, low, TimeSpan.FromSeconds(2));
            bool highOk = wire.WaitForValue(highField, high, TimeSpan.FromSeconds(2));

            return Report(new SurfaceCheck(lowField, beforeLow, $"{low} and {high}",
                $"{wire.State.Get(lowField)} and {wire.State.Get(highField)}",
                lowOk && highOk ? CheckResult.Took : CheckResult.DidNotTake, StateOwnership.ClientOwned,
                lowOk && highOk
                    ? "Filter edges are written as one command, 'filt', not with slice set."
                    : "The filter pair was accepted but the radio did not report both edges back."));
        }

        private static SurfaceCheck Report(SurfaceCheck check)
        {
            string verdict = check.Result switch
            {
                CheckResult.Took => "took",
                CheckResult.DidNotTake => "DID NOT TAKE",
                CheckResult.Refused => "REFUSED BY THE RADIO",
                CheckResult.Unobservable => "not observable",
                _ => "skipped",
            };

            Console.WriteLine($"  {check.Field}: {check.Before ?? "(absent)"} -> {check.After ?? "(absent)"}  [{verdict}]");
            if (!string.IsNullOrEmpty(check.Detail)) Console.WriteLine("      " + check.Detail);
            return check;
        }

        private static void PrintCheckReport(List<SurfaceCheck> checks)
        {
            int took = checks.Count(c => c.Result == CheckResult.Took);
            int failed = checks.Count(c => c.Result is CheckResult.DidNotTake or CheckResult.Refused);
            int unobservable = checks.Count(c => c.Result == CheckResult.Unobservable);
            int skipped = checks.Count(c => c.Result == CheckResult.Skipped);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{checks.Count} checks: {took} took, {failed} did not, {unobservable} not observable, {skipped} skipped."));
            Console.WriteLine();

            int globalTook = checks.Count(c => c.Result == CheckResult.Took && c.Ownership == StateOwnership.StationGlobal);
            int clientTook = checks.Count(c => c.Result == CheckResult.Took && c.Ownership == StateOwnership.ClientOwned);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Of those that took, {globalTook} were station-global and prove the real behaviour, and " +
                $"{clientTook} were on our own slice and prove only that the radio accepts the command."));
            Console.WriteLine();
            Console.WriteLine("Nothing here says whether JJ FLEXIBLE can change any of it. That question needs a");
            Console.WriteLine("key pressed in the running application and this tool watching the application's");
            Console.WriteLine("own objects — 'surface mark', press, 'surface diff --owner <program>'.");

            if (unobservable > 0)
            {
                Console.WriteLine();
                Console.WriteLine("The not-observable ones are worth reading rather than skimming. A field the");
                Console.WriteLine("radio never reports cannot be asserted on at all, and treating that as a pass");
                Console.WriteLine("is exactly how a harness starts lying.");
            }
        }

        /// <summary>Moves a value somewhere else, whatever kind of value it is.</summary>
        private static string Nudge(string current)
        {
            if (int.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return (n == 0 ? 1 : n - 1).ToString(CultureInfo.InvariantCulture);
            }
            if (double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                return (d + 0.001).ToString("F6", CultureInfo.InvariantCulture);
            }
            return current + "X";
        }

        private static double ParseDouble(string? text, double fallback) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

        private sealed class MarkFile
        {
            public DateTimeOffset TakenAt { get; set; }
            public string? Host { get; set; }
            public string? ObserverHandle { get; set; }
            public Dictionary<string, string> Owners { get; set; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Clients { get; set; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
        }
    }
}
