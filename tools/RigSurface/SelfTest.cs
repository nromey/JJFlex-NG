using System;
using System.Collections.Generic;
using System.Linq;

namespace JJFlex.RigSurface
{
    /// <summary>
    /// Checks the parts that must be right before the tool is pointed at a
    /// radio, using no radio at all.
    ///
    /// <para>The transmit classifier is the safety boundary of this whole tool.
    /// It decides whether a command can put RF out of a radio with no antenna
    /// connected, and it is the one piece here that must not be wrong even once.
    /// Testing it needs no hardware, so there is no excuse for not testing
    /// it.</para>
    ///
    /// <para>The status parser earns its place here for a different reason: it
    /// has three delimiter and removal conventions that differ per topic, and
    /// getting one wrong produces a tool that is quietly incomplete rather than
    /// obviously broken.</para>
    /// </summary>
    internal static class SelfTest
    {
        private static int _checks;
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine("RigSurface self test. No radio is involved.");
            Console.WriteLine();

            TransmitClassifier();
            StatusParsing();
            HandleNormalising();
            OwnershipInvariants();

            Console.WriteLine();
            Console.WriteLine($"{_checks} checks, {_failures} failed.");
            return _failures;
        }

        // ---------------------------------------------------------------- //

        private static void TransmitClassifier()
        {
            Console.WriteLine("The transmit classifier — the safety boundary.");

            // Things that key. Getting any of these wrong transmits by accident.
            Keys("xmit 1");
            Keys("xmit 0");                    // still routed through the keying path
            Keys("transmit tune 1");
            Keys("transmit set tune=1");
            Keys("transmit set mox=1");
            Keys("cwx send hello");
            Keys("dvk play 1");
            Wears("atu start");

            // Things that do NOT key. Getting any of these wrong blocks routine
            // work and, worse, trains somebody to reach for an override.
            Silent("slice tune 0 14.250000");  // contains "tune" and is harmless
            Silent("transmit set tunepower=10");
            Silent("transmit set rfpower=1");
            Silent("transmit set mic_level=40");
            Silent("atu bypass");
            Silent("atu set memories_enabled=1");
            Silent("radio set tnf_enabled=1");
            Silent("sub slice all");
            Silent("filt 0 -2700 -300");
            Silent("slice set 0 mode=USB");

            // The way OUT of transmit must never be classified as dangerous by
            // anything that could refuse it on a budget.
            Check("unkeying is never blocked",
                TransmitGuard.Classify("transmit set mox=0") == CommandEffect.Silent);
            Check("stopping the tune carrier is never blocked",
                TransmitGuard.Classify("transmit set tune=0") == CommandEffect.Silent);
            Check("transmit tune 0 is never blocked",
                TransmitGuard.Classify("transmit tune 0") == CommandEffect.Silent);
        }

        private static void Keys(string command) =>
            Check($"'{command}' is recognised as keying",
                TransmitGuard.Classify(command) is CommandEffect.Keys or CommandEffect.KeysAndWearsRelays);

        private static void Wears(string command) =>
            Check($"'{command}' is recognised as keying AND wearing relays",
                TransmitGuard.Classify(command) == CommandEffect.KeysAndWearsRelays);

        private static void Silent(string command) =>
            Check($"'{command}' is recognised as harmless",
                TransmitGuard.Classify(command) == CommandEffect.Silent);

        // ---------------------------------------------------------------- //

        private static void StatusParsing()
        {
            Console.WriteLine();
            Console.WriteLine("Status parsing.");

            List<ParsedStatus> slice = StatusParser.Parse("slice 3 mode=USB RF_frequency=14.250000 client_handle=0x1234ABCD");
            Check("a slice line yields one object", slice.Count == 1);
            Check("the slice index is read", slice[0].Index == 3);
            Check("the slice mode is read", slice[0].Fields["mode"] == "USB");
            Check("the owning client is read", slice[0].Fields["client_handle"] == "0x1234ABCD");

            List<ParsedStatus> released = StatusParser.Parse("slice 3 in_use=0");
            Check("a slice reporting in_use=0 is treated as gone", released[0].Removed);

            List<ParsedStatus> stillHere = StatusParser.Parse("slice 3 in_use=1 mode=CW");
            Check("a slice reporting in_use=1 is not treated as gone", !stillHere[0].Removed);

            // Meter status is hash delimited. A space-delimited reading of this
            // line finds one meter and silently loses the second.
            List<ParsedStatus> meters = StatusParser.Parse("meter 1.src=SLC#1.num=0#1.nam=LEVEL#2.src=COD-#2.nam=MICPEAK#");
            Check("hash-delimited meter status yields both meters", meters.Count == 2);
            Check("the first meter's name is read",
                meters.Any(m => m.Index == 1 && m.Fields.GetValueOrDefault("nam") == "LEVEL"));
            Check("the second meter's source is read",
                meters.Any(m => m.Index == 2 && m.Fields.GetValueOrDefault("src") == "COD-"));

            List<ParsedStatus> client = StatusParser.Parse("client 0x1234ABCD connected client_id=abc program=SmartSDR station=Shack");
            Check("a connected client is recorded as connected", client[0].Fields["connected"] == "1");
            Check("the client's program is read", client[0].Fields["program"] == "SmartSDR");

            List<ParsedStatus> bye = StatusParser.Parse("client 0x1234ABCD disconnected forced=0");
            Check("a disconnected client is marked removed", bye[0].Removed);

            List<ParsedStatus> pan = StatusParser.Parse("display pan 0x40000000 band=20 rfgain=8 pre=+8dB");
            Check("panadapter status is recognised", pan.Count == 1 && pan[0].Target == RigTarget.Display);
            Check("band is read off the panadapter", pan[0].Fields["band"] == "20");
            Check("RF gain is read off the panadapter", pan[0].Fields["rfgain"] == "8");

            // Embedded spaces arrive as DEL. A parser that does not decode this
            // reports a station name run together and looks broken elsewhere.
            string encoded = "client 0x1 connected station=My" + (char)0x7F + "Shack";
            Check("an embedded space is decoded",
                StatusParser.Parse(encoded)[0].Fields["station"] == "My Shack");
            Check("an embedded space is re-encoded on the way out",
                StatusParser.EncodeValue("My Shack") == "My" + (char)0x7F + "Shack");

            List<ParsedStatus> unknown = StatusParser.Parse("somethingnew foo=1");
            Check("an unmodelled object is kept rather than dropped",
                unknown.Count == 1 && unknown[0].Fields.ContainsKey("somethingnew.foo"));
        }

