using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JJFlex.RigSurface
{
    /// <summary>
    /// The command line for the radio-surface harness.
    ///
    /// <para>Two families of subcommand, and the distinction is the whole
    /// architecture. <b>Observe</b> commands change nothing and are safe to run
    /// while Noel is operating and while JJ Flexible is connected — that is in
    /// fact the arrangement they exist for. <b>Exercise</b> commands change
    /// station state and refuse to run while anyone else is connected.</para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            string command = args[0].ToLowerInvariant();
            string[] rest = args.Skip(1).ToArray();

            try
            {
                return command switch
                {
                    "ownership" => Ownership(rest),
                    "census" => Census(rest),
                    "observe" => Observe(rest),
                    "meters" => Meters(rest),
                    "snapshot" => Snapshot(rest),
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

        private static bool IsHelp(string arg) =>
            arg is "-h" or "--help" or "/?" or "help";

        private static int Unknown(string command)
        {
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintUsage();
            return 2;
        }

        internal static string HostFrom(string[] args)
        {
            string? explicitHost = Option(args, "--host");
            if (explicitHost is not null) return explicitHost;

            string? positional = args.FirstOrDefault(a =>
                !a.StartsWith('-') && a.Count(c => c == '.') == 3);
            return positional ?? RigWire.DefaultHost;
        }

        internal static string? Option(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        internal static bool Flag(string[] args, string name) =>
            args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        internal static RigWire Open(string[] args)
        {
            string host = HostFrom(args);
            Console.WriteLine($"Connecting to {host}.");
            RigWire wire = RigWire.Connect(host);
            Console.WriteLine($"Connected. Our client handle is {wire.ClientHandle}, protocol version {wire.Version}.");
            wire.SubscribeAll();
            wire.Settle(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
            return wire;
        }

        // ---------------------------------------------------------------- //

        /// <summary>Prints the ownership table. Needs no radio.</summary>
        private static int Ownership(string[] args)
        {
            string? filter = Option(args, "--target");
            bool writableOnly = Flag(args, "--writable");

            IEnumerable<RigFieldSpec> rows = OwnershipTable.All;
            if (filter is not null)
            {
                rows = rows.Where(r => string.Equals(r.Target.ToString(), filter, StringComparison.OrdinalIgnoreCase));
            }
            if (writableOnly) rows = rows.Where(r => r.Writable);

            foreach (IGrouping<StateOwnership, RigFieldSpec> group in rows.GroupBy(r => r.Ownership).OrderBy(g => g.Key))
            {
                Console.WriteLine();
                Console.WriteLine(Headline(group.Key));
                foreach (RigFieldSpec spec in group.OrderBy(r => r.Target).ThenBy(r => r.StatusKey, StringComparer.Ordinal))
                {
                    string write = spec.Writable
                        ? (spec.SetTemplate ?? "written by a special-cased command")
                        : "no write path";
                    Console.WriteLine($"  - {spec.Target.ToString().ToLowerInvariant()}.{spec.StatusKey} " +
                                      $"[{spec.Confidence}] — {write}.");
                    if (!string.IsNullOrEmpty(spec.Notes)) Console.WriteLine($"      {spec.Notes}");
                }
            }
            return 0;
        }

        private static string Headline(StateOwnership ownership) => ownership switch
        {
            StateOwnership.StationGlobal =>
                "STATION-GLOBAL. One value for the whole radio. Reading it from any connection is honest; " +
                "writing it changes it for every connected operator.",
            StateOwnership.ClientOwned =>
                "CLIENT-OWNED. Lives on an object with a client handle. Globally OBSERVABLE, privately OWNED: " +
                "any client may read it, only the owner should write it. Verifying the application's copy is honest; " +
                "creating our own object and asserting on that proves nothing.",
            StateOwnership.Telemetry =>
                "TELEMETRY. The radio reporting on itself. Never written, never restored, only observed.",
            _ => "UNCLASSIFIED. Never written.",
        };

        /// <summary>Who is connected. Read-only.</summary>
        private static int Census(string[] args)
        {
            using RigWire wire = Open(args);
            Console.WriteLine();
            Console.WriteLine(Guards.DescribeCensus(wire));
            Console.WriteLine();
            Console.WriteLine("Transmit state: " + Guards.ReadTransmitState(wire));
            string? tx = Guards.TransmittingClientHandle(wire);
            if (tx is not null) Console.WriteLine("Transmitter currently held by client " + tx + ".");
            return 0;
        }

        /// <summary>Dumps everything the radio has said. Read-only.</summary>
        private static int Observe(string[] args)
        {
            using RigWire wire = Open(args);
            string? only = Option(args, "--target");

            Console.WriteLine();
            Console.WriteLine(Guards.DescribeCensus(wire));

            foreach (RigObject obj in wire.State.AllObjects())
            {
                if (only is not null
                    && !string.Equals(obj.Target.ToString(), only, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Console.WriteLine();
                string ours = obj.OwnerHandle is null
                    ? ""
                    : Guards.IsOurs(wire, obj) ? "  (ours)" : "  (another client's)";
                Console.WriteLine(obj.Describe() + ours);

                foreach (KeyValuePair<string, string> kv in obj.Fields.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"  {kv.Key} = {kv.Value}");
                }
            }
            return 0;
        }

        /// <summary>
        /// The meter inventory, with its SOURCE attribution.
        ///
        /// <para>This exists because a fact wired to the wrong meter reads
        /// perfectly plausibly and produces a confident wrong answer. Printing
        /// each meter's source alongside its name is what makes "this number
        /// comes from the analog microphone's converter, not from the PC audio
        /// path" visible instead of inferred.</para>
        /// </summary>
        private static int Meters(string[] args)
        {
            using RigWire wire = Open(args);

            IReadOnlyList<RigObject> meters = wire.State.GetObjects(RigTarget.Meter);
            if (meters.Count == 0)
            {
                Console.WriteLine("The radio reported no meter descriptors.");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine($"{meters.Count} meters, as the radio describes them:");
            Console.WriteLine();

            foreach (RigObject meter in meters)
            {
                meter.Fields.TryGetValue("nam", out string? name);
                meter.Fields.TryGetValue("src", out string? src);
                meter.Fields.TryGetValue("num", out string? num);
                meter.Fields.TryGetValue("unit", out string? unit);
                meter.Fields.TryGetValue("low", out string? low);
                meter.Fields.TryGetValue("hi", out string? high);
                meter.Fields.TryGetValue("desc", out string? desc);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {meter.Index}: {name ?? "(unnamed)"} from source {src ?? "?"} index {num ?? "?"}, " +
                    $"{unit ?? "no unit"}, range {low ?? "?"} to {high ?? "?"}."));
                if (!string.IsNullOrEmpty(desc)) Console.WriteLine("      " + desc);
            }

            Console.WriteLine();
            Console.WriteLine("Only descriptors arrive over the command channel. The readings themselves travel " +
                              "over UDP as VITA-49, so this inventory tells you what each meter MEANS but not what " +
                              "it currently says.");
            return 0;
        }

        /// <summary>
        /// Proves the snapshot-and-restore path end to end on the safest field
        /// there is, then puts it back.
        ///
        /// <para>The radio's nickname is station-global, purely cosmetic, and
        /// instantly reversible. If restore cannot put THAT back, nothing else in
        /// this harness should be trusted with anything that matters.</para>
        /// </summary>
        private static int Snapshot(string[] args)
        {
            using RigWire wire = Open(args);

            if (Flag(args, "--prove"))
            {
                Guards.RequireSoleOperator(wire);
                Guards.RequireNotTransmitting(wire);
            }

            using var scope = RigStateScope.Capture(wire);
            Console.WriteLine();
            Console.WriteLine($"Snapshot holds {scope.Before.Count} fields across " +
                              $"{scope.Before.Keys.Select(f => (f.Target, f.Index)).Distinct().Count()} objects.");

            if (!Flag(args, "--prove"))
            {
                Console.WriteLine("Nothing was changed. Pass --prove to exercise the restore path against " +
                                  "the radio's nickname, which is cosmetic and instantly reversible.");
                return 0;
            }

            var field = RigField.Radio("nickname");
            string? before = wire.State.Get(field);
            Console.WriteLine($"Radio nickname is currently '{before ?? "(not reported)"}'.");

            const string probe = "RigSurfaceProbe";
            string? command = OwnershipTable.SetCommand(field, probe);
            if (command is null)
            {
                Console.Error.WriteLine("No write path for the nickname. Cannot prove the restore path.");
                return 1;
            }

            WireReply reply = wire.Send(command);
            Console.WriteLine($"Sent '{command}': {reply}");

            bool took = wire.WaitForValue(field, probe, TimeSpan.FromSeconds(3));
            Console.WriteLine(took
                ? "The radio confirmed the new nickname, so the write path works."
                : "The radio never reported the new nickname back. The write did not take.");

            RestoreReport report = scope.Restore();
            Console.WriteLine();
            Console.WriteLine(report.ToPlainText());
            return report.Clean ? 0 : 1;
        }

        // ---------------------------------------------------------------- //

        private static void PrintUsage()
        {
            Console.WriteLine("RigSurface — the radio-surface harness for JJ Flexible.");
            Console.WriteLine();
            Console.WriteLine("It observes the radio over the raw command channel rather than through the");
            Console.WriteLine("application's library, so that what it reports is what the RADIO says and not");
            Console.WriteLine("what our own cache believes.");
            Console.WriteLine();
            Console.WriteLine("Commands that change nothing and are safe while the radio is in use:");
            Console.WriteLine("  ownership [--target <kind>] [--writable]");
            Console.WriteLine("      Print the station-global versus client-owned classification. No radio needed.");
            Console.WriteLine("  census [--host <ip>]");
            Console.WriteLine("      Who is connected, and whether the radio is transmitting.");
            Console.WriteLine("  observe [--host <ip>] [--target <kind>]");
            Console.WriteLine("      Dump every object the radio reports, attributed to its owning client.");
            Console.WriteLine("  meters [--host <ip>]");
            Console.WriteLine("      The meter inventory with each meter's source, so a fact wired to the wrong");
            Console.WriteLine("      instrument is visible rather than inferred.");
            Console.WriteLine("  snapshot [--host <ip>]");
            Console.WriteLine("      Capture the radio's state and report its size. Add --prove to exercise the");
            Console.WriteLine("      restore path against the radio's nickname and put it back.");
            Console.WriteLine();
            Console.WriteLine("No antenna is connected to the bench radio.");
        }
    }
}
