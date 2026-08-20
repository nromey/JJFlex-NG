using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JJFlex.RigSurface;

namespace JJFlex.TxFactAudit
{
    /// <summary>
    /// Audits the transmit-chain analyzer's FACTS against the radio itself.
    ///
    /// <para>The rules engine above these facts is well tested and provably
    /// honest about what it could not check. That guarantee is worth exactly
    /// nothing if a fact lies about its own readability, and nothing downstream
    /// can catch it: a zero that means "no reading" and a zero that means
    /// "actually zero" are the same bits.</para>
    ///
    /// <para><b>This tool is a second client and knows it.</b> It never claims
    /// a slice, never registers as a GUI station, and never asserts on an
    /// object it created. Client-owned facts are read from the APPLICATION's
    /// objects, attributed by client handle, or they are reported unverified.</para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "/?")
            {
                Usage();
                return args.Length == 0 ? 2 : 0;
            }

            string command = args[0].ToLowerInvariant();
            string[] rest = args.Skip(1).ToArray();

            try
            {
                return command switch
                {
                    "map" => Map(rest),
                    "concerns" => Concerns(),
                    "crosscheck" => CrossCheck(),
                    "power" => Power(rest),
                    "audit" => Audit(rest),
                    "fingerprint" => Fingerprint(rest),
                    _ => Unknown(command),
                };
            }
            catch (HarnessRefusedException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 3;
            }
            catch (TransmitRefusedException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 3;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Console.Error.WriteLine("Failed: " + ex.Message);
                return 1;
            }
        }

        private static int Unknown(string command)
        {
            Console.Error.WriteLine($"Unknown command '{command}'.");
            Usage();
            return 2;
        }

        private static void Usage()
        {
            Console.WriteLine("TxFactAudit — does each transmit-chain fact tell the truth about a live radio?");
            Console.WriteLine();
            Console.WriteLine("Needs no radio:");
            Console.WriteLine("  map [--fact NAME]   Every fact, traced from the rule name to the radio's own key.");
            Console.WriteLine("  concerns            Only the facts with a known or suspected wiring problem.");
            Console.WriteLine("  crosscheck          Check every wire key in the map against Track C's ownership table.");
            Console.WriteLine();
            Console.WriteLine("Reads the radio, changes nothing:");
            Console.WriteLine("  power [--host H]    The transmit power setting and interlock state, from the RADIO.");
            Console.WriteLine("                      Run this immediately before any keying. Never trust a cached copy.");
            Console.WriteLine("  audit [--host H] [--owner HANDLE]");
            Console.WriteLine("                      Every fact, with the radio's own answer beside it.");
            Console.WriteLine("                      --owner names the APPLICATION's client handle so client-owned");
            Console.WriteLine("                      facts are attributed to JJ Flexible rather than to this harness.");
            Console.WriteLine();
            Console.WriteLine("Changes station settings, then puts them back:");
            Console.WriteLine("  fingerprint [--host H] [--apply]");
            Console.WriteLine("                      Sets every settable transmit setting to a value chosen to be");
            Console.WriteLine("                      unmistakable, so ONE capture of the app's evidence block proves");
            Console.WriteLine("                      a dozen facts at once. Without --apply it only prints the plan.");
        }

        // ---------------------------------------------------------------- //
        // No radio needed
        // ---------------------------------------------------------------- //

        private static int Map(string[] args)
        {
            string? only = Option(args, "--fact");
            IEnumerable<FactSpec> facts = FactMap.All;
            if (only is not null)
            {
                FactSpec? one = FactMap.Find(only);
                if (one is null)
                {
                    Console.Error.WriteLine($"No fact named '{only}'.");
                    return 2;
                }
                facts = new[] { one };
            }

            Console.WriteLine($"{FactMap.All.Count} facts, in the order the analyzer states them (signal-path order).");
            Console.WriteLine();

            foreach (FactSpec f in facts)
            {
                Console.WriteLine(f.Name + " — " + f.Label);
                Console.WriteLine("  App reads: " + f.AppMember);
                Console.WriteLine("  FlexLib: " + f.LibMember);
                Console.WriteLine("  On the radio: " + WhereText(f));
                Console.WriteLine("  Whose state: " + OwnershipText(f.Ownership));
                Console.WriteLine("  When nothing has reported: " + IdleText(f));
                Console.WriteLine("  How to prove it: " + f.Proof);
                if (f.Concern.Length != 0) Console.WriteLine("  CONCERN: " + f.Concern);
                Console.WriteLine();
            }
            return 0;
        }

        private static int Concerns()
        {
            IReadOnlyList<FactSpec> fabricators = FactMap.Fabricators;
            IReadOnlyList<FactSpec> concerns = FactMap.WithConcerns;

            Console.WriteLine("FACTS THAT PUBLISH A MEASUREMENT WHEN NOTHING HAS BEEN MEASURED");
            Console.WriteLine();
            Console.WriteLine("These defeat the analyzer's central guarantee. The engine reports honestly");
            Console.WriteLine("about facts it could not read; a fact that claims to have been read cannot be");
            Console.WriteLine("caught by anything above it.");
            Console.WriteLine();
            if (fabricators.Count == 0)
            {
                Console.WriteLine("  None. Every fact says so when it has nothing.");
            }
            foreach (FactSpec f in fabricators)
            {
                Console.WriteLine($"  - {f.Name} reads \"{f.IdleReads}\" before anything has reported.");
                Console.WriteLine("      " + f.Concern);
            }

            Console.WriteLine();
            Console.WriteLine("CORRECTED IN THIS TRACK — recorded rather than deleted, so it cannot come back");
            Console.WriteLine();
            foreach (FactSpec f in FactMap.All.Where(c => c.FixedHere))
            {
                Console.WriteLine($"  - {f.Name}: {f.Concern}");
            }

            Console.WriteLine();
            Console.WriteLine("STILL OPEN — reported, not fixed");
            Console.WriteLine();
            foreach (FactSpec f in concerns.Where(c => !c.FixedHere))
            {
                Console.WriteLine($"  - {f.Name}: {f.Concern}");
            }

            Console.WriteLine();
            Console.WriteLine("CLIENT-OWNED FACTS — a second connection may READ these, but only with attribution");
            Console.WriteLine();
            foreach (FactSpec f in FactMap.ClientOwned)
            {
                Console.WriteLine($"  - {f.Name} ({f.Label}).");
            }
            Console.WriteLine();
            Console.WriteLine("  A harness that creates its own slice and reports its mode has proved nothing.");
            Console.WriteLine("  Pass --owner with the application's client handle so these read the app's objects.");
            return 0;
        }

        /// <summary>
        /// Every wire key this map names, checked against Track C's ownership
        /// table. A key that is not in that table is either a spelling this
        /// project has invented or a gap in the table — and both are worth a
        /// failing line rather than a comment nobody re-reads.
        /// </summary>
        private static int CrossCheck()
        {
            int unknown = 0, disagree = 0, checkedCount = 0;

            foreach (FactSpec f in FactMap.All)
            {
                if (f.Wire is not RigField field) continue;
                checkedCount++;

                RigFieldSpec spec = OwnershipTable.Lookup(field);
                if (spec.Ownership == StateOwnership.Unknown)
                {
                    Console.WriteLine($"UNKNOWN KEY: {f.Name} claims the radio spells it " +
                                      $"{field}, which is not in the ownership table.");
                    unknown++;
                    continue;
                }

                StateOwnership expected = f.Ownership switch
                {
                    FactOwnership.StationGlobal => StateOwnership.StationGlobal,
                    FactOwnership.ClientOwned => StateOwnership.ClientOwned,
                    FactOwnership.Telemetry => StateOwnership.Telemetry,
                    _ => StateOwnership.Unknown,
                };

                if (expected != StateOwnership.Unknown && expected != spec.Ownership)
                {
                    if (f.WhyOwnershipDiffers.Length != 0)
                    {
                        Console.WriteLine($"DECLARED DIVERGENCE: {f.Name} is {f.Ownership} here and " +
                                          $"{spec.Ownership} in the ownership table for {field}.");
                        Console.WriteLine("    " + f.WhyOwnershipDiffers);
                        continue;
                    }

                    Console.WriteLine($"DISAGREEMENT: {f.Name} is classified {f.Ownership} here " +
                                      $"but {spec.Ownership} in the ownership table for {field}.");
                    disagree++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{checkedCount} facts carry a wire key. " +
                              $"{unknown} name a key the ownership table does not know. " +
                              $"{disagree} disagree with it about ownership.");
            return unknown == 0 && disagree == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- //
        // Reads the radio
        // ---------------------------------------------------------------- //

        /// <summary>
        /// The pre-keying safety read. Deliberately its own command and
        /// deliberately short: the rule is that power is read back FROM THE
        /// RADIO immediately before any keying, and a rule that needs a
        /// twelve-step invocation is a rule that gets skipped.
        /// </summary>
        private static int Power(string[] args)
        {
            using RigWire wire = Open(args);

            string? rf = wire.State.Get(RigField.Transmit("rfpower"));
            string? tune = wire.State.Get(RigField.Transmit("tunepower"));
            string? maxPa = wire.State.Get(RigField.Transmit("max_internal_pa_power"));
            TransmitState state = Guards.ReadTransmitState(wire);

            Console.WriteLine();
            Console.WriteLine("Read from the radio just now, not from any cache:");
            Console.WriteLine("  Transmit power setting: " + (rf ?? "the radio has not reported it"));
            Console.WriteLine("  Tune power setting: " + (tune ?? "the radio has not reported it"));
            Console.WriteLine("  Internal PA ceiling: " + (maxPa ?? "the radio has not reported it"));
            Console.WriteLine("  Interlock state: " + state);
            Console.WriteLine();

            if (rf is null)
            {
                Console.WriteLine("REFUSE TO KEY. The radio has not reported its power setting, so there is");
                Console.WriteLine("nothing to compare against a ceiling. An unreported setting is not a low one.");
                return 3;
            }

            if (!int.TryParse(rf, NumberStyles.Integer, CultureInfo.InvariantCulture, out int watts))
            {
                Console.WriteLine("REFUSE TO KEY. The power setting did not read as a number.");
                return 3;
            }

            Console.WriteLine(watts <= 1
                ? $"Power reads {watts}. At or under the one watt authorised for this bench."
                : $"REFUSE TO KEY. Power reads {watts}, above the one watt authorised. Set it down first.");
            return watts <= 1 ? 0 : 3;
        }

        private static int Audit(string[] args)
        {
            string? owner = Option(args, "--owner");
            using RigWire wire = Open(args);

            Console.WriteLine();
            Console.WriteLine(Guards.DescribeCensus(wire));
            Console.WriteLine();

            string? appHandle = ResolveApplicationHandle(wire, owner);
            Console.WriteLine(appHandle is null
                ? "No application client identified. Every client-owned fact below reads UNVERIFIED, "
                + "because reading one from this connection would describe this harness."
                : "Client-owned facts are attributed to " + appHandle + ".");

            IReadOnlyList<RigObject> meters = wire.State.GetObjects(RigTarget.Meter);
            Dictionary<string, RigObject> byMeterName = MetersByName(meters);

            Console.WriteLine();
            Console.WriteLine($"The radio published {meters.Count} meter descriptors.");
            Console.WriteLine();

            int verified = 0, wrong = 0, unverified = 0;

            foreach (FactSpec f in FactMap.All)
            {
                Console.WriteLine(f.Name + " — " + f.Label);
                Verdict v = Check(f, wire, appHandle, byMeterName);
                Console.WriteLine("  " + v.Text);
                if (f.Concern.Length != 0) Console.WriteLine("  CONCERN: " + f.Concern);
                Console.WriteLine();

                switch (v.Kind)
                {
                    case VerdictKind.Verified: verified++; break;
                    case VerdictKind.Wrong: wrong++; break;
                    default: unverified++; break;
                }
            }

            Console.WriteLine($"{verified} verified against the radio, {wrong} verified WRONG, " +
                              $"{unverified} left unverified from this connection.");
            Console.WriteLine();
            Console.WriteLine("Unverified is not a failure of the audit. It is the honest state of a fact");
            Console.WriteLine("whose value arrives only as VITA-49 meter data or only inside the application.");
            return wrong == 0 ? 0 : 1;
        }

        private enum VerdictKind { Verified, Wrong, Unverified }

        private readonly record struct Verdict(VerdictKind Kind, string Text);

        private static Verdict Check(FactSpec f, RigWire wire, string? appHandle,
                                     Dictionary<string, RigObject> meters)
        {
            switch (f.Provenance)
            {
                case Provenance.AppLocal:
                    return new Verdict(VerdictKind.Unverified,
                        "Not radio state. Nothing on the wire can confirm or contradict it.");

                case Provenance.PcLocal:
                    return new Verdict(VerdictKind.Unverified,
                        "Windows audio state. The radio has never heard of it.");

                case Provenance.WireTopicNotParsed:
                    return new Verdict(VerdictKind.Unverified,
                        "The radio answers this on a topic this harness does not parse.");

                case Provenance.DiscoveryBeacon:
                    return new Verdict(VerdictKind.Unverified,
                        "Read from the discovery beacon at connect time, never re-sent on the command "
                        + "channel. Not comparable from here, and not live for the application either.");

                case Provenance.MeterValue:
                case Provenance.MeterDescriptor:
                    return CheckMeter(f, meters);

                case Provenance.WireField:
                    return CheckWireField(f, wire, appHandle);
            }
            return new Verdict(VerdictKind.Unverified, "No verification route.");
        }

        private static Verdict CheckMeter(FactSpec f, Dictionary<string, RigObject> meters)
        {
            if (f.Meter is null)
            {
                return new Verdict(VerdictKind.Unverified, "No meter named for this fact.");
            }

            if (!meters.TryGetValue(f.Meter, out RigObject? m))
            {
                // The distinction that matters: a meter the radio does not have
                // is a legitimate Absent. A meter the radio HAS under a
                // different spelling is a bug, and it reads identically.
                string near = NearestMeterName(f.Meter, meters.Keys);
                string extra = near.Length == 0
                    ? ""
                    : $" The radio DOES publish a meter called {near}, which is close enough that this " +
                      "is very likely a spelling the analyzer invented rather than a meter the radio lacks.";
                return new Verdict(near.Length == 0 ? VerdictKind.Unverified : VerdictKind.Wrong,
                    $"The radio publishes no meter named {f.Meter}." + extra);
            }

            m.Fields.TryGetValue("desc", out string? desc);
            m.Fields.TryGetValue("src", out string? src);
            m.Fields.TryGetValue("unit", out string? unit);
            m.Fields.TryGetValue("low", out string? low);
            m.Fields.TryGetValue("hi", out string? high);

            string identity = $"The radio publishes {f.Meter}: \"{desc ?? "no description"}\", " +
                              $"source {src ?? "unstated"}, units {unit ?? "unstated"}, " +
                              $"range {low ?? "?"} to {high ?? "?"}.";

            if (f.Provenance == Provenance.MeterDescriptor)
            {
                return new Verdict(VerdictKind.Verified, identity + " Identity confirmed.");
            }

            return new Verdict(VerdictKind.Unverified, identity +
                " Identity confirmed; the READING itself arrives only as VITA-49 meter data to a" +
                " client that registered a stream, which this harness deliberately does not do.");
        }

        private static Verdict CheckWireField(FactSpec f, RigWire wire, string? appHandle)
        {
            if (f.Wire is not RigField field)
            {
                return new Verdict(VerdictKind.Unverified, "No wire key recorded for this fact.");
            }

            if (f.Ownership == FactOwnership.ClientOwned)
            {
                return CheckClientOwned(f, wire, appHandle);
            }

            string? value = wire.State.Get(field);
            return value is null
                ? new Verdict(VerdictKind.Unverified,
                    $"The radio has not reported {field}. That is itself worth noting: the fact would " +
                    "still publish something.")
                : new Verdict(VerdictKind.Verified, $"The radio says {field} = {value}.");
        }

        /// <summary>
        /// A client-owned fact read the only honest way: find the object that
        /// belongs to the APPLICATION and report that one.
        ///
        /// <para>This is the trap the whole track turns on. Reading the transmit
        /// slice from this connection would describe this harness, come back
        /// plausible and consistent, and mean nothing about what the operator's
        /// analyzer will say.</para>
        /// </summary>
        private static Verdict CheckClientOwned(FactSpec f, RigWire wire, string? appHandle)
        {
            if (f.Name is "transmitting")
            {
                string? state = wire.State.Get(RigField.Interlock("state"));
                string? txHandle = wire.State.Get(RigField.Interlock("tx_client_handle"));
                if (state is null)
                {
                    return new Verdict(VerdictKind.Unverified,
                        "The radio has not reported an interlock state. Note there is no 'mox' key on " +
                        "the wire at all — anything waiting for one waits forever.");
                }

                TransmitState parsed = Guards.ReadTransmitStateFrom(state);
                string holder = txHandle is null ? "unstated" : txHandle;
                string mine = appHandle is not null && txHandle is not null
                    && string.Equals(RigWire.NormaliseHandle(txHandle), appHandle, StringComparison.OrdinalIgnoreCase)
                    ? "the application's"
                    : "NOT the application's";
                return new Verdict(VerdictKind.Verified,
                    $"Interlock state is {state} ({parsed}); the transmitting client handle is {holder}, " +
                    $"which is {mine}. The app's fact is true only when both hold, so it is a CLIENT " +
                    "fact wearing a station label.");
            }

            if (appHandle is null)
            {
                return new Verdict(VerdictKind.Unverified,
                    "Client-owned, and no application client was identified. Reading it from this " +
                    "connection would describe the harness.");
            }

            IReadOnlyList<RigObject> slices = wire.State.GetObjects(RigTarget.Slice);
            var appSlices = slices
                .Where(s => s.OwnerHandle is not null
                            && string.Equals(RigWire.NormaliseHandle(s.OwnerHandle), appHandle,
                                             StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (appSlices.Count == 0)
            {
                return new Verdict(VerdictKind.Unverified,
                    "The application owns no slice the radio has told us about.");
            }

            RigObject? txSlice = appSlices.FirstOrDefault(s =>
                s.Fields.TryGetValue("tx", out string? tx) && tx == "1");

            if (f.Name == "tx-slice")
            {
                return txSlice is null
                    ? new Verdict(VerdictKind.Unverified,
                        $"The application owns {appSlices.Count} slice(s), none flagged as the transmit slice.")
                    : new Verdict(VerdictKind.Verified,
                        $"The application's transmit slice is index {txSlice.Index}. The fact reports a " +
                        "LETTER, so what it prints must be this slice's letter and no other.");
            }

            if (f.Name == "tx-mode")
            {
                if (txSlice is null)
                {
                    return new Verdict(VerdictKind.Unverified,
                        "No transmit slice owned by the application, so there is no mode to compare.");
                }
                txSlice.Fields.TryGetValue("mode", out string? mode);
                return new Verdict(VerdictKind.Verified,
                    $"The application's transmit slice (index {txSlice.Index}) is in mode {mode ?? "unreported"}.");
            }

            return new Verdict(VerdictKind.Unverified, "No attribution route for this client-owned fact.");
        }

        // ---------------------------------------------------------------- //
        // Fingerprint
        // ---------------------------------------------------------------- //

        /// <summary>
        /// One change, one capture, a dozen facts proved.
        ///
        /// <para>Radio time is the scarce resource, not compute. Verifying a
        /// dozen settings one at a time means a dozen round trips through a
        /// dialog with a blind operator at the keyboard. Setting them all to
        /// values that could not possibly be a coincidence means ONE capture of
        /// the evidence block answers all of them, and any fact that did not
        /// move stands out on its own.</para>
        ///
        /// <para>Every value is chosen to be unmistakable and harmless: nothing
        /// here touches power, keying, antennas or profiles.</para>
        /// </summary>
        private static int Fingerprint(string[] args)
        {
            bool apply = args.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase));

            (RigField Field, string Value, string ExpectFact, string Expect)[] plan =
            {
                (RigField.Transmit("mic_level"), "37", "mic-gain", "37"),
                (RigField.Transmit("speech_processor_enable"), "1", "speech-processor", "yes"),
                (RigField.Transmit("speech_processor_level"), "2", "speech-processor-level", "DXX"),
                (RigField.Transmit("compander"), "0", "compander", "no"),
                (RigField.Transmit("sb_monitor"), "1", "tx-monitor", "yes"),
                (RigField.Transmit("lo"), "150", "tx-filter-low", "150 Hz"),
                (RigField.Transmit("hi"), "2850", "tx-filter-high", "2850 Hz"),
                (RigField.Transmit("mic_boost"), "0", "mic-boost", "no"),
                (RigField.Transmit("mic_bias"), "0", "mic-bias", "no"),
            };

            Console.WriteLine("Fingerprint plan. Each value is deliberately not a round number or a default,");
            Console.WriteLine("so a fact reading it back cannot be a coincidence and a fact that did NOT move");
            Console.WriteLine("is unmistakable.");
            Console.WriteLine();
            foreach (var step in plan)
            {
                Console.WriteLine($"  {step.Field} to {step.Value} — the evidence block should then read " +
                                  $"\"{FactMap.Find(step.ExpectFact)?.Label}: {step.Expect}\".");
            }
            Console.WriteLine();
            Console.WriteLine("Also expected, with no change needed: transmit filter width reads exactly 2700 Hz,");
            Console.WriteLine("which is the difference of the two edges and proves the derived fact separately.");
            Console.WriteLine();

            if (!apply)
            {
                Console.WriteLine("Plan only. Re-run with --apply to write these to the radio.");
                return 0;
            }

            using RigWire wire = Open(args);
            Guards.RequireNotTransmitting(wire);

            Console.WriteLine();
            Console.WriteLine(Guards.DescribeCensus(wire));
            Console.WriteLine();
            Console.WriteLine("These are STATION-GLOBAL settings. Changing them changes them for every");
            Console.WriteLine("connected operator, which is exactly why they are restored below.");
            Console.WriteLine();

            using var scope = RigStateScope.Capture(wire, new RigStateScopeOptions
            {
                Include = f => f.Target == RigTarget.Transmit,
                Report = Console.WriteLine,
            });

            foreach (var step in plan)
            {
                RigFieldSpec spec = OwnershipTable.Lookup(step.Field);
                if (!spec.Writable)
                {
                    Console.WriteLine($"  {step.Field}: no documented write path. Skipped rather than guessed at.");
                    continue;
                }

                string command = (spec.SetTemplate ?? "").Replace("{v}", step.Value).Replace("{i}", "");
                WireReply reply = wire.Send(command);
                bool stuck = wire.WaitForValue(step.Field, step.Value, TimeSpan.FromSeconds(2));
                Console.WriteLine($"  {step.Field} to {step.Value}: " +
                                  (stuck ? "the radio reported it back."
                                         : $"DID NOT STICK (reply {reply})."));
            }

            Console.WriteLine();
            Console.WriteLine("The radio is now fingerprinted. Capture the app's transmit-chain evidence block,");
            Console.WriteLine("then press Enter here to put every setting back.");
            Console.ReadLine();

            RestoreReport report = scope.Restore();
            Console.WriteLine();
            Console.WriteLine(report.ToPlainText());
            return report.Clean ? 0 : 1;
        }

        // ---------------------------------------------------------------- //
        // Plumbing
        // ---------------------------------------------------------------- //

        private static RigWire Open(string[] args)
        {
            string host = Option(args, "--host") ?? RigWire.DefaultHost;
            Console.WriteLine($"Connecting to {host}.");
            RigWire wire = RigWire.Connect(host);
            Console.WriteLine($"Connected. This harness is client {wire.ClientHandle}.");
            wire.SubscribeAll();
            wire.Settle(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
            return wire;
        }

        private static string? Option(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        /// <summary>
        /// Which connected client is JJ Flexible. An explicit handle wins; a
        /// single obvious match by program name is accepted and SAID OUT LOUD;
        /// anything ambiguous returns null rather than guessing, because a
        /// wrong attribution here produces confident nonsense.
        /// </summary>
        private static string? ResolveApplicationHandle(RigWire wire, string? explicitHandle)
        {
            if (explicitHandle is not null) return RigWire.NormaliseHandle(explicitHandle);

            ConnectedClient? found = Guards.FindClientByProgram(wire, "JJFlex");
            if (found is not null)
            {
                Console.WriteLine("Identified the application by its program name: " + found.Describe());
                return RigWire.NormaliseHandle(found.Handle);
            }

            IReadOnlyList<ConnectedClient> others = Guards.OtherOperators(wire);
            if (others.Count == 1)
            {
                Console.WriteLine("Only one other client is connected, so it is taken to be the application: "
                                  + others[0].Describe());
                return RigWire.NormaliseHandle(others[0].Handle);
            }

            return null;
        }

        private static Dictionary<string, RigObject> MetersByName(IReadOnlyList<RigObject> meters)
        {
            var map = new Dictionary<string, RigObject>(StringComparer.OrdinalIgnoreCase);
            foreach (RigObject m in meters)
            {
                if (m.Fields.TryGetValue("nam", out string? name) && name is not null && !map.ContainsKey(name))
                {
                    map[name] = m;
                }
            }
            return map;
        }

        /// <summary>
        /// A meter name close enough to the one we asked for that the miss is
        /// almost certainly our spelling rather than the radio's absence. This
        /// is what turns "your radio has no such meter" — a false statement
        /// about the operator's radio — into a finding.
        /// </summary>
        private static string NearestMeterName(string wanted, IEnumerable<string> have)
        {
            foreach (string name in have)
            {
                if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return "";
                if (name.Length != wanted.Length) continue;

                int differences = 0;
                for (int i = 0; i < name.Length; i++)
                {
                    if (char.ToUpperInvariant(name[i]) != char.ToUpperInvariant(wanted[i])) differences++;
                }
                if (differences == 1) return name;
            }
            return "";
        }

        private static string WhereText(FactSpec f) => f.Provenance switch
        {
            Provenance.WireField => "status key " + f.Wire,
            Provenance.MeterDescriptor => "meter " + f.Meter + " (descriptor readable on the command channel)",
            Provenance.MeterValue => "meter " + f.Meter + " (reading arrives only as VITA-49 data)",
            Provenance.AppLocal => "nowhere — this lives in JJ Flexible",
            Provenance.PcLocal => "nowhere — this is Windows audio state",
            Provenance.WireTopicNotParsed => "a status topic this harness does not parse",
            Provenance.DiscoveryBeacon => "the discovery beacon, not the command channel — a connect-time value",
            _ => "unknown",
        };

        private static string OwnershipText(FactOwnership o) => o switch
        {
            FactOwnership.StationGlobal => "station-global. One value for the whole radio; reading it from any connection is honest.",
            FactOwnership.ClientOwned => "client-owned. Globally observable, privately owned — honest only with attribution.",
            FactOwnership.Telemetry => "telemetry. The radio reporting on itself.",
            _ => "not radio state.",
        };

        private static string IdleText(FactSpec f) => f.Idle switch
        {
            IdleHonesty.NoIdleState => "there is no idle state — a setting the radio holds is current by definition.",
            IdleHonesty.Gated => "says so. Reports as unreadable or silent rather than inventing a value.",
            IdleHonesty.Fabricates => $"reads \"{f.IdleReads}\", which is indistinguishable from a measurement.",
            _ => "unknown",
        };
    }
}