        // ---------------------------------------------------------------- //

        private static void HandleNormalising()
        {
            Console.WriteLine();
            Console.WriteLine("Handle normalising.");

            // The banner arrives as bare hex and slice status spells the same
            // value with an 0x prefix. Comparing them raw is a silent way to
            // conclude that none of your own slices are yours.
            Check("a bare hex handle normalises", RigWire.NormaliseHandle("1A2B3C4D") == "0x1A2B3C4D");
            Check("a prefixed handle normalises", RigWire.NormaliseHandle("0x1a2b3c4d") == "0x1A2B3C4D");
            Check("the two forms agree",
                RigWire.NormaliseHandle("1A2B3C4D") == RigWire.NormaliseHandle("0x1A2B3C4D"));
        }

        // ---------------------------------------------------------------- //

        private static void OwnershipInvariants()
        {
            Console.WriteLine();
            Console.WriteLine("Ownership table invariants.");

            Check("nothing classified as telemetry is writable",
                OwnershipTable.All.Where(s => s.Ownership == StateOwnership.Telemetry).All(s => !s.Writable));

            Check("every writable field produces a command",
                OwnershipTable.All.Where(s => s.Writable).All(s =>
                    OwnershipTable.SetCommand(new RigField(s.Target, 0, s.StatusKey), "1") is { Length: > 0 }));

            Check("no writable field's command would key the radio",
                OwnershipTable.All.Where(s => s.Writable).All(s =>
                    TransmitGuard.Classify(OwnershipTable.SetCommand(new RigField(s.Target, 0, s.StatusKey), "1")!)
                        == CommandEffect.Silent));

            Check("an unknown field is never writable",
                !OwnershipTable.IsWritable(RigField.Radio("no_such_field_exists")));

            Check("an unknown field is classified unknown, not global",
                OwnershipTable.OwnershipOf(RigField.Radio("no_such_field_exists")) == StateOwnership.Unknown);

            // The vocabulary mismatches. Each of these has a status spelling and
            // a different set spelling, and each was found by reading the vendor
            // parser rather than by guessing.
            Vocabulary(RigField.Transmit("mic_level"), "miclevel");
            Vocabulary(RigField.Transmit("sb_monitor"), "mon=");
            Vocabulary(RigField.Transmit("am_carrier_level"), "am_carrier=");
            Vocabulary(RigField.Transmit("speed"), "cw wpm");
            Vocabulary(RigField.Transmit("mic_selection"), "mic input");
            Vocabulary(RigField.Radio("nickname"), "radio name");
            Vocabulary(RigField.Slice(0, "RF_frequency"), "slice tune");
            Vocabulary(RigField.Slice(0, "nrl"), "lms_nr=");

            Check("slice lock writes a verb rather than a value",
                OwnershipTable.SetCommand(RigField.Slice(2, "lock"), "1") == "slice lock 2"
                && OwnershipTable.SetCommand(RigField.Slice(2, "lock"), "0") == "slice unlock 2");

            Check("filter edges have no single-field write path",
                !OwnershipTable.IsWritable(RigField.Slice(0, "filter_lo"))
                && !OwnershipTable.IsWritable(RigField.Transmit("lo")));

            Check("filter edges have a composite write path",
                OwnershipTable.CompositeCommand(RigTarget.Slice, 0, "-2700", "-300") == "filt 0 -2700 -300");

            Check("panadapter writes address the object by handle",
                OwnershipTable.SetCommand(new RigField(RigTarget.Display, unchecked((int)0x40000000u), "band"), "20")
                    == "display pan set 0x40000000 band=20");

            Check("the slice rfgain trap is recorded as not writable",
                !OwnershipTable.IsWritable(RigField.Slice(0, "rfgain")));
        }

        private static void Vocabulary(RigField field, string expectedFragment)
        {
            string? command = OwnershipTable.SetCommand(field, "1");
            Check($"{field} writes with '{expectedFragment}'",
                command is not null && command.Contains(expectedFragment, StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------- //

        private static void Check(string what, bool passed)
        {
            _checks++;
            if (passed)
            {
                Console.WriteLine("  ok    " + what);
                return;
            }
            _failures++;
            Console.WriteLine("  FAIL  " + what);
        }
    }
}
